using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Runtime;

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
    private readonly IManualChallengeRunner _challengeRunner;
    private readonly IAppNotificationService _notifications;
    private readonly IGameWindowService _gameWindowService;
    private readonly ILogger<IndependentTaskService> _logger;

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

    public IndependentTaskKind? RunningTaskKind => _runningTaskKind;

    public IndependentTaskSettings Settings => _settings.Clone();

    public IReadOnlyList<string> LegendaryTargets => _legendaryTargets ?? IndependentTaskSettings.LegendaryTargets;

    public IndependentTaskService(
        IRuntimeSessionControl runtimeTaskService,
        ILocalSettingsService localSettingsService,
        IManualChallengeRunner challengeRunner,
        IAppNotificationService notifications,
        IGameWindowService gameWindowService,
        ILogger<IndependentTaskService> logger)
    {
        _runtimeTaskService = runtimeTaskService;
        _localSettingsService = localSettingsService;
        _challengeRunner = challengeRunner;
        _notifications = notifications;
        _gameWindowService = gameWindowService;
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
        _settings = (settings ?? IndependentTaskSettings.CreateDefault()).Normalize(_legendaryTargets);
        _ = SaveSettingsAsync(_settings.Clone());
    }

    public async Task<IndependentTaskStartResult> StartAsync(
        IndependentTaskKind kind,
        CancellationToken cancellationToken = default)
    {
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

            if (kind == IndependentTaskKind.FlowerSeedChallenge
                && !_settings.FlowerSeedOptions.Any(option => option.Number == _settings.FlowerSeedTargetNumber))
            {
                return IndependentTaskStartResult.Failed("请先扫描并选择目标花种。");
            }

            if (kind == IndependentTaskKind.LegendaryChallenge && !LegendaryTargets.Contains(_settings.LegendaryTargetName))
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
            var settings = _settings.Clone();
            var battleSettings = _runtimeTaskService.AutoBattleSettings;
            _taskLoop = Task.Run(() => RunTaskAsync(kind, settings, battleSettings, taskCts), CancellationToken.None);
            _logger.LogInformation("独立任务已启动：{TaskName}", GetTaskDisplayName(kind));
        }
        finally
        {
            _stateLock.Release();
        }

        NotifyStateChanged();
        return IndependentTaskStartResult.Started($"{GetTaskDisplayName(kind)}任务已启动，实时任务自动暂停，结束后恢复。");
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

    private async Task RunTaskAsync(
        IndependentTaskKind kind,
        IndependentTaskSettings settings,
        AutoBattleSettings battleSettings,
        CancellationTokenSource taskCts)
    {
        var ownsSuspension = !_runtimeTaskService.IsSuspended;
        var taskName = GetTaskDisplayName(kind);
        try
        {
            UpdateProgress(new("准备任务", "等待实时任务暂停"));
            await _runtimeTaskService.SuspendAsync($"{taskName}任务运行中", taskCts.Token);
            var state = _runtimeTaskService.CurrentState
                ?? throw new InvalidOperationException("运行会话已结束，请先在启动页启动任务。");

            if (_gameWindowService.TryBringGameWindowToForeground(state.TargetWindow))
                _logger.LogInformation("独立任务启动：已将游戏窗口切换到前台。Task={TaskName}, Window={Window}", taskName, state.TargetWindow.DisplayName);
            else
                _logger.LogWarning("独立任务启动：未能将游戏窗口切换到前台。Task={TaskName}, Window={Window}", taskName, state.TargetWindow.DisplayName);

            switch (kind)
            {
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
                    _runtimeTaskService.ShowIndependentTaskResult($"扫描完成，共识别 {names.Count} 个传说精灵", "名单和选择仅本次启动有效，重启后恢复本地设置");
                    break;
                case IndependentTaskKind.LegendaryChallenge:
                    await _challengeRunner.RunLegendaryAsync(state, settings.LegendaryTargetName,
                        settings.LegendaryChallengeRunCount, settings.LegendaryDifficulty, settings.UseLegendaryMarkWhenInsufficient, battleSettings, UpdateProgress, taskCts.Token);
                    taskCts.Token.ThrowIfCancellationRequested();
                    await ReportResultAsync($"传说精灵挑战完成，成功 {settings.LegendaryChallengeRunCount} 次", "已退出并返回大世界");
                    break;
                case IndependentTaskKind.FlowerSeedChallenge:
                    await _challengeRunner.RunAsync(state,
                        settings.FlowerSeedOptions.Single(option => option.Number == settings.FlowerSeedTargetNumber),
                        settings.FlowerSeedRunCount, battleSettings,
                        UpdateProgress, taskCts.Token);
                    taskCts.Token.ThrowIfCancellationRequested();
                    await ReportResultAsync($"花种挑战完成，成功 {settings.FlowerSeedRunCount} 次", "已退出并返回大世界");
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
        }
        catch (OperationCanceledException) when (taskCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "独立任务异常终止：{TaskName}", GetTaskDisplayName(kind));
            UpdateProgress(new("任务失败", ex.Message));
            await ReportResultAsync($"{taskName}已停止", ex.Message);
        }
        finally
        {
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
                _logger.LogInformation("独立任务已停止：{TaskName}", GetTaskDisplayName(kind));
                NotifyStateChanged();
            }
        }

        void UpdateProgress(IndependentTaskProgress progress)
            => _runtimeTaskService.UpdateIndependentTaskStatus(taskName, progress);

        async Task ReportResultAsync(string title, string message)
        {
            _runtimeTaskService.ShowIndependentTaskResult(title, message);
            if (_notifications.IsEnabled && !await _notifications.ShowAsync(title, message))
                _logger.LogWarning("任务结果通知未显示：{Title}，{Message}", title, message);
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
            _logger.LogWarning(ex, "保存独立任务设置失败。");
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
            IndependentTaskKind.LegendaryScan => "扫描传说精灵",
            _ => "独立"
        };
    }
}
