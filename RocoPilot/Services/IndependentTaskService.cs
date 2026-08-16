using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

/// <summary>
/// 独立任务调度：首领战斗、传说精灵挑战等限次任务按需启动，跑完即止。
/// 独立任务基于启动页建立的运行会话工作：启动前需实时任务处于运行状态，
/// 运行期间自动挂起实时识别循环并复用其捕获会话，结束后自动恢复。
/// 当前版本只维护启动/停止状态与配置持久化，具体挑战执行逻辑后续接入 <see cref="RunTaskAsync"/>。
/// </summary>
public sealed class IndependentTaskService : IIndependentTaskService
{
    private readonly IRuntimeTaskService _runtimeTaskService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly ILogger<IndependentTaskService> _logger;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private IndependentTaskSettings _settings = IndependentTaskSettings.CreateDefault();
    private bool _hasLoadedSettings;
    private IndependentTaskKind? _runningTaskKind;
    private CancellationTokenSource? _taskCts;
    private Task? _taskLoop;
    private bool _hasSuspendedRuntimeTask;

    public event EventHandler? StateChanged;

    public bool IsRunning => _runningTaskKind is not null;

    public IndependentTaskKind? RunningTaskKind => _runningTaskKind;

    public IndependentTaskSettings Settings => _settings.Clone();

    public IndependentTaskService(
        IRuntimeTaskService runtimeTaskService,
        ILocalSettingsService localSettingsService,
        ILogger<IndependentTaskService> logger)
    {
        _runtimeTaskService = runtimeTaskService;
        _localSettingsService = localSettingsService;
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
            _logger.LogWarning(ex, "读取独立任务设置失败，使用默认值。");
        }

        _hasLoadedSettings = true;
    }

    public void SetSettings(IndependentTaskSettings settings)
    {
        _settings = (settings ?? IndependentTaskSettings.CreateDefault()).Normalize();
        _ = SaveSettingsAsync(_settings);
    }

    public async Task<IndependentTaskStartResult> StartAsync(
        IndependentTaskKind kind,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        var suspendedRuntimeTask = false;
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
            if (!_runtimeTaskService.IsRunning)
            {
                return IndependentTaskStartResult.Failed(
                    "请先在启动页启动任务，再运行独立任务。");
            }

            // 实时任务无需手动停止：独立任务运行期间自动挂起，结束后自动恢复。
            if (!_runtimeTaskService.IsSuspended)
            {
                _runtimeTaskService.Suspend($"{GetTaskDisplayName(kind)}任务运行中");
                _hasSuspendedRuntimeTask = true;
                suspendedRuntimeTask = true;
            }

            var taskCts = new CancellationTokenSource();
            _taskCts = taskCts;
            _runningTaskKind = kind;
            _taskLoop = Task.Run(() => RunTaskAsync(kind, taskCts), CancellationToken.None);
            _logger.LogInformation("独立任务已启动：{TaskName}", GetTaskDisplayName(kind));
        }
        finally
        {
            _stateLock.Release();
        }

        NotifyStateChanged();
        return IndependentTaskStartResult.Started(
            suspendedRuntimeTask
                ? $"{GetTaskDisplayName(kind)}任务已启动，实时任务已自动暂停，任务结束后恢复。"
                : $"{GetTaskDisplayName(kind)}任务已启动。");
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
                _logger.LogDebug(ex, "等待独立任务结束时出现异常。");
            }
        }
    }

    private async Task RunTaskAsync(IndependentTaskKind kind, CancellationTokenSource taskCts)
    {
        try
        {
            // 挑战执行逻辑（识别、按键、次数控制）后续接入；当前仅保持运行状态直到手动停止。
            await Task.Delay(Timeout.InfiniteTimeSpan, taskCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "独立任务异常终止：{TaskName}", GetTaskDisplayName(kind));
        }
        finally
        {
            var stateCleared = false;
            var shouldResumeRuntimeTask = false;
            await _stateLock.WaitAsync();
            try
            {
                if (ReferenceEquals(_taskCts, taskCts))
                {
                    _taskCts = null;
                    _taskLoop = null;
                    _runningTaskKind = null;
                    stateCleared = true;
                    shouldResumeRuntimeTask = _hasSuspendedRuntimeTask;
                    _hasSuspendedRuntimeTask = false;
                }
            }
            finally
            {
                _stateLock.Release();
            }

            taskCts.Dispose();
            if (stateCleared)
            {
                if (shouldResumeRuntimeTask)
                {
                    _runtimeTaskService.Resume();
                }

                _logger.LogInformation("独立任务已停止：{TaskName}", GetTaskDisplayName(kind));
                NotifyStateChanged();
            }
        }
    }

    private async Task SaveSettingsAsync(IndependentTaskSettings settings)
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(SettingsKeys.IndependentTaskSettings, settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存独立任务设置失败。");
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
            IndependentTaskKind.BossBattle => "首领战斗",
            IndependentTaskKind.LegendaryChallenge => "传说精灵挑战",
            _ => "独立"
        };
    }
}
