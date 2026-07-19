using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private static readonly TimeSpan EncounterDuplicateSuppressWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PendingShinyDuplicateSuppressWindow = TimeSpan.FromSeconds(12);

    private const string CaptureButtonEnabledTemplateName = "battle-button-capture.png";
    private const string CaptureButtonDisabledTemplateName = "battle-button-capture-disabled.png";
    private const string CaptureButtonDisabledMarkerTemplateName = "battle-button-capture-disabled-marker.png";
    private const string S3SeasonId = "S3";
    private const string ShinyTipText = "发现异色精灵";
    private const double ShinyTipMatchThreshold = 0.78;
    private const int ShinyTipMinimumTextLength = 4;

    private static readonly string[] BattleTipRegionIds =
    [
        RecognitionRegionIds.BattleMessageTip,
        "battle-tip"
    ];
    private static readonly string[] BattleS3EncounterTipRegionIds =
    [
        RecognitionRegionIds.BattleS3EncounterTip
    ];
    private static readonly string[] BattleShinyTipRegionIds =
    [
        RecognitionRegionIds.BattleShinyTip
    ];
    private static readonly string[] BattleEnemyNameRegionIds =
    [
        RecognitionRegionIds.BattleEnemyName
    ];
    private static readonly string[] BattleCaptureButtonRegionIds =
    [
        RecognitionRegionIds.BattleCaptureButton
    ];
    private static readonly ImageMatchOptions CaptureButtonMatchOptions = new()
    {
        MinimumScore = EncounterCaptureButtonRecognition.MinimumVisibilityScore,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly ImageMatchOptions CaptureButtonDisabledMarkerMatchOptions = new()
    {
        MinimumScore = EncounterCaptureButtonRecognition.DisabledMarkerPresentScore,
        AlphaThreshold = 16,
        SearchStep = 1
    };

    private readonly object _encounterRecordLock = new();
    private readonly object _pendingShinyRecordLock = new();
    private readonly object _runtimeEncounterSignalLock = new();
    private readonly object _encounterCaptureButtonObservationLock = new();
    private readonly EncounterCaptureButtonStateTracker _encounterCaptureButtonStateTracker = new();
    private readonly Dictionary<string, string> _lastAuxiliaryTipTexts =
        new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _encounterStatisticsEnabled = true;
    private bool _hasActiveEncounterRecord;
    private string? _lastRecordedEncounterSeasonId;
    private string? _lastRecordedEncounterName;
    private DateTimeOffset _lastRecordedEncounterAt;
    private bool _hasActivePendingShinyRecord;
    private string? _lastPendingShinySeasonId;
    private string? _lastPendingShinyName;
    private DateTimeOffset _lastPendingShinyAt;
    private bool _hasPendingShinyDetection;
    private string? _pendingShinyDetectionSeasonId;
    private double _pendingShinyDetectionSimilarity;
    private EncounterCaptureButtonObservation? _latestEncounterCaptureButtonObservation;

    public bool EncounterStatisticsEnabled => _encounterStatisticsEnabled;

    public void SetEncounterStatisticsEnabled(bool isEnabled)
    {
        _encounterStatisticsEnabled = isEnabled;
        UpdateInfoOverlayTaskIndicators();
        NotifySettingsChanged();
        _ = SaveEncounterStatisticsEnabledAsync(isEnabled);
    }

    private async Task SaveEncounterStatisticsEnabledAsync(bool isEnabled)
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(SettingsKeys.EncounterStatisticsEnabled, isEnabled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存奇遇统计开关状态失败。");
        }
    }

    private IReadOnlyList<InfoOverlayCounter> GetCurrentSeasonEncounterCounters()
    {
        var season = _encounterSeasonConfigService.GetCurrentSeason();
        if (season is null)
        {
            return [];
        }

        return _statisticsService.GetActiveAccountSeasonEncounters(season.Id)
            .Select(record => new InfoOverlayCounter(
                record.Name,
                record.Count,
                0,
                record.LastCapturedAt))
            .ToList();
    }

    private InfoOverlayPendingShinyCapture? GetCurrentPendingShinyCapture()
    {
        var pendingCapture = _statisticsService
            .GetSelectedAccountPendingShinyCaptures()
            .FirstOrDefault();
        return pendingCapture is null
            ? null
            : new InfoOverlayPendingShinyCapture(
                pendingCapture.Name,
                pendingCapture.Season,
                pendingCapture.DetectedAt);
    }

    private async Task UpdateRuntimeEncounterOcrSignalsAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var season = _encounterSeasonConfigService.GetCurrentSeason();
        if (season is null)
        {
            return;
        }

        var shinyTipTask = TryUpdateRuntimeShinyTipSignalAsync(
            state,
            frame,
            season,
            cancellationToken);
        var battleTipTask = TryLogBattleTipAsync(
            state,
            frame,
            season,
            cancellationToken);
        var s3EncounterTipTask = TryLogS3EncounterTipAsync(
            state,
            frame,
            season,
            cancellationToken);

        await Task.WhenAll(
            shinyTipTask,
            battleTipTask,
            s3EncounterTipTask);
        await TryRecordEncounterAfterRelievedAsync(
            state,
            frame,
            season,
            cancellationToken);
    }

    private async Task TryLogBattleTipAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        EncounterSeasonDefinition season,
        CancellationToken cancellationToken)
    {
        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleTipRegionIds,
            cancellationToken,
            "战斗提示");
        if (!TryRememberAuxiliaryTip(RecognitionRegionIds.BattleMessageTip, tipText))
        {
            return;
        }

        _logger.LogInformation(
            "战斗提示：{TipText}。Season={SeasonId}",
            FormatLogText(tipText),
            season.Id);
    }

    private async Task TryLogS3EncounterTipAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        EncounterSeasonDefinition season,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(season.Id, S3SeasonId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleS3EncounterTipRegionIds,
            cancellationToken,
            "S3 奇遇提示");
        if (!TryRememberAuxiliaryTip(RecognitionRegionIds.BattleS3EncounterTip, tipText))
        {
            return;
        }

        _logger.LogInformation(
            "S3 奇遇血脉提示：{TipText}",
            FormatLogText(tipText));
    }

    private bool TryRememberAuxiliaryTip(string regionId, string tipText)
    {
        var normalizedTipText = TextMatchingHelper.CleanRecognizedText(tipText);
        lock (_runtimeEncounterSignalLock)
        {
            if (normalizedTipText.Length == 0)
            {
                return false;
            }

            if (_lastAuxiliaryTipTexts.TryGetValue(regionId, out var previousTipText)
                && string.Equals(
                    previousTipText,
                    normalizedTipText,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _lastAuxiliaryTipTexts[regionId] = normalizedTipText;
            return true;
        }
    }

    private async Task UpdateEncounterCaptureButtonStateAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        if (IsAutoBattleBossBattle)
        {
            return;
        }

        var season = _encounterSeasonConfigService.GetCurrentSeason();
        if (season is null)
        {
            return;
        }

        var observation = await RecognizeEncounterCaptureButtonStateAsync(
            state,
            frame,
            cancellationToken);
        RememberEncounterCaptureButtonObservation(observation);
        ApplyEncounterCaptureButtonState(season, observation.State);
    }

    private async Task<EncounterCaptureButtonObservation> RecognizeEncounterCaptureButtonStateAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var disabledMarkerTemplatePath = GetResolutionTemplatePath(
            state.RecognitionRegionConfig,
            CaptureButtonDisabledMarkerTemplateName);
        if (!TemplateExists(disabledMarkerTemplatePath))
        {
            LogDebugOncePerValue(
                CreateDebugLogKey(
                    "encounter-capture-button-disabled-marker-missing",
                    disabledMarkerTemplatePath),
                "missing",
                "奇遇捕捉按钮筛选跳过：未找到捕捉按钮禁用标志模板。Template={Template}",
                disabledMarkerTemplatePath);
            return new EncounterCaptureButtonObservation(
                EncounterCaptureButtonState.Unknown,
                0,
                0,
                0);
        }

        var enabledMatchTask = MatchRuntimeTemplateResultAsync(
            state,
            frame,
            BattleCaptureButtonRegionIds,
            CaptureButtonEnabledTemplateName,
            CaptureButtonMatchOptions,
            "奇遇识别",
            "可捕捉按钮",
            cancellationToken);
        var disabledMatchTask = MatchRuntimeTemplateResultAsync(
            state,
            frame,
            BattleCaptureButtonRegionIds,
            CaptureButtonDisabledTemplateName,
            CaptureButtonMatchOptions,
            "奇遇识别",
            "禁用捕捉按钮",
            cancellationToken);
        var disabledMarkerMatchTask = MatchRuntimeTemplateResultAsync(
            state,
            frame,
            BattleCaptureButtonRegionIds,
            CaptureButtonDisabledMarkerTemplateName,
            CaptureButtonDisabledMarkerMatchOptions,
            "奇遇识别",
            "捕捉按钮禁用标志",
            cancellationToken);

        await Task.WhenAll(
            enabledMatchTask,
            disabledMatchTask,
            disabledMarkerMatchTask);
        var enabledMatch = await enabledMatchTask;
        var disabledMatch = await disabledMatchTask;
        var disabledMarkerMatch = await disabledMarkerMatchTask;
        var buttonState = EncounterCaptureButtonRecognition.Classify(
            enabledMatch.Score,
            disabledMatch.Score,
            disabledMarkerMatch.Score);
        return new EncounterCaptureButtonObservation(
            buttonState,
            enabledMatch.Score,
            disabledMatch.Score,
            disabledMarkerMatch.Score);
    }

    private void RememberEncounterCaptureButtonObservation(
        EncounterCaptureButtonObservation observation)
    {
        lock (_encounterCaptureButtonObservationLock)
        {
            _latestEncounterCaptureButtonObservation = observation;
        }
    }

    private void LogAdoptedEncounterCaptureButtonObservationForCurrentTurn()
    {
        if (IsAutoBattleBossBattle
            || _hasLoggedCurrentAutoBattleCaptureButtonObservation)
        {
            return;
        }

        EncounterCaptureButtonObservation? observation;
        lock (_encounterCaptureButtonObservationLock)
        {
            observation = _latestEncounterCaptureButtonObservation;
        }

        if (observation is null)
        {
            return;
        }

        var turnNumber = _currentAutoBattleTurnNumber > 0
            ? _currentAutoBattleTurnNumber
            : 1;
        _logger.LogDebug(
            "自动战斗：第 {TurnNumber} 回合采用捕捉按钮识别结果：State={State}, EnabledScore={EnabledScore:F3}, DisabledScore={DisabledScore:F3}, DisabledMarkerScore={DisabledMarkerScore:F3}",
            turnNumber,
            observation.State,
            observation.EnabledScore,
            observation.DisabledScore,
            observation.DisabledMarkerScore);
        _hasLoggedCurrentAutoBattleCaptureButtonObservation = true;
    }

    private void ApplyEncounterCaptureButtonState(
        EncounterSeasonDefinition season,
        EncounterCaptureButtonState buttonState)
    {
        if (_encounterCaptureButtonStateTracker.Observe(buttonState))
        {
            _logger.LogInformation(
                "奇遇识别：捕捉按钮已由禁用变为可用，判定本场奇遇效果解除。Season={SeasonId}",
                season.Id);
            ApplyAutoBattleEncounterRelievedDetection("捕捉按钮状态");
        }
    }

    private async Task TryRecordEncounterAfterRelievedAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        EncounterSeasonDefinition season,
        CancellationToken cancellationToken)
    {
        if (!EncounterStatisticsEnabled
            || !_encounterCaptureButtonStateTracker.IsRelieved
            || HasActiveEncounterRecord())
        {
            return;
        }

        var enemyNameText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleEnemyNameRegionIds,
            cancellationToken,
            "奇遇解除精灵名");
        var enemyName = await MatchRecognizedSpiritNameAsync(enemyNameText, cancellationToken);
        var spiritNameMatchThreshold = GetSpiritNameMatchThreshold();
        LogDebugOncePerValue(
            CreateDebugLogKey("encounter-relieved-enemy-filter", season.Id),
            string.Join(
                "|",
                CreateTextDebugFingerprint(enemyNameText),
                CreateTextDebugFingerprint(enemyName),
                CreateBooleanDebugFingerprint(!string.IsNullOrWhiteSpace(enemyName))),
            "奇遇解除精灵名筛选：EnemyNameRaw={EnemyNameRaw}, Matched={SpiritName}, IsValid={IsValid}, MatchThreshold={MatchThreshold:P1}",
            FormatLogText(enemyNameText),
            enemyName,
            !string.IsNullOrWhiteSpace(enemyName),
            spiritNameMatchThreshold);
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            return;
        }

        await RecordEncounterAsync(
            season,
            enemyName,
            DateTimeOffset.Now,
            cancellationToken);
    }

    private async Task<bool> TryUpdateRuntimeShinyTipSignalAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        EncounterSeasonDefinition season,
        CancellationToken cancellationToken)
    {
        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleShinyTipRegionIds,
            cancellationToken,
            "异色识别");

        var isTipMatch = IsShinyTip(tipText, out var similarity);
        LogDebugOncePerValue(
            CreateDebugLogKey("runtime-shiny-tip-filter", season.Id),
            CreateMatchFilterDebugFingerprint(tipText, similarity, isTipMatch),
            "异色识别筛选：TipText={TipText}, Expected={ExpectedTipText}, Similarity={Similarity:P1}, Threshold={Threshold:P1}, IsMatch={IsMatch}",
            FormatLogText(tipText),
            ShinyTipText,
            similarity,
            ShinyTipMatchThreshold,
            isTipMatch);
        if (!isTipMatch)
        {
            return false;
        }

        RememberPendingShinyDetection(season.Id, similarity);
        ApplyAutoBattleShinySuspension(tipText, "异色识别");
        return true;
    }

    private async Task RecordEncounterAsync(
        EncounterSeasonDefinition season,
        string enemyName,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!EncounterStatisticsEnabled)
        {
            return;
        }

        enemyName = await ResolveEncounterStatisticsRecordNameAsync(enemyName, cancellationToken);
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            return;
        }

        if (!TryReserveEncounterRecord(season.Id, enemyName, now))
        {
            return;
        }

        var previousCount = GetEncounterCount(season.Id, enemyName);
        await _statisticsService.RecordEncounterAsync(season, enemyName, now);
        var currentCount = GetEncounterCount(season.Id, enemyName);
        if (currentCount > previousCount)
        {
            _logger.LogInformation(
                "奇遇统计：{SpiritName} 奇遇 +1（当前 {Count}）",
                enemyName,
                currentCount);
        }

        _logger.LogDebug(
            "奇遇统计已记录：Season={SeasonId}, Type={EncounterType}, Spirit={SpiritName}, PreviousCount={PreviousCount}, CurrentCount={CurrentCount}, Detection=CaptureButtonTransition",
            season.Id,
            season.EncounterTypeName,
            enemyName,
            previousCount,
            currentCount);
    }

    private async Task RecordPendingShinyCaptureAsync(
        EncounterSeasonDefinition season,
        string enemyName,
        CancellationToken cancellationToken)
    {
        if (!EncounterStatisticsEnabled)
        {
            return;
        }

        enemyName = await ResolveEncounterStatisticsRecordNameAsync(enemyName, cancellationToken);
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (!TryReservePendingShinyRecord(season.Id, enemyName, now))
        {
            return;
        }

        await _statisticsService.AddPendingShinyCaptureAsync(season, enemyName, now);
        _logger.LogInformation(
            "异色识别：{SpiritName} 已暂存，等待统计页面确认后计入异色并清空对应赛季奇遇计数。",
            enemyName);
    }

    private async Task TryUpdatePendingShinyCaptureAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        EncounterSeasonDefinition season,
        CancellationToken cancellationToken)
    {
        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleShinyTipRegionIds,
            cancellationToken,
            "异色识别");

        var isTipMatch = IsShinyTip(tipText, out var similarity);
        LogDebugOncePerValue(
            CreateDebugLogKey("shiny-tip-filter", season.Id),
            CreateMatchFilterDebugFingerprint(tipText, similarity, isTipMatch),
            "异色识别筛选：TipText={TipText}, Expected={ExpectedTipText}, Similarity={Similarity:P1}, Threshold={Threshold:P1}, IsMatch={IsMatch}",
            FormatLogText(tipText),
            ShinyTipText,
            similarity,
            ShinyTipMatchThreshold,
            isTipMatch);
        if (!isTipMatch)
        {
            return;
        }

        ApplyAutoBattleShinySuspension(tipText, "异色识别");

        var enemyNameText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleEnemyNameRegionIds,
            cancellationToken,
            "异色识别");
        var enemyName = await MatchRecognizedSpiritNameAsync(enemyNameText, cancellationToken);
        var spiritNameMatchThreshold = GetSpiritNameMatchThreshold();
        LogDebugOncePerValue(
            CreateDebugLogKey("shiny-enemy-filter", season.Id),
            string.Join(
                "|",
                CreateTextDebugFingerprint(enemyNameText),
                CreateTextDebugFingerprint(enemyName),
                CreateBooleanDebugFingerprint(!string.IsNullOrWhiteSpace(enemyName))),
            "异色识别筛选：EnemyNameRaw={EnemyNameRaw}, Matched={SpiritName}, IsValid={IsValid}, MatchThreshold={MatchThreshold:P1}",
            FormatLogText(enemyNameText),
            enemyName,
            !string.IsNullOrWhiteSpace(enemyName),
            spiritNameMatchThreshold);
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            LogDebugOncePerValue(
                CreateDebugLogKey("shiny-missing-enemy", season.Id),
                CreateSimilarityDebugFingerprint(similarity),
                "已匹配异色提示，但 battle-enemy-name 区域未识别到精灵名。相似度：{Similarity:P1}",
                similarity);
            return;
        }

        enemyName = await ResolveEncounterStatisticsRecordNameAsync(enemyName, cancellationToken);
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (!TryReservePendingShinyRecord(season.Id, enemyName, now))
        {
            return;
        }

        await _statisticsService.AddPendingShinyCaptureAsync(season, enemyName, now);
        _logger.LogInformation(
            "异色识别：{SpiritName} 已暂存，等待统计页面确认后计入异色并清空对应赛季奇遇计数。",
            enemyName);
    }

    private bool TryReserveEncounterRecord(string seasonId, string spiritName, DateTimeOffset now)
    {
        lock (_encounterRecordLock)
        {
            if (string.Equals(_lastRecordedEncounterSeasonId, seasonId, StringComparison.OrdinalIgnoreCase)
                && (_hasActiveEncounterRecord || now - _lastRecordedEncounterAt < EncounterDuplicateSuppressWindow))
            {
                var remaining = _hasActiveEncounterRecord
                    ? EncounterDuplicateSuppressWindow
                    : EncounterDuplicateSuppressWindow - (now - _lastRecordedEncounterAt);
                LogDebugOncePerValue(
                    CreateDebugLogKey("encounter-duplicate-suppression", seasonId),
                    string.Join(
                        "|",
                        _lastRecordedEncounterName,
                        spiritName,
                        CreateBooleanDebugFingerprint(_hasActiveEncounterRecord)),
                    "奇遇统计筛选：冷却中，本次识别已忽略。LastSpirit={LastSpiritName}, CurrentSpirit={CurrentSpiritName}, Remaining={RemainingSeconds:F1}s",
                    _lastRecordedEncounterName,
                    spiritName,
                    Math.Max(0, remaining.TotalSeconds));
                return false;
            }

            _lastRecordedEncounterSeasonId = seasonId;
            _lastRecordedEncounterName = spiritName;
            _lastRecordedEncounterAt = now;
            _hasActiveEncounterRecord = true;
            return true;
        }
    }

    private bool HasActiveEncounterRecord()
    {
        lock (_encounterRecordLock)
        {
            return _hasActiveEncounterRecord;
        }
    }

    private bool TryReservePendingShinyRecord(string seasonId, string spiritName, DateTimeOffset now)
    {
        lock (_pendingShinyRecordLock)
        {
            if (string.Equals(_lastPendingShinySeasonId, seasonId, StringComparison.OrdinalIgnoreCase)
                && (_hasActivePendingShinyRecord || now - _lastPendingShinyAt < PendingShinyDuplicateSuppressWindow))
            {
                var remaining = _hasActivePendingShinyRecord
                    ? PendingShinyDuplicateSuppressWindow
                    : PendingShinyDuplicateSuppressWindow - (now - _lastPendingShinyAt);
                LogDebugOncePerValue(
                    CreateDebugLogKey("shiny-duplicate-suppression", seasonId),
                    string.Join(
                        "|",
                        _lastPendingShinyName,
                        spiritName,
                        CreateBooleanDebugFingerprint(_hasActivePendingShinyRecord)),
                    "异色识别筛选：冷却中，本次识别已忽略。LastSpirit={LastSpiritName}, CurrentSpirit={CurrentSpiritName}, Remaining={RemainingSeconds:F1}s",
                    _lastPendingShinyName,
                    spiritName,
                    Math.Max(0, remaining.TotalSeconds));
                return false;
            }

            _lastPendingShinySeasonId = seasonId;
            _lastPendingShinyName = spiritName;
            _lastPendingShinyAt = now;
            _hasActivePendingShinyRecord = true;
            return true;
        }
    }

    private void RememberPendingShinyDetection(string seasonId, double similarity)
    {
        lock (_runtimeEncounterSignalLock)
        {
            _hasPendingShinyDetection = true;
            _pendingShinyDetectionSeasonId = seasonId;
            _pendingShinyDetectionSimilarity = similarity;
        }
    }

    private bool TryGetPendingShinyDetection(string seasonId, out double similarity)
    {
        lock (_runtimeEncounterSignalLock)
        {
            if (!_hasPendingShinyDetection
                || !string.Equals(_pendingShinyDetectionSeasonId, seasonId, StringComparison.OrdinalIgnoreCase))
            {
                similarity = 0;
                return false;
            }

            similarity = _pendingShinyDetectionSimilarity;
            return true;
        }
    }

    private void ClearPendingShinyDetection()
    {
        lock (_runtimeEncounterSignalLock)
        {
            _hasPendingShinyDetection = false;
            _pendingShinyDetectionSeasonId = null;
            _pendingShinyDetectionSimilarity = 0;
        }
    }

    private void ResetEncounterRecordSuppression()
    {
        lock (_encounterRecordLock)
        {
            _hasActiveEncounterRecord = false;
        }

        lock (_pendingShinyRecordLock)
        {
            _hasActivePendingShinyRecord = false;
        }

        _encounterCaptureButtonStateTracker.Reset();
        lock (_encounterCaptureButtonObservationLock)
        {
            _latestEncounterCaptureButtonObservation = null;
        }

        lock (_runtimeEncounterSignalLock)
        {
            _lastAuxiliaryTipTexts.Clear();
        }

        ClearPendingShinyDetection();
    }

    private int GetEncounterCount(string seasonId, string spiritName)
    {
        var record = _statisticsService
            .GetActiveAccountSeasonEncounters(seasonId)
            .FirstOrDefault(record => TextMatchingHelper.AreSameSpiritName(record.Name, spiritName));
        return record?.Count ?? 0;
    }

    private async Task<string> MatchRecognizedSpiritNameAsync(
        string recognizedText,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _spiritCatalogService.MatchSpiritNameAsync(
                recognizedText,
                GetSpiritNameMatchThreshold(),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "精灵名图鉴匹配失败，本次 OCR 精灵名已跳过。");
            return string.Empty;
        }
    }

    private double GetSpiritNameMatchThreshold()
    {
        return _encounterSeasonConfigService.Load().SpiritNameMatchThreshold;
    }

    private async Task<string> ResolveEncounterStatisticsRecordNameAsync(
        string spiritName,
        CancellationToken cancellationToken)
    {
        var normalizedName = TextMatchingHelper.NormalizeSpiritNameForDisplay(spiritName);
        if (normalizedName.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            var resolvedName = await _spiritCatalogService.ResolveEvolutionRecordNameAsync(
                normalizedName,
                cancellationToken);
            return string.IsNullOrWhiteSpace(resolvedName)
                ? normalizedName
                : resolvedName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "精灵进化链最低阶统计名解析失败，已使用匹配精灵名。Spirit={SpiritName}",
                normalizedName);
            return normalizedName;
        }
    }

    private static bool IsShinyTip(string tipText, out double similarity)
    {
        var normalized = TextMatchingHelper.CleanRecognizedText(tipText);
        if (normalized.Length < ShinyTipMinimumTextLength)
        {
            similarity = 0;
            return false;
        }

        if (normalized.Contains("异色", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains("精灵", StringComparison.OrdinalIgnoreCase))
        {
            similarity = 1;
            return true;
        }

        similarity = Math.Max(
            TextMatchingHelper.CalculateSimilarity(normalized, ShinyTipText),
            TextMatchingHelper.CalculateSimilarity(normalized, "异色精灵"));
        return similarity >= ShinyTipMatchThreshold;
    }

    private sealed record EncounterCaptureButtonObservation(
        EncounterCaptureButtonState State,
        double EnabledScore,
        double DisabledScore,
        double DisabledMarkerScore);
}
