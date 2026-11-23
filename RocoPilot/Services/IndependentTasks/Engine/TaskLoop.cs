using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services.IndependentTasks.Engine;

/// <summary>
/// 按步骤推进的任务循环，日常任务共用。负责各任务原先各写一遍的部分：
/// <list type="bullet">
/// <item>取帧：窗口关闭时报错，失去前台时暂停计时并等待，输入后等画面更新，静止界面按时接受旧帧；</item>
/// <item>节奏：看到目标后按反应时间出手，之后按轮询间隔复查，不用固定的长等待；</item>
/// <item>补发：进入新步骤后迟迟等不到预期画面，就回到上一步用当前画面重新判断，
/// 目标还在说明输入没生效，再发一次；目标已经不在则只是画面慢，继续等；</item>
/// <item>超时：一步里的前台时间用完后抛出 <see cref="TaskStalledException"/>，由调度层退回大世界重跑。</item>
/// </list>
/// 任务本身只描述“在某一步看到什么就做什么”，每次都依据当前画面，不依据上一次发过什么。
/// </summary>
internal sealed class TaskLoop<TStep> where TStep : struct, Enum
{
    private readonly string _task;
    private readonly RuntimeTaskState _state;
    private readonly IScreenCaptureService _capture;
    private readonly IKeyboardInputService _keyboard;
    private readonly IMouseInputService _mouse;
    private readonly Action<IndependentTaskProgress> _progress;
    private readonly Func<TStep, string> _describe;
    private readonly CancellationToken _token;
    private readonly ILogger? _logger;
    private readonly Stopwatch _stepTimer = Stopwatch.StartNew();
    private readonly Stopwatch _sinceInput = new();

    private DateTimeOffset _freshAfter = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFrameAt = DateTimeOffset.MinValue;
    private int _settleMs = TaskPacing.SettleMs;
    private bool _lastInputWasPointer;
    private int _inputs;
    private int _inputsAtStepStart;
    private int _inputFailures;
    private string? _stageLabel;
    private bool _paused;
    private bool _retrying;
    // 当前步骤是由哪一步的输入带过来的；没有输入就推进的步骤无可补发。
    private (TStep From, int Retries)? _origin;

    public TaskLoop(string task, RuntimeTaskState state, IScreenCaptureService capture, IKeyboardInputService keyboard,
        IMouseInputService mouse, Action<IndependentTaskProgress> progress, Func<TStep, string> describe,
        TStep initial, CancellationToken token, ILogger? logger = null)
    {
        _task = task;
        _state = state;
        _capture = capture;
        _keyboard = keyboard;
        _mouse = mouse;
        _progress = progress;
        _describe = describe;
        _token = token;
        _logger = logger;
        Step = initial;
        Status = describe(initial);
        keyboard.EnsureReady(Method);
        progress(new(task, Status));
    }

    public TStep Step { get; private set; }

    /// <summary>显示给用户的当前步骤，也用在超时原因里。</summary>
    public string Status { get; private set; }

    /// <summary>当前步骤已用的前台时间；失去前台期间不计。</summary>
    public TimeSpan StepElapsed => _stepTimer.Elapsed;

    /// <summary>每一步允许的前台时间。</summary>
    public Func<TStep, TimeSpan> Timeout { get; init; } = _ => TimeSpan.FromSeconds(30);

    /// <summary>每一步的识别间隔；长时间观察的步骤可以放慢。</summary>
    public Func<TStep, int> Poll { get; init; } = _ => TaskPacing.PollMs;

    /// <summary>
    /// 这一步是否每帧只处理一次。靠“连续几帧都如此”做判断的步骤（大世界里等提示消失）需要，
    /// 否则同一张画面会被数好几遍；静止界面上的步骤不要开，否则漏点后等不到新帧。
    /// </summary>
    public Func<TStep, bool> DistinctFrames { get; init; } = _ => false;

    /// <summary>失去前台时调用，用来松开按住的键。</summary>
    public Func<Task>? Paused { get; init; }

    /// <summary>不可逆操作完成后记下结果；之后即使卡在退出界面，也不会重做。</summary>
    public string? CommittedOutcome { get; set; }

    public nint Hwnd => _state.TargetWindow.Hwnd;

    public KeyboardInputMethod Method => _state.Options.AutoBattleSettings.KeyboardInputMethod;

