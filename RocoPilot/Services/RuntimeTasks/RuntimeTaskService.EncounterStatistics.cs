using Microsoft.Extensions.Logging;
using RocoPilot.Configuration;
using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.ImageMatching;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.Runtime;
using RocoPilot.Services.RuntimeTasks;
using RocoPilot.Services.Encounters;
using static RocoPilot.Services.RuntimeTasks.RuntimeDebugLogger;
using static RocoPilot.Services.RuntimeTasks.RuntimeFrameRecognizer;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private static readonly TimeSpan EncounterDuplicateSuppressWindow = TimeSpan.FromSeconds(6);

    private const string CaptureButtonEnabledTemplateName = "battle-button-capture.png";
    private const string CaptureButtonDisabledTemplateName = "battle-button-capture-disabled.png";
    private const string CaptureButtonDisabledMarkerTemplateName = "battle-button-capture-disabled-marker.png";
    private const int AuxiliaryTipMinimumChineseCharacterCount = 3;
    private const string ShinyTipText = "发现异色精灵";
    private const double ShinyTipMatchThreshold = 0.78;
    private const int ShinyTipMinimumTextLength = 4;

    private static readonly string[] BattleTipRegionIds =
    [
        RecognitionRegionIds.BattleMessageTip,
        "battle-tip"
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
    private static readonly ImageMatchOptions CaptureButtonEnabledMatchOptions = new()
    {
        MinimumScore = EncounterCaptureButtonRecognition.ButtonPresentScore,
        AlphaThreshold = 16,
        SearchStep = 1
    };
    private static readonly ImageMatchOptions CaptureButtonDisabledMatchOptions = new()
    {
        MinimumScore = EncounterCaptureButtonRecognition.ButtonPresentScore,
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
    private string? _encounterRecordId;
    private string? _lastRecordedEncounterSeasonId;
    private string? _lastRecordedEncounterName;
    private DateTimeOffset _lastRecordedEncounterAt;
    private RuntimePendingShinyCapture? _pendingShinyCapture;
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

        season = EncounterSeasonTimeline.ResolveForRecording(_encounterSeasonConfigService.Load(), DateTimeOffset.Now, season);
        if (season.Id == EncounterSeasonTimeline.PendingSeasonId) return [];

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
        var pendingCaptures = _statisticsService.GetSelectedAccountPendingShinyCaptures();
        var pendingCapture = pendingCaptures.FirstOrDefault();
        return pendingCapture is null
            ? null
            : new InfoOverlayPendingShinyCapture(
                string.IsNullOrWhiteSpace(pendingCapture.Name) ? "未识别精灵" : pendingCapture.Name,
                pendingCapture.Season == EncounterSeasonTimeline.PendingSeasonId
                    ? EncounterSeasonTimeline.PendingSeasonName : pendingCapture.Season,
                pendingCapture.DetectedAt,
                pendingCaptures.Count);
    }

    private async Task UpdateRuntimeEncounterOcrSignalsAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        long battleId,
        CancellationToken cancellationToken)
    {
        var bloodlineTipTask = RecognizeAndApplyBloodlineTipAsync(state, frame, battleId, cancellationToken);
        var season = _encounterSeasonConfigService.GetCurrentSeason();
        if (season is null)
        {
            await bloodlineTipTask;
            return;
        }

        var shinyTipTask = TryUpdateRuntimeShinyTipSignalAsync(
            state,
            frame,
            season,
            battleId,
            cancellationToken);
        var battleTipTask = TryLogBattleTipAsync(
            state,
            frame,
            season,
            cancellationToken);
        await Task.WhenAll(
            shinyTipTask,
            battleTipTask,
            bloodlineTipTask);
        if (battleId != _battle.BattleId) return;
        await TryRecordEncounterAfterRelievedAsync(
            state,
            frame,
            season,
            battleId,
            cancellationToken);
        await TryCompletePendingShinyNameAsync(state, frame, battleId, cancellationToken);
    }

    private async Task TryLogBattleTipAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        EncounterSeasonDefinition season,
        CancellationToken cancellationToken)
    {
        var tipText = await _frameRecognizer.RecognizeRegionTextAsync(
            state,
            frame,
            BattleTipRegionIds,
            cancellationToken,
            "战斗提示");
        if (TextMatchingHelper.CountChineseCharacters(tipText) < AuxiliaryTipMinimumChineseCharacterCount
            || !TryRememberAuxiliaryTip(RecognitionRegionIds.BattleMessageTip, tipText))
        {
            return;
        }

        _logger.LogInformation(
            "战斗提示：{TipText}。Season={SeasonId}",
            FormatLogText(tipText),
            season.Id);
    }

    private async Task RecognizeAndApplyBloodlineTipAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        long battleId,
        CancellationToken cancellationToken)
    {
        if (!EncounterBloodlineRecognition.IsAvailable(state.RecognitionRegionConfig))
        {
            return;
        }

        var tipText = await _frameRecognizer.RecognizeRegionTextAsync(
            state,
            frame,
            EncounterBloodlineRecognition.RegionIds,
            cancellationToken,
            "奇遇血脉提示");
        if (string.IsNullOrWhiteSpace(tipText))
        {
            return;
        }

        var hasParsedKind = EncounterBloodlineRecognition.TryParse(tipText, out var kind);
        if (hasParsedKind)
        {
            _battle.ObserveBloodline(battleId, kind);
        }

        if (!TryRememberAuxiliaryTip(RecognitionRegionIds.BattleBloodlineTip, tipText))
        {
            return;
        }

        _logger.LogDebug(
            "奇遇血脉提示：{TipText}，Bloodline={Bloodline}",
            FormatLogText(tipText),
            EncounterBloodlineRecognition.GetDisplayName(
                hasParsedKind ? kind : EncounterBloodlineKind.Unrecognized));
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
        ApplyEncounterCaptureButtonState(season, observation);
    }

    private async Task<EncounterCaptureButtonObservation> RecognizeEncounterCaptureButtonStateAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var matchAlgorithm = _imageMatchingService.DefaultAlgorithm;
        var disabledMarkerTemplatePath = GetResolutionTemplatePath(
            state.RecognitionRegionConfig,
            CaptureButtonDisabledMarkerTemplateName);
        if (!_frameRecognizer.TemplateExists(disabledMarkerTemplatePath))
        {
            _debugLog.Write(
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
                0,
                matchAlgorithm);
        }

        var enabledMatchTask = _frameRecognizer.MatchRuntimeTemplateResultAsync(
            state,
            frame,
            BattleCaptureButtonRegionIds,
            CaptureButtonEnabledTemplateName,
            CaptureButtonEnabledMatchOptions,
            "奇遇识别",
            "可捕捉按钮",
            cancellationToken);
        var disabledMatchTask = _frameRecognizer.MatchRuntimeTemplateResultAsync(
            state,
            frame,
            BattleCaptureButtonRegionIds,
            CaptureButtonDisabledTemplateName,
            CaptureButtonDisabledMatchOptions,
            "奇遇识别",
            "禁用捕捉按钮",
            cancellationToken);
        await Task.WhenAll(
            enabledMatchTask,
            disabledMatchTask);
        var enabledMatch = await enabledMatchTask;
        var disabledMatch = await disabledMatchTask;
        var anchorMatch = enabledMatch.Score >= disabledMatch.Score
            ? enabledMatch
            : disabledMatch;
        var alignedMarkerRegion = new RecognitionRegion
        {
            Id = RecognitionRegionIds.BattleCaptureButton,
            X = anchorMatch.X,
            Y = anchorMatch.Y,
            Width = anchorMatch.Width,
            Height = anchorMatch.Height,
            Enabled = true
        };
        var disabledMarkerMatchOptions = CreateScaledImageMatchOptions(
            CaptureButtonDisabledMarkerMatchOptions,
            frame,
            state.TargetWindow,
            state.RecognitionRegionConfig);
        var disabledMarkerMatch = await _imageMatchingService.MatchAsync(
            frame,
            alignedMarkerRegion,
            disabledMarkerTemplatePath,
            disabledMarkerMatchOptions,
            cancellationToken);
        var captureButtonRegion = FindRegion(
            state.RecognitionRegionConfig,
            BattleCaptureButtonRegionIds);
        _recognitionOverlayService.ShowImageMatchResult(
            captureButtonRegion.Id,
            disabledMarkerMatch.Score);
        var buttonState = EncounterCaptureButtonRecognition.Classify(
            enabledMatch.Score,
            disabledMatch.Score,
            disabledMarkerMatch.Score);
        return new EncounterCaptureButtonObservation(
            buttonState,
            enabledMatch.Score,
            disabledMatch.Score,
            disabledMarkerMatch.Score,
            matchAlgorithm);
    }

    private void RememberEncounterCaptureButtonObservation(
        EncounterCaptureButtonObservation observation)
    {
        lock (_encounterCaptureButtonObservationLock)
        {
            _latestEncounterCaptureButtonObservation = observation;
        }
    }

    private void LogEncounterCaptureButtonDecisionForCurrentTurn(string decision)
    {
        if (_hasLoggedCurrentAutoBattleCaptureButtonObservation)
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

        var turnNumber = _battle.TurnNumber > 0
            ? _battle.TurnNumber
            : 1;
        _logger.LogDebug(
            "自动战斗：第 {TurnNumber} 回合捕捉按钮判定：State={State}, Algorithm={Algorithm}, EnabledScore={EnabledScore:F3}, DisabledScore={DisabledScore:F3}, DisabledMarkerScore={DisabledMarkerScore:F3}, EnabledConfirmations={EnabledConfirmationCount}/{RequiredEnabledConfirmationCount}, Decision={Decision}",
            turnNumber,
            observation.State,
            observation.Algorithm,
            observation.EnabledScore,
            observation.DisabledScore,
            observation.DisabledMarkerScore,
            _encounterCaptureButtonStateTracker.EnabledConfirmationCount,
            EncounterCaptureButtonStateTracker.RequiredEnabledConfirmationCount,
            decision);
        _hasLoggedCurrentAutoBattleCaptureButtonObservation = true;
    }

    private void ApplyEncounterCaptureButtonState(
        EncounterSeasonDefinition season,
        EncounterCaptureButtonObservation observation)
    {
        if (_encounterCaptureButtonStateTracker.Observe(observation.State))
        {
            _logger.LogInformation(
                "奇遇识别：捕捉按钮已由禁用变为可用，判定本场奇遇效果解除。Season={SeasonId}, Algorithm={Algorithm}, EnabledScore={EnabledScore:F3}, DisabledScore={DisabledScore:F3}, DisabledMarkerScore={DisabledMarkerScore:F3}, EnabledConfirmations={EnabledConfirmationCount}/{RequiredEnabledConfirmationCount}",
                season.Id,
                observation.Algorithm,
                observation.EnabledScore,
                observation.DisabledScore,
                observation.DisabledMarkerScore,
                _encounterCaptureButtonStateTracker.EnabledConfirmationCount,
                EncounterCaptureButtonStateTracker.RequiredEnabledConfirmationCount);
            ApplyAutoBattleEncounterRelievedDetection("捕捉按钮状态");
        }
    }

    private async Task TryRecordEncounterAfterRelievedAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        EncounterSeasonDefinition season,
        long battleId,
        CancellationToken cancellationToken)
    {
        if (!EncounterStatisticsEnabled
            || !_encounterCaptureButtonStateTracker.IsRelieved
            || HasActiveEncounterRecord()
            || _statisticsService.IsActiveAccountSelectionRequired)
        {
            return;
        }

        // 在异步 OCR 前固定这次奇遇的账号和时间，避免切换账号后记到其他账号。
        var accountUid = _statisticsService.ActiveAccountUid ?? _statisticsService.SelectedAccountUid
            ?? _statisticsService.CurrentDocument.Accounts.FirstOrDefault()?.Uid;
        if (accountUid is null) return;
        var detectedAt = DateTimeOffset.Now;
        season = EncounterSeasonTimeline.ResolveForRecording(_encounterSeasonConfigService.Load(), detectedAt, season);
        var enemyNameText = await _frameRecognizer.RecognizeRegionTextAsync(
            state,
            frame,
            BattleEnemyNameRegionIds,
            cancellationToken,
            "奇遇解除精灵名");
        var enemyName = await MatchRecognizedSpiritNameAsync(enemyNameText, cancellationToken);
        var spiritNameMatchThreshold = GetSpiritNameMatchThreshold();
        _debugLog.Write(
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
        if (battleId != _battle.BattleId)
        {
            return;
        }

        RefreshOverlayActivity(state, _overlayActivities.RecognizeSpirit(battleId, null, enemyName, DateTimeOffset.Now));
        await RecordEncounterAsync(
            season,
            enemyName,
            enemyNameText,
            detectedAt,
            accountUid,
            battleId,
            cancellationToken);
    }

    private async Task<bool> TryUpdateRuntimeShinyTipSignalAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        EncounterSeasonDefinition season,
        long battleId,
        CancellationToken cancellationToken)
    {
        var accountUid = _statisticsService.IsActiveAccountSelectionRequired ? null
            : _statisticsService.ActiveAccountUid ?? _statisticsService.SelectedAccountUid
                ?? _statisticsService.CurrentDocument.Accounts.FirstOrDefault()?.Uid;
        var detectedAt = DateTimeOffset.Now;
        var tipText = await _frameRecognizer.RecognizeRegionTextAsync(
            state,
            frame,
            BattleShinyTipRegionIds,
            cancellationToken,
            "异色识别");

        var isTipMatch = IsShinyTip(tipText, out var similarity);
        _debugLog.Write(
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

        if (battleId != _battle.BattleId) return false;
        ApplyAutoBattleShinySuspension(tipText, "异色识别", battleId);
        if (EncounterStatisticsEnabled && !_statisticsService.IsActiveAccountSelectionRequired && accountUid is not null)
        {
            RuntimePendingShinyCapture capture;
            lock (_pendingShinyRecordLock)
            {
                if (_pendingShinyCapture?.BattleId != battleId)
                    _pendingShinyCapture = new RuntimePendingShinyCapture(battleId, accountUid, season, detectedAt);
                capture = _pendingShinyCapture!;
            }
            // 名称 OCR 或图鉴匹配失败也不能丢掉已确认的异色提示。
            await capture.SaveAsync(_statisticsService);
        }
        return true;
    }

    private async Task RecordEncounterAsync(
        EncounterSeasonDefinition season,
        string enemyName,
        string rawText,
        DateTimeOffset now,
        string accountUid,
        long battleId,
        CancellationToken cancellationToken)
    {
        if (!EncounterStatisticsEnabled)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(enemyName))
            enemyName = await ResolveEncounterStatisticsRecordNameAsync(enemyName, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (battleId != _battle.BattleId || !EncounterStatisticsEnabled) return;

        if (!TryReserveEncounterRecord(season.Id, enemyName, now, out var recordId))
        {
            return;
        }

        try
        {
            if (season.Id == EncounterSeasonTimeline.PendingSeasonId || string.IsNullOrWhiteSpace(enemyName))
            {
                await _statisticsService.AddPendingEncounterAsync(accountUid, season, recordId,
                    string.IsNullOrWhiteSpace(enemyName) ? rawText : string.Empty, now, enemyName);
                _logger.LogInformation(
                    "奇遇统计：本次奇遇已暂存，等待补齐赛季或精灵名称后自动归档。Uid={Uid}, Season={SeasonId}, Spirit={SpiritName}, EnemyNameRaw={EnemyNameRaw}",
                    accountUid, season.Id, enemyName, FormatLogText(rawText));
                if (CurrentState is { } pendingState && !cancellationToken.IsCancellationRequested && !_isSuspended)
                    RefreshOverlayActivity(pendingState, _overlayActivities.Record(enemyName, null, now, DateTimeOffset.Now));
                return;
            }

            var document = await _statisticsService.RecordEncounterAsync(season, enemyName, now, accountUid);
            var currentCount = document.Accounts.FirstOrDefault(account => account.Uid == accountUid)?.Seasons
                .FirstOrDefault(item => item.Id == season.Id)?.Encounters
                .FirstOrDefault(item => TextMatchingHelper.AreSameSpiritName(item.Name, enemyName))?.Count ?? 0;
            if (currentCount == 0) return;
            if (CurrentState is { } recordedState && !cancellationToken.IsCancellationRequested && !_isSuspended)
                RefreshOverlayActivity(recordedState, _overlayActivities.Record(enemyName, currentCount, now, DateTimeOffset.Now));
            _logger.LogInformation(
                "奇遇统计：{SpiritName} 奇遇 +1（当前 {Count}）",
                enemyName,
                currentCount);
        }
        catch
        {
            // 保存失败时允许后续帧重试；不能把尚未落盘的事件当成已经记录。
            lock (_encounterRecordLock)
            {
                if (_lastRecordedEncounterAt == now && _encounterRecordId == recordId)
                {
                    _hasActiveEncounterRecord = false;
                    _lastRecordedEncounterAt = default;
                }
            }
            throw;
        }
    }

    private async Task TryCompletePendingShinyNameAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        long battleId,
        CancellationToken cancellationToken)
    {
        RuntimePendingShinyCapture? capture;
        lock (_pendingShinyRecordLock) capture = _pendingShinyCapture;
        if (!EncounterStatisticsEnabled || capture is null || capture.BattleId != battleId
            || battleId != _battle.BattleId || capture.HasName) return;

        // 即使异色提示已消失，首次保存失败仍可重试；与自动战斗的名称判定解耦。
        await capture.SaveAsync(_statisticsService);
        var rawText = await _frameRecognizer.RecognizeRegionTextAsync(
            state, frame, BattleEnemyNameRegionIds, cancellationToken, "异色精灵名");
        if (battleId != _battle.BattleId) return;
        await capture.SaveAsync(_statisticsService, rawText);
        var name = await MatchRecognizedSpiritNameAsync(rawText, cancellationToken);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = await ResolveEncounterStatisticsRecordNameAsync(name, cancellationToken);
        if (battleId != _battle.BattleId) return;
        await capture.SaveAsync(_statisticsService, rawText, name);
        _logger.LogInformation("异色识别：{SpiritName} 已暂存，等待统计页面确认。", name);
    }

    private bool TryReserveEncounterRecord(string seasonId, string spiritName, DateTimeOffset now, out string recordId)
    {
        lock (_encounterRecordLock)
        {
            recordId = string.Empty;
            if (string.Equals(_lastRecordedEncounterSeasonId, seasonId, StringComparison.OrdinalIgnoreCase)
                && (_hasActiveEncounterRecord || now - _lastRecordedEncounterAt < EncounterDuplicateSuppressWindow))
            {
                var remaining = _hasActiveEncounterRecord
                    ? EncounterDuplicateSuppressWindow
                    : EncounterDuplicateSuppressWindow - (now - _lastRecordedEncounterAt);
                _debugLog.Write(
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
            _encounterRecordId ??= Guid.NewGuid().ToString("N");
            recordId = _encounterRecordId;
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

    private void ResetEncounterRecordSuppression()
    {
        lock (_encounterRecordLock)
        {
            _hasActiveEncounterRecord = false;
            _encounterRecordId = null;
        }

        lock (_pendingShinyRecordLock)
        {
            _pendingShinyCapture = null;
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
        double DisabledMarkerScore,
        ImageMatchAlgorithm Algorithm);
}
