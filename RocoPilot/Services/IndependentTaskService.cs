using Microsoft.Extensions.Logging;
using System.Xml.Linq;

using RocoPilot.Configuration;
using RocoPilot.Contracts.Services;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

/// <summary>
/// 独立任务调度：首领战斗、传说精灵挑战等限次任务按需启动，跑完即止。
/// 独立任务基于启动页建立的运行会话工作：启动前需实时任务处于运行状态，
/// 运行期间自动挂起实时识别循环并复用其捕获会话，结束后自动恢复。
/// </summary>
public sealed class IndependentTaskService : IIndependentTaskService
{
    private readonly IRuntimeSessionControl _runtimeTaskService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IFlowerSeedChallengeRunner _flowerSeedRunner;
    private readonly IAppNotificationService _notifications;
    private readonly ILogger<IndependentTaskService> _logger;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _settingsSaveLock = new(1, 1);
    private IndependentTaskSettings _settings = IndependentTaskSettings.CreateDefault();
    private bool _hasLoadedSettings;
    private IndependentTaskKind? _runningTaskKind;
    private CancellationTokenSource? _taskCts;
    private Task? _taskLoop;

    public event EventHandler? StateChanged;

    public bool IsRunning => _runningTaskKind is not null;

    public IndependentTaskKind? RunningTaskKind => _runningTaskKind;

    public IndependentTaskSettings Settings => _settings.Clone();

    public IndependentTaskService(
        IRuntimeSessionControl runtimeTaskService,
        ILocalSettingsService localSettingsService,
        IFlowerSeedChallengeRunner flowerSeedRunner,
        IAppNotificationService notifications,
        ILogger<IndependentTaskService> logger)
    {
        _runtimeTaskService = runtimeTaskService;
        _localSettingsService = localSettingsService;
        _flowerSeedRunner = flowerSeedRunner;
        _notifications = notifications;
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

            switch (kind)
            {
                case IndependentTaskKind.FlowerSeedChallenge:
                    await _flowerSeedRunner.RunAsync(state,
                        settings.FlowerSeedOptions.Single(option => option.Number == settings.FlowerSeedTargetNumber),
                        settings.FlowerSeedRunCount, battleSettings,
                        UpdateProgress, taskCts.Token);
                    _runtimeTaskService.ShowIndependentTaskResult($"花种挑战完成，成功 {settings.FlowerSeedRunCount} 次", "已退出并返回大世界");
                    break;
                case IndependentTaskKind.FlowerSeedScan:
                    var options = await _flowerSeedRunner.ScanAsync(state, UpdateProgress, taskCts.Token);
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
                    _runtimeTaskService.ShowIndependentTaskResult($"扫描完成，共识别 {_settings.FlowerSeedOptions.Count} 个花种", "花种选项已更新");
                    break;
                default:
                    // 首领和传说任务尚未接入执行逻辑，保持现有手动停止行为。
                    await Task.Delay(Timeout.InfiniteTimeSpan, taskCts.Token);
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
            if (kind == IndependentTaskKind.FlowerSeedChallenge)
            {
                var title = "花种挑战已停止";
                _runtimeTaskService.ShowIndependentTaskResult(title, ex.Message);
                var payload = new XElement("toast", new XElement("visual",
                    new XElement("binding", new XAttribute("template", "ToastGeneric"),
                        new XElement("text", title), new XElement("text", ex.Message))));
                if (!_notifications.Show(payload.ToString(SaveOptions.DisableFormatting)))
                    _logger.LogWarning("花种挑战停止通知未显示：{Message}", ex.Message);
            }
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
    }

    private async Task SaveSettingsAsync(IndependentTaskSettings settings)
    {
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
            IndependentTaskKind.BossBattle => "首领战斗",
            IndependentTaskKind.LegendaryChallenge => "传说精灵挑战",
            IndependentTaskKind.FlowerSeedChallenge => "花种挑战",
            IndependentTaskKind.FlowerSeedScan => "扫描花种",
            _ => "独立"
        };
    }
}