    public bool IsForeground => _keyboard.IsWindowForeground(Hwnd);

    /// <summary>进入下一步并重新计时。</summary>
    /// <param name="retryable">
    /// 本次输入没生效时能否照原样再发一次。再发会重复计数或重复消耗的输入传 false，由任务自己处理。
    /// </param>
    public void Advance(TStep next, string? status = null, bool retryable = true, string? stageLabel = null)
    {
        if (_retrying)
        {
            // 补发只是把上一步重做一遍，不重置计时，也不改变“从哪一步来”的记录。
            Step = next;
            return;
        }

        _origin = retryable && _inputs != _inputsAtStepStart ? (Step, 0) : null;
        Step = next;
        _stepTimer.Restart();
        _stageLabel = stageLabel;
        Report(status ?? _describe(next));
    }

    public void Report(string status)
    {
        Status = status;
        _progress(new(_stageLabel ?? _task, status));
    }

    /// <summary>步骤内有实际进展（又种了一格、又点了一朵）时调用，重新计超时。</summary>
    public void KeepAlive() => _stepTimer.Restart();

    /// <summary>
    /// 取下一帧用于识别。返回 null 表示这一轮没有可用画面（暂停中、截图失败、画面尚未更新），调用方直接进入下一轮。
    /// </summary>
    public async Task<CapturedFrame?> NextFrameAsync()
    {
        await Task.Delay(_paused ? 300 : Poll(Step), _token);
        if (!_keyboard.IsWindowAvailable(Hwnd)) throw new InvalidOperationException("游戏窗口已关闭。");
        if (!IsForeground)
        {
            if (!_paused)
            {
                _paused = true;
                _stepTimer.Stop();
                if (Paused is not null) await Paused();
                _progress(new(_task, "已暂停，等待切回游戏"));
                _logger?.LogInformation("{Task}：游戏窗口不在前台，已暂停，切回游戏后继续", _task);
            }

            return null;
        }

        if (_paused)
        {
            _paused = false;
            _stepTimer.Start();
            // 切回来的过程中画面会变，重新等一帧；补发的等待也从现在算起。
            RequireFreshFrame();
            Report(Status);
        }

        if (_stepTimer.Elapsed > Timeout(Step))
            throw new TaskStalledException($"{_task}等待超时：{Status}。") { CommittedOutcome = CommittedOutcome };

        if (_sinceInput.IsRunning && _sinceInput.ElapsedMilliseconds < _settleMs)
            await Task.Delay(_settleMs - (int)_sinceInput.ElapsedMilliseconds, _token);

        var frame = await Task.Run(() => _capture.Capture(_state.TargetWindow, _state.Options.CaptureMethod), _token);
        if (frame is null) return null;
        var stale = frame.CapturedAt <= _freshAfter;
        if ((stale && (!_sinceInput.IsRunning || _sinceInput.ElapsedMilliseconds < TaskPacing.StaleFrameAfterMs))
            || (DistinctFrames(Step) && frame.CapturedAt <= _lastFrameAt))
        {
            frame.Dispose();
            return null;
        }

        _lastFrameAt = frame.CapturedAt;
        return frame;
    }

    /// <summary>
    /// 执行当前步骤的处理。处理返回非 null 表示任务结束。
    /// 本轮既没有输入也没有换步骤、且上一步的输入已经等够时间，就回到上一步用同一帧重新判断一次。
    /// </summary>
    public async Task<string?> RunStepAsync(Func<Task<string?>> handle)
    {
        _inputsAtStepStart = _inputs;
        var step = Step;
        var result = await handle();
        if (result is not null || !Step.Equals(step) || _inputs != _inputsAtStepStart) return result;
        if (_origin is not { } origin || origin.Retries >= TaskPacing.MaxStepRetries
            || _sinceInput.Elapsed < TaskPacing.RetryDelay(_lastInputWasPointer, origin.Retries)) return null;

        _retrying = true;
        try
        {
            Step = origin.From;
            result = await handle();
        }
        finally
        {
            _retrying = false;
        }

        if (result is not null) return result;
        if (_inputs == _inputsAtStepStart)
        {
            // 上一步的目标已经不在了：输入是生效的，只是下一个界面还没出来。
            Step = step;
            return null;
        }

        _origin = (origin.From, origin.Retries + 1);
        if (Step.Equals(origin.From)) Step = step;
        _logger?.LogWarning("{Task}：上一步的输入没有生效，已重新操作（第 {Retry} 次），当前步骤 {Status}",
            _task, origin.Retries + 1, Status);
        return null;
    }

