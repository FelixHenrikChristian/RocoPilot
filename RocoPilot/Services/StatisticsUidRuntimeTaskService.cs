using Microsoft.Extensions.Logging;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.Statistics;
using RocoPilot.Views;

namespace RocoPilot.Services;

public sealed class StatisticsUidRuntimeTaskService : IRuntimeTaskService
{
    private readonly RuntimeTaskService _runtimeTaskService;
    private readonly IStatisticsUidDetectionService _statisticsUidDetectionService;
    private readonly IStatisticsService _statisticsService;
    private readonly IInfoOverlayNotificationService _infoOverlayNotificationService;
    private readonly ILogger<StatisticsUidRuntimeTaskService> _logger;

    public StatisticsUidRuntimeTaskService(
        RuntimeTaskService runtimeTaskService,
        IStatisticsUidDetectionService statisticsUidDetectionService,
        IStatisticsService statisticsService,
        IInfoOverlayNotificationService infoOverlayNotificationService,
        ILogger<StatisticsUidRuntimeTaskService> logger)
    {
        _runtimeTaskService = runtimeTaskService;
        _statisticsUidDetectionService = statisticsUidDetectionService;
        _statisticsService = statisticsService;
        _infoOverlayNotificationService = infoOverlayNotificationService;
        _logger = logger;
    }

    public event EventHandler? SettingsChanged
    {
        add => _runtimeTaskService.SettingsChanged += value;
        remove => _runtimeTaskService.SettingsChanged -= value;
    }

    public bool IsRunning => _runtimeTaskService.IsRunning;

    public RuntimeTaskState? CurrentState => _runtimeTaskService.CurrentState;

    public bool EncounterStatisticsEnabled => _runtimeTaskService.EncounterStatisticsEnabled;

    public AutoBattleSettings AutoBattleSettings => _runtimeTaskService.AutoBattleSettings;

    public RuntimeRecognitionSettings RuntimeRecognitionSettings =>
        _runtimeTaskService.RuntimeRecognitionSettings;

    public async Task<RuntimeTaskStartResult> StartAsync(
        RuntimeTaskStartOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_runtimeTaskService.IsRunning)
        {
            return await _runtimeTaskService.StartAsync(options, cancellationToken);
        }

        _statisticsService.RequireActiveAccountSelection();
        _infoOverlayNotificationService.UpdateUidNotice(null);

        StatisticsUidPreparation preparation;
        try
        {
            preparation = await PrepareStatisticsUidAsync(options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return RuntimeTaskStartResult.Failed("启动任务已取消。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "准备首页启动使用的统计账号 UID 失败。");
            preparation = new StatisticsUidPreparation(
                StatisticsUidDetectionResult.Failed("UID 识别准备失败。"),
                ExistingAccountMatched: false);
        }

        var startResult = await _runtimeTaskService.StartAsync(options, cancellationToken);
        if (!startResult.Success || startResult.State is null)
        {
            _infoOverlayNotificationService.UpdateUidNotice(null);
            return startResult;
        }

        var uidMessage = await CompleteStatisticsUidSelectionAsync(preparation);
        var message = string.IsNullOrWhiteSpace(uidMessage)
            ? startResult.Message
            : $"{startResult.Message} {uidMessage}";
        return RuntimeTaskStartResult.Started(startResult.State, message);
    }

    public Task LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        return _runtimeTaskService.LoadSettingsAsync(cancellationToken);
    }

    public void SetEncounterStatisticsEnabled(bool isEnabled)
    {
        _runtimeTaskService.SetEncounterStatisticsEnabled(isEnabled);
    }

    public void SetRecognitionOverlayEnabled(bool isEnabled)
    {
        _runtimeTaskService.SetRecognitionOverlayEnabled(isEnabled);
    }

    public void SetInfoOverlayEnabled(bool isEnabled)
    {
        _runtimeTaskService.SetInfoOverlayEnabled(isEnabled);
    }

    public void SetInfoOverlayLocked(bool isLocked)
    {
        _runtimeTaskService.SetInfoOverlayLocked(isLocked);
    }

    public void SetAutoBattleSettings(AutoBattleSettings settings)
    {
        _runtimeTaskService.SetAutoBattleSettings(settings);
    }

    public void SetRuntimeRecognitionSettings(RuntimeRecognitionSettings settings)
    {
        _runtimeTaskService.SetRuntimeRecognitionSettings(settings);
    }

