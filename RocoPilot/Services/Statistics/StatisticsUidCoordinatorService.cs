using Microsoft.Extensions.Logging;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.Statistics;

namespace RocoPilot.Services;

public sealed class StatisticsUidCoordinatorService :
    IStatisticsUidCoordinatorService
{
    private readonly IStatisticsUidDetectionService _statisticsUidDetectionService;
    private readonly IStatisticsService _statisticsService;
    private readonly IInfoOverlayNotificationService _infoOverlayNotificationService;
    private readonly ILogger<StatisticsUidCoordinatorService> _logger;
    private StatisticsUidConfirmationRequest? _pendingConfirmation;

    public StatisticsUidCoordinatorService(
        IStatisticsUidDetectionService statisticsUidDetectionService,
        IStatisticsService statisticsService,
        IInfoOverlayNotificationService infoOverlayNotificationService,
        ILogger<StatisticsUidCoordinatorService> logger)
    {
        _statisticsUidDetectionService = statisticsUidDetectionService;
        _statisticsService = statisticsService;
        _infoOverlayNotificationService = infoOverlayNotificationService;
        _logger = logger;
    }

    public event EventHandler? PendingConfirmationChanged;

    public StatisticsUidConfirmationRequest? PendingConfirmation => Volatile.Read(ref _pendingConfirmation);

    internal async Task<StatisticsUidPreparation?> PrepareStartAsync(RuntimeTaskStartOptions options, CancellationToken cancellationToken)
    {
        Clear();
        if (!options.EncounterStatisticsEnabled) return null;
        var selectedAccountUid = await ResolveSelectedAccountUidAsync();
        _statisticsService.RequireActiveAccountSelection();
        try
        {
            return await PrepareStatisticsUidAsync(options, selectedAccountUid, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "准备首页启动使用的统计账号 UID 失败。");
            return new StatisticsUidPreparation(
                StatisticsUidDetectionResult.Failed("UID 识别准备失败。"),
                new StatisticsUidSelectionDecision(StatisticsUidSelectionAction.RequireConfirmation, null, "UID 识别准备失败。"),
                options.CaptureMethod, options.TextRecognitionMethod);
        }
    }

    internal string CompleteStart(StatisticsUidPreparation? preparation)
    {
        return preparation is null ? string.Empty : CompleteStatisticsUidSelection(preparation);
    }

    internal void Clear()
    {
        ClearPendingConfirmation();
        _infoOverlayNotificationService.UpdateUidNotice(null);
    }

    public void MarkPendingConfirmationPresented()
    {
        var pending = PendingConfirmation;
        if (pending is null || pending.WasPresented)
        {
            return;
        }

        SetPendingConfirmation(pending with { WasPresented = true });
    }

    public async Task<StatisticsUidDetectionResult> RetryDetectionAsync(
        CancellationToken cancellationToken = default)
    {
        var pending = PendingConfirmation;
        if (pending is null)
        {
            return StatisticsUidDetectionResult.Failed("当前没有待确认的统计账号。");
        }

        StatisticsUidDetectionResult result;
        try
        {
            result = await _statisticsUidDetectionService.DetectAsync(
                pending.CaptureMethod,
                pending.TextRecognitionMethod,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "重新识别统计账号 UID 失败。");
            result = StatisticsUidDetectionResult.Failed("重新识别 UID 时发生异常。");
        }

        var suggestedUid = result.Success ? result.Uid : null;
        SetPendingConfirmation(pending with
        {
            SuggestedUid = suggestedUid ?? _statisticsService.SelectedAccountUid,
            Message = result.Message,
            RecognitionSucceeded = result.Success,
            WasPresented = true
        });
        UpdatePendingOverlay(result);
        return result;
    }

    public async Task<string> ConfirmUidAsync(
        string uid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!StatisticsUidRules.TryNormalize(uid, out var confirmedUid))
        {
            throw new ArgumentException("UID 中没有可用的数字。", nameof(uid));
        }

        var document = await _statisticsService.LoadAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (!document.Accounts.Any(account =>
                string.Equals(account.Uid, confirmedUid, StringComparison.OrdinalIgnoreCase)))
        {
            await _statisticsService.AddAccountAsync(confirmedUid);
        }

        SelectActiveStatisticsAccount(confirmedUid);
        ClearPendingConfirmation();
        _infoOverlayNotificationService.UpdateUidNotice(null);
        return confirmedUid;
    }

    private async Task<StatisticsUidPreparation> PrepareStatisticsUidAsync(
        RuntimeTaskStartOptions options,
        string? selectedAccountUid,
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

        var decision = StatisticsUidSelectionRules.Decide(detectionResult, selectedAccountUid);
        return new StatisticsUidPreparation(
            detectionResult,
            decision,
            options.CaptureMethod,
            options.TextRecognitionMethod);
    }

    private string CompleteStatisticsUidSelection(StatisticsUidPreparation preparation)
    {
        if (preparation.Decision.Action == StatisticsUidSelectionAction.UseRecognizedUid
            && preparation.Decision.SuggestedUid is { } matchedUid)
        {
            SelectActiveStatisticsAccount(matchedUid);
            ClearPendingConfirmation();
            _infoOverlayNotificationService.UpdateUidNotice(null);
            return $"已识别统计账号 UID {matchedUid}。";
        }

        SetPendingConfirmation(new StatisticsUidConfirmationRequest(
            preparation.Decision.SuggestedUid ?? _statisticsService.SelectedAccountUid,
            preparation.Decision.Message,
            preparation.CaptureMethod,
            preparation.TextRecognitionMethod,
            preparation.DetectionResult.Success));
        UpdatePendingOverlay(preparation.DetectionResult);
        _logger.LogWarning(
            "首页启动时统计账号 UID 需要确认：{Message}",
            preparation.Decision.Message);
        return $"{preparation.Decision.Message} 请前往统计页确认账号，确认前自动统计暂停。";
    }

    private void SelectActiveStatisticsAccount(string uid)
    {
        _statisticsService.SetActiveAccountUid(uid);
        _statisticsService.SetSelectedAccountUid(uid);
    }

    private async Task<string?> ResolveSelectedAccountUidAsync()
    {
        var document = await _statisticsService.LoadAsync();
        var selectedUid = _statisticsService.SelectedAccountUid;
        if (!string.IsNullOrWhiteSpace(selectedUid)
            && document.Accounts.Any(account =>
                string.Equals(account.Uid, selectedUid, StringComparison.OrdinalIgnoreCase)))
        {
            return selectedUid;
        }

        selectedUid = document.Accounts.FirstOrDefault()?.Uid;
        if (!string.IsNullOrWhiteSpace(selectedUid))
        {
            _statisticsService.SetSelectedAccountUid(selectedUid);
        }

        return selectedUid;
    }

    private void UpdatePendingOverlay(StatisticsUidDetectionResult detectionResult)
    {
        _infoOverlayNotificationService.UpdateUidNotice(
            detectionResult.Success && !string.IsNullOrWhiteSpace(detectionResult.Uid)
                ? new InfoOverlayNotice(
                    "需要确认统计账号",
                    $"检测到 UID {detectionResult.Uid}，请前往统计页核对")
                : new InfoOverlayNotice(
                    "UID 识别失败",
                    "自动统计已暂停，请前往统计页手动输入或重新识别"));
    }

    private void SetPendingConfirmation(StatisticsUidConfirmationRequest pending)
    {
        Volatile.Write(ref _pendingConfirmation, pending);
        PendingConfirmationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearPendingConfirmation()
    {
        if (Interlocked.Exchange(ref _pendingConfirmation, null) is null)
        {
            return;
        }

        PendingConfirmationChanged?.Invoke(this, EventArgs.Empty);
    }

    internal sealed record StatisticsUidPreparation(
        StatisticsUidDetectionResult DetectionResult,
        StatisticsUidSelectionDecision Decision,
        Models.Capture.CaptureMethod CaptureMethod,
        Models.TextRecognition.TextRecognitionMethod TextRecognitionMethod);
}