    /// <param name="settleMs">这次按键之后等多久再识别；角色移动、动画较长的操作可以指定。</param>
    public async Task<bool> KeyAsync(string key, int? holdMs = null, bool react = true, int? settleMs = null)
    {
        var sent = await SendAsync(false, react, $"按键 {key}", () => _keyboard.SendSequenceAsync(Hwnd, key, new()
        {
            Method = Method,
            HoldDurationMs = holdMs ?? _state.Options.AutoBattleSettings.KeyboardHoldDurationMs
        }, _token));
        if (sent && settleMs is { } settle) _settleMs = settle;
        return sent;
    }

    public Task<bool> ClickAsync(int x, int y, bool react = true)
        => SendAsync(true, react, $"点击 ({x},{y})", () => _mouse.ClickAsync(Hwnd, x, y, Method, _token));

    public Task<bool> ClickAsync(DailyTaskScreen screen, ImageMatchResult match, double dx = 0, double dy = 0, bool react = true)
        => ClickAsync(screen.ClientX(match.X + match.Width / 2d + dx * screen.Scale),
            screen.ClientY(match.Y + match.Height / 2d + dy * screen.Scale), react);

    /// <summary>
    /// 同一个界面上需要反复按的键（Esc 逐层退出、空格推进对话）：距上一次输入不足间隔时不发，
    /// 给界面留出响应时间，避免多按一下把刚到的界面又关掉。
    /// </summary>
    public async Task<bool> RepeatKeyAsync(string key, int intervalMs = TaskPacing.RepeatKeyMs)
        => InputIdleFor(intervalMs) && await KeyAsync(key, react: false);

    /// <summary>距上一次输入是否已经过了指定时间；用于不换步骤、需要等界面展开的重复点击。</summary>
    public bool InputIdleFor(int ms) => !_sinceInput.IsRunning || _sinceInput.ElapsedMilliseconds >= ms;

    public Task<bool> DragAsync(int x, int y, int endX, int endY)
        => SendAsync(true, true, $"拖动 ({x},{y})", () => _mouse.DragAsync(Hwnd, x, y, endX, endY, Method, _token));

    public Task<bool> ScrollAsync(int x, int y, int delta)
        => SendAsync(true, false, $"滚轮 {delta}", () => _mouse.ScrollAsync(Hwnd, x, y, delta, Method, _token));

    /// <summary>任务自己发了输入（按住行走、长按鼠标）时调用，让下一帧等画面更新。</summary>
    public void NoteInput(bool pointer = true, int settleMs = TaskPacing.SettleMs)
    {
        _settleMs = settleMs;
        _inputs++;
        _inputFailures = 0;
        _lastInputWasPointer = pointer;
        _sinceInput.Restart();
        _freshAfter = DateTimeOffset.UtcNow;
    }

    /// <summary>不发输入，只要求下一帧晚于现在（例如刚松开行走键）。</summary>
    public void RequireFreshFrame()
    {
        _settleMs = TaskPacing.SettleMs;
        _sinceInput.Restart();
        _freshAfter = DateTimeOffset.UtcNow;
    }

    private async Task<bool> SendAsync(bool pointer, bool react, string input, Func<Task> send)
    {
        if (react) await Task.Delay(TaskPacing.ReactionMs, _token);
        // 不在前台时不发；下一轮取帧会进入暂停。
        if (!IsForeground) return false;
        try
        {
            await send();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ArgumentException
            && !_token.IsCancellationRequested)
        {
            // 鼠标被别的窗口挡住、光标没移到位、驱动一时没响应：画面没变，下一轮会按当前画面重新决定。
            if (++_inputFailures >= TaskPacing.MaxInputFailures)
                throw new TaskStalledException($"{_task}输入连续失败：{ex.Message}", ex) { CommittedOutcome = CommittedOutcome };
            _logger?.LogWarning(ex, "{Task}：{Input}没有发出去，稍后重试（连续 {Count} 次）", _task, input, _inputFailures);
            await Task.Delay(TaskPacing.SettleMs, _token);
            return false;
        }

        NoteInput(pointer);
        return true;
    }
}