    public async Task StopAsync()
    {
        _infoOverlayNotificationService.UpdateUidNotice(null);
        await _runtimeTaskService.StopAsync();
    }

    private async Task<StatisticsUidPreparation> PrepareStatisticsUidAsync(
        RuntimeTaskStartOptions options,
        CancellationToken cancellationToken)
    {
        StatisticsUidDetectionResult detectionResult;
        try
        {
            detectionResult = await _statisticsUidDetectionService.DetectAsync(
                options.CaptureMethod,
                options.TextRecognitionMethod,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "点击首页启动时识别统计账号 UID 失败。");
            detectionResult = StatisticsUidDetectionResult.Failed("UID 识别发生异常。");
        }

        if (!detectionResult.Success || string.IsNullOrWhiteSpace(detectionResult.Uid))
        {
            return new StatisticsUidPreparation(detectionResult, ExistingAccountMatched: false);
        }

        var document = await _statisticsService.LoadAsync();
        var existingAccountMatched = document.Accounts.Any(account =>
            string.Equals(account.Uid, detectionResult.Uid, StringComparison.OrdinalIgnoreCase));
        if (existingAccountMatched)
        {
            SelectActiveStatisticsAccount(detectionResult.Uid);
        }

        return new StatisticsUidPreparation(detectionResult, existingAccountMatched);
    }

    private async Task<string> CompleteStatisticsUidSelectionAsync(StatisticsUidPreparation preparation)
    {
        if (preparation.ExistingAccountMatched && preparation.DetectionResult.Uid is { } matchedUid)
        {
            _infoOverlayNotificationService.UpdateUidNotice(null);
            return $"已识别统计账号 UID {matchedUid}。";
        }

        if (!preparation.DetectionResult.Success
            || string.IsNullOrWhiteSpace(preparation.DetectionResult.Uid))
        {
            var failureMessage = preparation.DetectionResult.Message;
            _infoOverlayNotificationService.UpdateUidNotice(new InfoOverlayNotice(
                "UID 识别失败",
                "自动统计已暂停，请在统计页手动选择账号"));
            _logger.LogWarning("首页启动时未能识别统计账号 UID：{Message}", failureMessage);
            return $"{failureMessage} 自动统计已暂停。";
        }

        var detectedUid = preparation.DetectionResult.Uid;
        _infoOverlayNotificationService.UpdateUidNotice(new InfoOverlayNotice(
            "需要确认统计账号",
            $"检测到新 UID {detectedUid}，请回到 RocoPilot 核对"));

        try
        {
            var confirmedUid = await StatisticsUidConfirmationDialog.ShowAsync(
                App.MainWindow.Content?.XamlRoot,
                detectedUid);
            if (confirmedUid is null)
            {
                _statisticsService.RequireActiveAccountSelection();
                _infoOverlayNotificationService.UpdateUidNotice(new InfoOverlayNotice(
                    "UID 尚未确认",
                    "自动统计已暂停，请在统计页手动选择账号"));
                return "UID 尚未确认，自动统计已暂停。";
            }

            var document = await _statisticsService.LoadAsync();
            var accountExists = document.Accounts.Any(account =>
                string.Equals(account.Uid, confirmedUid, StringComparison.OrdinalIgnoreCase));
            if (!accountExists)
            {
                await _statisticsService.AddAccountAsync(confirmedUid);
            }

            SelectActiveStatisticsAccount(confirmedUid);
            _infoOverlayNotificationService.UpdateUidNotice(null);
            return accountExists
                ? $"已使用统计账号 UID {confirmedUid}。"
                : $"已添加并使用统计账号 UID {confirmedUid}。";
        }
        catch (Exception ex)
        {
            _statisticsService.RequireActiveAccountSelection();
            _infoOverlayNotificationService.UpdateUidNotice(new InfoOverlayNotice(
                "UID 确认失败",
                "自动统计已暂停，请在统计页手动选择账号"));
            _logger.LogWarning(ex, "确认首页启动时识别到的统计账号 UID 失败。");
            return "UID 确认失败，自动统计已暂停。";
        }
    }

    private void SelectActiveStatisticsAccount(string uid)
    {
        _statisticsService.SetActiveAccountUid(uid);
        _statisticsService.SetSelectedAccountUid(uid);
    }

    private sealed record StatisticsUidPreparation(
        StatisticsUidDetectionResult DetectionResult,
        bool ExistingAccountMatched);
}
