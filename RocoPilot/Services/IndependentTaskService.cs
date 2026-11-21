using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Capture;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Runtime;
using RocoPilot.Notifications;
using RocoPilot.Services.IndependentTasks;
using RocoPilot.Services.IndependentTasks.Engine;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Services;

/// <summary>
/// 独立任务调度：花种挑战、传说精灵挑战等限次任务按需启动，跑完即止。
/// 独立任务基于启动页建立的运行会话工作：启动前需实时任务处于运行状态，
/// 运行期间自动挂起实时识别循环并复用其捕获会话，结束后自动恢复。
/// </summary>
public sealed class IndependentTaskService : IIndependentTaskService
{
    private readonly IRuntimeSessionControl _runtimeTaskService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly BattlePassRunner _battlePass;
    private readonly MailRunner _mail;
    private readonly CropSaleRunner _cropSale;
    private readonly GardeningRunner _gardening;
    private readonly EveningRestRunner _eveningRest;
    private readonly StaminaCraftingRunner _staminaCrafting;
    private readonly IManualChallengeRunner _challengeRunner;
    private readonly ThrowBallRunner _throwBall;
    private readonly IAppNotificationService _notifications;
    private readonly IGameWindowService _gameWindowService;
    private readonly ILogger<IndependentTaskService> _logger;
    private readonly RecognitionDiagnostics? _recognitionDiagnostics;
    private readonly IScreenCaptureService? _screenCapture;
    private readonly NotificationScreenshotStore? _screenshots;
    private readonly WorldRecovery? _worldRecovery;

    /// <summary>任务卡住后，退回大世界重跑的次数。</summary>
    private const int MaximumTaskRetries = 1;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _settingsSaveLock = new(1, 1);
    private IndependentTaskSettings _settings = IndependentTaskSettings.CreateDefault();
    private IReadOnlyList<string>? _legendaryTargets;
    private string? _savedLegendaryTarget;
    private bool _hasLoadedSettings;
    private IndependentTaskKind? _runningTaskKind;
    private CancellationTokenSource? _taskCts;
    private Task? _taskLoop;

    public event EventHandler? StateChanged;

    public bool IsRunning => _runningTaskKind is not null;

    public bool IsDailyRunning { get; private set; }

    public string StatusMessage { get; private set; } = "就绪";

    public IndependentTaskKind? RunningTaskKind => _runningTaskKind;

    public IndependentTaskSettings Settings => _settings.Clone();

    public IReadOnlyList<string> LegendaryTargets => _legendaryTargets ?? IndependentTaskSettings.LegendaryTargets;

    public IndependentTaskService(
        IRuntimeSessionControl runtimeTaskService,
        ILocalSettingsService localSettingsService,
        IManualChallengeRunner challengeRunner,
        IAppNotificationService notifications,
        IGameWindowService gameWindowService,
        StaminaCraftingRunner staminaCrafting, EveningRestRunner eveningRest, MailRunner mail, GardeningRunner gardening, CropSaleRunner cropSale, BattlePassRunner battlePass,
        ThrowBallRunner throwBall,
        ILogger<IndependentTaskService> logger,
        RecognitionDiagnostics? recognitionDiagnostics = null,
        IScreenCaptureService? screenCapture = null,
        NotificationScreenshotStore? screenshots = null,
        WorldRecovery? worldRecovery = null)
    {
        _worldRecovery = worldRecovery;
        _recognitionDiagnostics = recognitionDiagnostics;
        _screenCapture = screenCapture;
        _screenshots = screenshots;
        _runtimeTaskService = runtimeTaskService;
        _localSettingsService = localSettingsService;
        _challengeRunner = challengeRunner;
        _throwBall = throwBall;
        _notifications = notifications;
        _gameWindowService = gameWindowService;
        _staminaCrafting = staminaCrafting;
        _eveningRest = eveningRest;
        _mail = mail;
        _battlePass = battlePass;
        _gardening = gardening;
        _cropSale = cropSale;
        _logger = logger;
    }

    public async Task LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_hasLoadedSettings)
        {
            return;
        }

        try
        {
            var stored = await _localSettingsService.ReadSettingAsync<IndependentTaskSettings>(
                SettingsKeys.IndependentTaskSettings);
            if (stored is not null)
            {
                _settings = stored.Normalize();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取任务设置失败，已使用默认值");
        }

        _hasLoadedSettings = true;
    }

    public void SetSettings(IndependentTaskSettings settings)
    {
        _settings = (settings ?? IndependentTaskSettings.CreateDefault()).Normalize(_legendaryTargets);
        _ = SaveSettingsAsync(_settings.Clone());
    }

    public Task<IndependentTaskStartResult> StartAsync(
        IndependentTaskKind kind,
        CancellationToken cancellationToken = default)
        => StartTasksAsync([kind], null, cancellationToken);

    public Task<IndependentTaskStartResult> StartDailyAsync(
        IReadOnlyList<IndependentTaskKind> tasks, IndependentTaskSettings challenges,
        CancellationToken cancellationToken = default)
    {
        if (tasks.Count == 0)
            return Task.FromResult(IndependentTaskStartResult.Failed("请至少启用一个每日任务。"));
        if (tasks.Any(kind => kind is not (IndependentTaskKind.LegendaryChallenge or IndependentTaskKind.FlowerSeedChallenge or IndependentTaskKind.StaminaCrafting or IndependentTaskKind.EveningRest or IndependentTaskKind.Mail or IndependentTaskKind.Gardening or IndependentTaskKind.Sell or IndependentTaskKind.BattlePassRewards)))
            return Task.FromResult(IndependentTaskStartResult.Failed("每日清单包含尚未接入的任务。"));
        return StartTasksAsync(tasks.ToArray(), challenges.Clone(), cancellationToken);
    }

    private async Task<IndependentTaskStartResult> StartTasksAsync(
        IReadOnlyList<IndependentTaskKind> tasks,
        IndependentTaskSettings? dailySettings,
        CancellationToken cancellationToken)
    {
        var isDaily = dailySettings is not null;
        var kind = tasks[0];
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            if (_runningTaskKind is { } runningKind)
            {
                return IndependentTaskStartResult.Failed(
                    runningKind == kind
                        ? $"{GetTaskDisplayName(kind)}任务已在运行中。"
                        : $"{GetTaskDisplayName(runningKind)}任务运行中，请先停止后再启动其他任务。");
            }

            // 独立任务复用启动页建立的运行会话（窗口、截图方式、识别配置）。
            if (!_runtimeTaskService.IsRunning || _runtimeTaskService.CurrentState is null)
            {
                return IndependentTaskStartResult.Failed(
                    "请先在启动页启动任务，再运行独立任务。");
            }

            var settings = dailySettings?.Normalize(LegendaryTargets) ?? _settings.Clone();
            settings.FlowerSeedOptions = [.. _settings.FlowerSeedOptions];
            settings.UseLegendaryMarkWhenInsufficient = _settings.UseLegendaryMarkWhenInsufficient;
            if (tasks.Contains(IndependentTaskKind.Gardening) && !settings.ReuseEquippedSeed &&
                (!IndependentTaskSettings.SaleFlowers.Contains(settings.GardenSeed) || settings.SeedsPerPlot is < 1 or > 3))
                return IndependentTaskStartResult.Failed("请选择种子和每格播种颗数。");

            if (tasks.Contains(IndependentTaskKind.Sell) && (!IndependentTaskSettings.SaleFlowers.Contains(settings.SaleFlower) || settings.SaleQuantity < 1))
                return IndependentTaskStartResult.Failed("请选择要出售的花，并填写正整数数量。");

            if (tasks.Contains(IndependentTaskKind.FlowerSeedChallenge)
                && !settings.FlowerSeedAtTargetLocation
                && !settings.FlowerSeedOptions.Any(option => option.Number == settings.FlowerSeedTargetNumber))
            {
                return IndependentTaskStartResult.Failed("请先扫描并选择目标花种。");
            }

            if (tasks.Contains(IndependentTaskKind.LegendaryChallenge) && !settings.LegendaryAtTargetLocation
                && !LegendaryTargets.Contains(settings.LegendaryTargetName))
                return IndependentTaskStartResult.Failed("请先选择目标传说精灵。");

            if (kind == IndependentTaskKind.FlowerSeedScan)
            {
                _settings.FlowerSeedOptions.Clear();
                _settings.FlowerSeedTargetNumber = 0;
                await SaveSettingsAsync(_settings.Clone());
                NotifyStateChanged();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var taskCts = new CancellationTokenSource();
            _taskCts = taskCts;
            _runningTaskKind = kind;
            IsDailyRunning = isDaily;
            StatusMessage = isDaily ? "每日任务准备中" : $"{GetTaskDisplayName(kind)}准备中";
            var battleSettings = _runtimeTaskService.AutoBattleSettings;
            _taskLoop = Task.Run(() => RunTasksAsync(tasks, isDaily, settings, battleSettings, taskCts), CancellationToken.None);
        }
        finally
        {
            _stateLock.Release();
        }

        NotifyStateChanged();
        return IndependentTaskStartResult.Started($"{(isDaily ? "每日" : GetTaskDisplayName(kind))}任务已启动，实时任务自动暂停，结束后恢复。");
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? taskCts;
        Task? taskLoop;
        await _stateLock.WaitAsync();
        try
        {
            taskCts = _taskCts;
            taskLoop = _taskLoop;
        }
        finally
        {
            _stateLock.Release();
        }

        if (taskCts is null)
        {
            return;
        }

        try
        {
            taskCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (taskLoop is not null)
        {
            try
            {
                await taskLoop;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "等待任务结束时出现异常");
            }
        }
    }

    private async Task RunTasksAsync(
        IReadOnlyList<IndependentTaskKind> tasks,
        bool isDaily,
        IndependentTaskSettings settings,
        AutoBattleSettings battleSettings,
        CancellationTokenSource taskCts)
    {
        var ownsSuspension = !_runtimeTaskService.IsSuspended;
        var outcomes = new string?[tasks.Count];
        var activeIndex = -1;
        var kind = tasks[0];
        var taskName = GetTaskDisplayName(kind);
        string? lastProgress = null;
        RuntimeTaskState? session = null;
        (string Task, string Reason)? firstFailure = null;
        CapturedFrame? failureFrame = null;
        var abortRemaining = false;
        try
        {
            UpdateProgress(new("准备任务", "等待实时任务暂停"));
            await _runtimeTaskService.SuspendAsync($"{taskName}任务运行中", taskCts.Token);
            var state = session = _runtimeTaskService.CurrentState
                ?? throw new InvalidOperationException("运行会话已结束，请先在启动页启动任务。");

            if (_gameWindowService.TryBringGameWindowToForeground(state.TargetWindow))
                _logger.LogDebug("{Task}：已将游戏窗口切换到前台，窗口 {Window}", taskName, state.TargetWindow.DisplayName);
            else
                _logger.LogWarning("{Task}：未能把游戏窗口切换到前台，请手动切到游戏", taskName);

            for (var index = 0; index < tasks.Count; index++)
            {
                taskCts.Token.ThrowIfCancellationRequested();
                activeIndex = index;
                var outcome = "成功";
                kind = tasks[index];
                taskName = GetTaskDisplayName(kind);
                _runningTaskKind = kind;
                StatusMessage = isDaily ? $"正在执行 {index + 1}/{tasks.Count}：{taskName}" : $"正在执行：{taskName}";
                NotifyStateChanged();
                lastProgress = null;
                if (isDaily)
                    _logger.LogInformation("每日任务 {Index}/{Total}：开始执行{Task}", index + 1, tasks.Count, taskName);
                else
                    _logger.LogInformation("{Task}：开始执行", taskName);
                // 卡住（超时）时退回大世界重跑一次；清单里的任务互不拖累，失败了记下来继续跑后面的。
                var doneRuns = 0;
                Exception? failure = null;
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        outcome = await RunKindAsync(state, kind, doneRuns);
                        failure = null;
                        break;
                    }
                    catch (Exception ex) when (!taskCts.IsCancellationRequested)
                    {
                        failure = ex;
                        var stalled = ex is TimeoutException;
                        var committed = (ex as TaskStalledException)?.CommittedOutcome;
                        doneRuns += (ex as TaskStalledException)?.CompletedRuns ?? 0;
                        var retry = stalled && committed is null && attempt < MaximumTaskRetries;
                        // 单独运行且不再重试：保持原样，让画面停在出错的界面，方便看卡在哪。
                        if (!isDaily && !retry && committed is null) break;
                        if (retry)
                            _logger.LogWarning(ex, "{Task}卡住了，退回大世界后重试（第 {Attempt} 次）", taskName, attempt + 1);
                        else if (committed is null)
                            _logger.LogError(ex, "{Task}失败，继续执行清单里的下一项", taskName);
                        if (isDaily && committed is null && !retry && _notifications.IsEnabled)
                            failureFrame ??= await CaptureFailureFrameAsync();
                        UpdateProgress(new("恢复", retry ? "退回大世界后重试" : "退回大世界"));
                        var recovered = _worldRecovery is null || await _worldRecovery.ReturnToWorldAsync(state, taskCts.Token);
                        if (recovered && committed is not null)
                        {
                            _logger.LogWarning(ex, "{Task}的操作已完成，但没能正常退出界面，已退回大世界", taskName);
                            outcome = committed;
                            failure = null;
                            break;
                        }
                        if (!recovered)
                        {
                            // 回不到大世界，后面的任务无从开始。
                            failure = new InvalidOperationException($"{ex.Message.TrimEnd('。')}，且未能退回大世界。", ex);
                            abortRemaining = true;
                            break;
                        }
                        if (!retry) break;
                        lastProgress = null;
                    }
                }
                if (failure is not null)
                {
                    if (!isDaily || abortRemaining) throw failure;
                    outcomes[index] = $"失败：{failure.Message}";
                    firstFailure ??= (taskName, failure.Message);
                    activeIndex = -1;
                    continue;
                }
                outcomes[index] = outcome;
                activeIndex = -1;
                // 挑战和扫描在上面各自记过结果，这里只补日常任务的结束。
                if (!HasOwnLog(kind)) _logger.LogInformation("{Task}结束：{Outcome}", taskName, outcome);
            }

            var failed = outcomes.Count(item => item?.StartsWith("失败") == true);
            StatusMessage = !isDaily ? $"{taskName}：{outcomes[0]}"
                : failed == 0 ? $"每日清单结束，共 {tasks.Count} 项" : $"每日清单结束，{failed} 项失败";
            if (isDaily)
            {
                // 每日清单只通知最终结果，各子任务的完成信息仍显示在灵动岛。
                _runtimeTaskService.ShowIndependentTaskResult(StatusMessage, Summary());
                if (firstFailure is { } first)
                    await NotifyAsync(TaskNotificationText.DailyFailed(first.Task, first.Reason, Outcomes()), failureFrame, openGame: true);
                else
                    await NotifyAsync(TaskNotificationText.DailyCompleted(Outcomes()));
            }
        }
        catch (OperationCanceledException) when (taskCts.IsCancellationRequested)
        {
            if (activeIndex >= 0) outcomes[activeIndex] = "已取消";
            _logger.LogInformation("{Task}：已取消", taskName);
            StatusMessage = isDaily ? "每日任务已停止" : $"{taskName}已停止";
        }
        catch (Exception ex)
        {
            if (activeIndex >= 0) outcomes[activeIndex] = $"失败：{ex.Message}";
            _logger.LogError(ex, "{Task}失败，任务已停止", GetTaskDisplayName(kind));
            StatusMessage = $"{(isDaily ? "每日任务" : taskName)}已停止：{ex.Message}";
            // 失败原因上一行已经记过，这里只更新遮罩。
            _runtimeTaskService.UpdateIndependentTaskStatus(taskName, new("任务失败", ex.Message));
            _runtimeTaskService.ShowIndependentTaskResult($"{(isDaily ? "每日任务" : taskName)}已停止",
                isDaily ? $"{ex.Message.TrimEnd('。')}。{Summary()}" : ex.Message);
            // 出错时附上当前游戏画面，不在电脑前也能看出卡在哪个界面；点击通知直接回到游戏。
            if (_notifications.IsEnabled) failureFrame ??= await CaptureFailureFrameAsync();
            await NotifyAsync(isDaily ? TaskNotificationText.DailyFailed(taskName, ex.Message, Outcomes())
                : TaskNotificationText.TaskFailed(taskName, ex.Message), failureFrame, openGame: true);
        }
        finally
        {
            if (isDaily)
            {
                var summary = string.Join(Environment.NewLine, Outcomes().Select(item =>
                    $"{GetOutcomeIcon(item.Outcome)} {item.Name}：{item.Outcome ?? "未执行"}"));
                _logger.LogInformation("每日任务汇总：{Summary}", Environment.NewLine + summary);
            }
            _recognitionDiagnostics?.Flush();
            (_challengeRunner as IChallengeResultFrameSource)?.TakeResultFrame()?.Dispose();
            failureFrame?.Dispose();
            var stateCleared = false;
            await _stateLock.WaitAsync();
            try
            {
                if (ReferenceEquals(_taskCts, taskCts))
                {
                    _runtimeTaskService.UpdateIndependentTaskStatus(null);
                    if (ownsSuspension)
                    {
                        _runtimeTaskService.Resume();
                    }

                    _taskCts = null;
                    _taskLoop = null;
                    _runningTaskKind = null;
                    IsDailyRunning = false;
                    stateCleared = true;
                }
            }
            finally
            {
                _stateLock.Release();
            }

            taskCts.Dispose();
            if (stateCleared)
            {
                NotifyStateChanged();
            }
        }

        async Task<string> RunKindAsync(RuntimeTaskState state, IndependentTaskKind kind, int doneRuns)
        {
            var outcome = "成功";
                switch (kind)
                {
                case IndependentTaskKind.BattlePassRewards:
                    outcome = await _battlePass.RunAsync(state, UpdateProgress, taskCts.Token);
                    break;
                case IndependentTaskKind.Sell:
                    outcome = await _cropSale.RunAsync(state, settings.SaleFlower, settings.SaleQuantity, UpdateProgress, taskCts.Token);
                    break;
                case IndependentTaskKind.Gardening:
                    outcome = await _gardening.RunAsync(state, settings, UpdateProgress, taskCts.Token);
                    break;
                case IndependentTaskKind.Mail:
                    outcome = await _mail.RunAsync(state, UpdateProgress, taskCts.Token);
                    break;
                case IndependentTaskKind.EveningRest:
                    outcome = await _eveningRest.RunAsync(state, UpdateProgress, taskCts.Token);
                    break;
                case IndependentTaskKind.StaminaCrafting:
                    outcome = await _staminaCrafting.RunAsync(state, UpdateProgress, taskCts.Token);
                    break;
                case IndependentTaskKind.ThrowBall:
                    // 循环丢球直到手动停止，正常情况下不会返回。
                    outcome = await _throwBall.RunAsync(state, settings, UpdateProgress, taskCts.Token);
                    break;
                case IndependentTaskKind.LegendaryScan:
                    var names = await _challengeRunner.ScanLegendaryAsync(state, UpdateProgress, taskCts.Token);
                    taskCts.Token.ThrowIfCancellationRequested();
                    if (names.Count == 0)
                        throw new InvalidOperationException("未识别到传说精灵，已保留原有名单和选择。");
                    await _stateLock.WaitAsync(taskCts.Token);
                    try
                    {
                        // 首次刷新前的选择用于持久化；后续刷新和选择只影响本次启动。
                        _savedLegendaryTarget ??= _settings.LegendaryTargetName;
                        _legendaryTargets = Array.AsReadOnly(names.ToArray());
                        _settings = _settings.Normalize(_legendaryTargets);
                    }
                    finally
                    {
                        _stateLock.Release();
                    }
                    NotifyStateChanged();
                    _logger.LogInformation("传说精灵扫描完成，共 {Count} 个", names.Count);
                    _runtimeTaskService.ShowIndependentTaskResult($"扫描完成，共识别 {names.Count} 个传说精灵", "名单仅本次启动有效");
                    break;
                case IndependentTaskKind.LegendaryChallenge:
                    // 重试时只补剩下的次数，已经捉到的不重打。
                    if (settings.LegendaryChallengeRunCount > doneRuns)
                        await _challengeRunner.RunLegendaryAsync(state, settings.LegendaryTargetName,
                        settings.LegendaryChallengeRunCount - doneRuns, settings.LegendaryDifficulty, settings.UseLegendaryMarkWhenInsufficient,
                        settings.LegendaryAtTargetLocation, battleSettings, UpdateProgress, taskCts.Token);
                    taskCts.Token.ThrowIfCancellationRequested();
                    var legendaryTarget = settings.LegendaryAtTargetLocation ? "当前位置" : settings.LegendaryTargetName;
                    outcome = $"成功：{legendaryTarget}，{settings.LegendaryChallengeRunCount} 次";
                    _logger.LogInformation("传说精灵挑战完成：{Target}，成功 {Count} 次", legendaryTarget, settings.LegendaryChallengeRunCount);
                    _runtimeTaskService.ShowIndependentTaskResult($"传说精灵挑战完成，成功 {settings.LegendaryChallengeRunCount} 次", "已退出并返回大世界");
                    await NotifyChallengeCompletedAsync(settings.LegendaryAtTargetLocation ? null : settings.LegendaryTargetName,
                        settings.LegendaryChallengeRunCount);
                    break;
                case IndependentTaskKind.FlowerSeedChallenge:
                    if (settings.FlowerSeedRunCount > doneRuns)
                        await _challengeRunner.RunAsync(state,
                        settings.FlowerSeedAtTargetLocation ? null
                            : settings.FlowerSeedOptions.Single(option => option.Number == settings.FlowerSeedTargetNumber),
                        settings.FlowerSeedRunCount - doneRuns, settings.FlowerSeedAtTargetLocation,
                        settings.FlowerSeedPreferCondensedMark, battleSettings,
                        UpdateProgress, taskCts.Token);
                    taskCts.Token.ThrowIfCancellationRequested();
                    var flowerSeedTarget = settings.FlowerSeedAtTargetLocation ? "当前位置" : $"花种 {settings.FlowerSeedTargetNumber}";
                    outcome = $"成功：{flowerSeedTarget}，{settings.FlowerSeedRunCount} 次";
                    _logger.LogInformation("花种挑战完成：{Target}，成功 {Count} 次", flowerSeedTarget, settings.FlowerSeedRunCount);
                    _runtimeTaskService.ShowIndependentTaskResult($"花种挑战完成，成功 {settings.FlowerSeedRunCount} 次", "已退出并返回大世界");
                    await NotifyChallengeCompletedAsync(settings.FlowerSeedAtTargetLocation ? null : flowerSeedTarget,
                        settings.FlowerSeedRunCount);
                    break;
                case IndependentTaskKind.FlowerSeedScan:
                    var options = await _challengeRunner.ScanAsync(state, UpdateProgress, taskCts.Token);
                    taskCts.Token.ThrowIfCancellationRequested();
                    await _stateLock.WaitAsync(taskCts.Token);
                    try
                    {
                        _settings.FlowerSeedOptions = options.ToList();
                        _settings.FlowerSeedTargetNumber = 0;
                        await SaveSettingsAsync(_settings.Clone());
                    }
                    finally
                    {
                        _stateLock.Release();
                    }

                    _logger.LogInformation("花种扫描完成：{Count} 个花种", _settings.FlowerSeedOptions.Count);
                    NotifyStateChanged();
                    taskCts.Token.ThrowIfCancellationRequested();
                    _runtimeTaskService.ShowIndependentTaskResult($"扫描完成，共识别 {_settings.FlowerSeedOptions.Count} 个花种", "花种选项已更新");
                    break;
                }
            return outcome;
        }

        string Summary() => string.Join("；", tasks.Select((task, i) =>
            $"{GetTaskDisplayName(task)}：{outcomes[i] ?? "未执行"}"));

        static string GetOutcomeIcon(string? outcome) => outcome switch
        {
            null or "已取消" => "⚪",
            _ when outcome.StartsWith("跳过", StringComparison.Ordinal) => "⚪",
            _ when outcome.StartsWith("失败", StringComparison.Ordinal) => "❌",
            _ => "✅"
        };

        void UpdateProgress(IndependentTaskProgress progress)
        {
            _runtimeTaskService.UpdateIndependentTaskStatus(taskName, progress);

            // 各任务每一轮都会上报进度，内容变了才记。日常任务自己不写日志，进度就是它们的流水；
            // 挑战和扫描有自己的流水，进度只留调试信息。
            var text = string.IsNullOrWhiteSpace(progress.Operation) ? progress.Stage : progress.Operation;
            if (string.IsNullOrWhiteSpace(text) || text == lastProgress) return;
            lastProgress = text;
            if (HasOwnLog(kind))
                _logger.LogDebug("{Task}进度：{Stage}，{Operation}", taskName, progress.Stage, progress.Operation);
            else
                _logger.LogInformation("{Task}：{Step}", taskName, text);
        }

        static bool HasOwnLog(IndependentTaskKind kind) => kind is IndependentTaskKind.LegendaryScan
            or IndependentTaskKind.LegendaryChallenge or IndependentTaskKind.FlowerSeedChallenge
            or IndependentTaskKind.FlowerSeedScan;

        (string Name, string? Outcome)[] Outcomes()
            => tasks.Select((task, i) => (GetTaskDisplayName(task), outcomes[i])).ToArray();

        async Task NotifyChallengeCompletedAsync(string? target, int count)
        {
            // 结果画面随手取走；每日清单里的挑战不单独通知。
            using var resultFrame = (_challengeRunner as IChallengeResultFrameSource)?.TakeResultFrame();
            if (!isDaily) await NotifyAsync(TaskNotificationText.ChallengeCompleted(taskName, target, count), resultFrame);
        }

        async Task<CapturedFrame?> CaptureFailureFrameAsync()
        {
            if (_screenCapture is null || _screenshots is null || session is null) return null;
            try
            {
                return await Task.Run(() => _screenCapture.Capture(session.TargetWindow, session.Options.CaptureMethod));
            }
            catch (Exception captureError)
            {
                _logger.LogDebug(captureError, "{Task}：出错时没能截到游戏画面，通知不带图", taskName);
                return null;
            }
        }

        async Task NotifyAsync((string Title, string Message) text, CapturedFrame? frame = null, bool openGame = false)
        {
            if (!_notifications.IsEnabled) return;
            string? imagePath = null;
            if (frame is not null && _screenshots is not null)
            {
                try
                {
                    imagePath = await _screenshots.SaveAsync(frame, session?.TargetWindow);
                }
                catch (Exception imageError)
                {
                    _logger.LogWarning(imageError, "{Task}：通知配图没有保存下来，改发不带图的通知", taskName);
                }
            }

            await _notifications.ShowAsync(text.Title, text.Message, imagePath, openGame);
        }
    }

    private async Task SaveSettingsAsync(IndependentTaskSettings settings)
    {
        if (_savedLegendaryTarget is not null) settings.LegendaryTargetName = _savedLegendaryTarget;
        await _settingsSaveLock.WaitAsync();
        try
        {
            await _localSettingsService.SaveSettingAsync(SettingsKeys.IndependentTaskSettings, settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存任务设置失败，本次修改重启后不会保留");
        }
        finally
        {
            _settingsSaveLock.Release();
        }
    }

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string GetTaskDisplayName(IndependentTaskKind kind)
    {
        return kind switch
        {
            IndependentTaskKind.LegendaryChallenge => "传说精灵挑战",
            IndependentTaskKind.FlowerSeedChallenge => "花种挑战",
            IndependentTaskKind.FlowerSeedScan => "扫描花种",
            IndependentTaskKind.BattlePassRewards => "通行证奖励",
            IndependentTaskKind.Mail => "邮件领取",
            IndependentTaskKind.Sell => "自动出售",
            IndependentTaskKind.Gardening => "自动种菜",
            IndependentTaskKind.EveningRest => "晚风小憩",
            IndependentTaskKind.StaminaCrafting => "体力合成",
            IndependentTaskKind.LegendaryScan => "扫描传说精灵",
            IndependentTaskKind.ThrowBall => "自动丢球",
            _ => "独立"
        };
    }
}
