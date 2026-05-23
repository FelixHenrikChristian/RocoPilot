using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private static readonly TimeSpan EncounterDuplicateSuppressWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PendingShinyDuplicateSuppressWindow = TimeSpan.FromSeconds(12);

    private const string DefaultEncounterPlaceholderName = "幸运惊喜盒";
    private const string HeterochromiaTipText = "发现异色精灵";
    private const double HeterochromiaTipMatchThreshold = 0.78;
    private const int HeterochromiaTipMinimumTextLength = 4;

    private static readonly string[] BattleTipRegionIds =
    [
        "battle-tip"
    ];
    private static readonly string[] BattleTipHeterochromiaRegionIds =
    [
        "battle-tip-heterochromia"
    ];
    private static readonly string[] BattleEnemyNameRegionIds =
    [
        "battle-enemy-name"
    ];

    private readonly object _encounterRecordLock = new();
    private readonly object _pendingShinyRecordLock = new();
    private readonly object _encounterNameTransitionLock = new();
    private volatile bool _encounterStatisticsEnabled = true;
    private bool _hasActiveEncounterRecord;
    private string? _lastRecordedEncounterSeasonId;
    private string? _lastRecordedEncounterName;
    private DateTimeOffset _lastRecordedEncounterAt;
    private bool _hasActivePendingShinyRecord;
    private string? _lastPendingShinySeasonId;
    private string? _lastPendingShinyName;
    private DateTimeOffset _lastPendingShinyAt;
    private bool _hasSeenEncounterPlaceholderName;
    private string? _encounterNameTransitionSeasonId;

    public bool EncounterStatisticsEnabled => _encounterStatisticsEnabled;

    public void SetEncounterStatisticsEnabled(bool isEnabled)
    {
        _encounterStatisticsEnabled = isEnabled;
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

        return _statisticsService.GetSelectedAccountSeasonEncounters(season.Id)
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

    private async Task TryUpdateEncounterStatisticsAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var season = _encounterSeasonConfigService.GetCurrentSeason();
        if (season is null)
        {
            return;
        }

        await TryUpdatePendingShinyCaptureAsync(state, frame, season, cancellationToken);

        if (UsesEnemyNameTransitionDetection(season)
            && await TryUpdateEncounterStatisticsByEnemyNameTransitionAsync(state, frame, season, cancellationToken))
        {
            return;
        }

        await TryUpdateEncounterStatisticsByTipTextAsync(state, frame, season, cancellationToken);
    }

    private async Task TryUpdateEncounterStatisticsByTipTextAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        EncounterSeasonDefinition season,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(season.TipText))
        {
            return;
        }

        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleTipRegionIds,
            cancellationToken,
            "奇遇统计");

        var isTipMatch = TextMatchingHelper.IsSimilar(
                tipText,
                season.TipText,
                season.MatchThreshold,
                out var similarity);
        LogDebugOncePerValue(
            CreateDebugLogKey("encounter-tip-filter", season.Id),
            CreateMatchFilterDebugFingerprint(tipText, similarity, isTipMatch),
            "奇遇统计筛选：TipText={TipText}, Expected={ExpectedTipText}, Similarity={Similarity:P1}, Threshold={Threshold:P1}, IsMatch={IsMatch}",
            FormatLogText(tipText),
            FormatLogText(season.TipText),
            similarity,
            season.MatchThreshold,
            isTipMatch);
        if (!isTipMatch)
        {
            return;
        }

        ApplyAutoBattleEncounterRelievedDetection("奇遇统计");

        var enemyNameText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleEnemyNameRegionIds,
            cancellationToken,
            "奇遇统计");
        var enemyName = await MatchRecognizedSpiritNameAsync(enemyNameText, season, cancellationToken);
        LogDebugOncePerValue(
            CreateDebugLogKey("encounter-tip-enemy-filter", season.Id),
            string.Join(
                "|",
                CreateTextDebugFingerprint(enemyNameText),
                CreateTextDebugFingerprint(enemyName),
                CreateBooleanDebugFingerprint(!string.IsNullOrWhiteSpace(enemyName))),
            "奇遇统计筛选：EnemyNameRaw={EnemyNameRaw}, Matched={SpiritName}, IsValid={IsValid}, MatchThreshold={MatchThreshold:P1}",
            FormatLogText(enemyNameText),
            enemyName,
            !string.IsNullOrWhiteSpace(enemyName),
            season.SpiritNameMatchThreshold);
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            LogDebugOncePerValue(
                CreateDebugLogKey("encounter-tip-missing-enemy", season.Id),
                CreateSimilarityDebugFingerprint(similarity),
                "已匹配奇遇提示，但 battle-enemy-name 区域未识别到精灵名。相似度：{Similarity:P1}",
                similarity);
            return;
        }

        await RecordEncounterAsync(season, enemyName, DateTimeOffset.Now, "TipText", similarity, cancellationToken);
    }

    private async Task<bool> TryUpdateEncounterStatisticsByEnemyNameTransitionAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        EncounterSeasonDefinition season,
        CancellationToken cancellationToken)
    {
        var enemyName = await TryDetectEncounterNameTransitionAsync(
            state,
            frame,
            season,
            cancellationToken,
            "奇遇统计");
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            return false;
        }

        ApplyAutoBattleEncounterRelievedDetection("奇遇统计");
        await RecordEncounterAsync(season, enemyName, DateTimeOffset.Now, season.DetectionMode, 1, cancellationToken);
        return true;
    }

    private async Task RecordEncounterAsync(
        EncounterSeasonDefinition season,
        string enemyName,
        DateTimeOffset now,
        string detectionMode,
        double detectionSimilarity,
        CancellationToken cancellationToken)
    {
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
            "奇遇统计已记录：Season={SeasonId}, Type={EncounterType}, Spirit={SpiritName}, PreviousCount={PreviousCount}, CurrentCount={CurrentCount}, DetectionMode={DetectionMode}, DetectionSimilarity={Similarity:P1}",
            season.Id,
            season.EncounterTypeName,
            enemyName,
            previousCount,
            currentCount,
            detectionMode,
            detectionSimilarity);
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
            BattleTipHeterochromiaRegionIds,
            cancellationToken,
            "异色识别");

        var isTipMatch = IsHeterochromiaTip(tipText, out var similarity);
        LogDebugOncePerValue(
            CreateDebugLogKey("shiny-tip-filter", season.Id),
            CreateMatchFilterDebugFingerprint(tipText, similarity, isTipMatch),
            "异色识别筛选：TipText={TipText}, Expected={ExpectedTipText}, Similarity={Similarity:P1}, Threshold={Threshold:P1}, IsMatch={IsMatch}",
            FormatLogText(tipText),
            HeterochromiaTipText,
            similarity,
            HeterochromiaTipMatchThreshold,
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
        var enemyName = await MatchRecognizedSpiritNameAsync(enemyNameText, season, cancellationToken);
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
            season.SpiritNameMatchThreshold);
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

        ResetEncounterNameTransitionState();
    }

    private int GetEncounterCount(string seasonId, string spiritName)
    {
        var record = _statisticsService
            .GetSelectedAccountSeasonEncounters(seasonId)
            .FirstOrDefault(record => TextMatchingHelper.AreSameSpiritName(record.Name, spiritName));
        return record?.Count ?? 0;
    }

    private async Task<string> TryDetectEncounterNameTransitionAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        EncounterSeasonDefinition season,
        CancellationToken cancellationToken,
        string taskName)
    {
        var enemyNameText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleEnemyNameRegionIds,
            cancellationToken,
            taskName);

        var isPlaceholderName = IsEncounterPlaceholderName(enemyNameText, season, out var placeholderSimilarity);
        var hasSeenPlaceholderName = HasSeenEncounterPlaceholderName(season.Id);
        LogDebugOncePerValue(
            CreateDebugLogKey("encounter-name-transition-placeholder", taskName, season.Id),
            string.Join(
                "|",
                CreateTextDebugFingerprint(enemyNameText),
                CreateSimilarityDebugFingerprint(placeholderSimilarity),
                CreateBooleanDebugFingerprint(hasSeenPlaceholderName),
                CreateBooleanDebugFingerprint(isPlaceholderName)),
            "{TaskName}名称变化筛选：EnemyNameRaw={EnemyNameRaw}, Placeholder={PlaceholderName}, PlaceholderSimilarity={Similarity:P1}, Threshold={Threshold:P1}, HasSeenPlaceholder={HasSeenPlaceholder}, IsPlaceholder={IsPlaceholder}",
            taskName,
            FormatLogText(enemyNameText),
            GetEncounterPlaceholderName(season),
            placeholderSimilarity,
            season.PlaceholderMatchThreshold,
            hasSeenPlaceholderName,
            isPlaceholderName);

        if (isPlaceholderName)
        {
            if (MarkEncounterPlaceholderNameSeen(season.Id))
            {
                _logger.LogInformation(
                    "{TaskName}：已识别到 {PlaceholderName}，等待名称恢复后记录奇遇。",
                    taskName,
                    GetEncounterPlaceholderName(season));
            }

            return string.Empty;
        }

        if (!hasSeenPlaceholderName)
        {
            return string.Empty;
        }

        var enemyName = await MatchRecognizedSpiritNameAsync(enemyNameText, season, cancellationToken);
        LogDebugOncePerValue(
            CreateDebugLogKey("encounter-name-transition-spirit", taskName, season.Id),
            string.Join(
                "|",
                CreateTextDebugFingerprint(enemyNameText),
                CreateTextDebugFingerprint(enemyName),
                CreateBooleanDebugFingerprint(!string.IsNullOrWhiteSpace(enemyName))),
            "{TaskName}名称变化筛选：EnemyNameRaw={EnemyNameRaw}, Matched={SpiritName}, IsValid={IsValid}, MatchThreshold={MatchThreshold:P1}",
            taskName,
            FormatLogText(enemyNameText),
            enemyName,
            !string.IsNullOrWhiteSpace(enemyName),
            season.SpiritNameMatchThreshold);
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            return string.Empty;
        }

        return IsEncounterPlaceholderName(enemyName, season, out _)
            ? string.Empty
            : enemyName;
    }

    private bool HasSeenEncounterPlaceholderName(string seasonId)
    {
        lock (_encounterNameTransitionLock)
        {
            return _hasSeenEncounterPlaceholderName
                && string.Equals(_encounterNameTransitionSeasonId, seasonId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private bool MarkEncounterPlaceholderNameSeen(string seasonId)
    {
        lock (_encounterNameTransitionLock)
        {
            if (_hasSeenEncounterPlaceholderName
                && string.Equals(_encounterNameTransitionSeasonId, seasonId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _hasSeenEncounterPlaceholderName = true;
            _encounterNameTransitionSeasonId = seasonId;
            return true;
        }
    }

    private void ResetEncounterNameTransitionState()
    {
        lock (_encounterNameTransitionLock)
        {
            _hasSeenEncounterPlaceholderName = false;
            _encounterNameTransitionSeasonId = null;
        }
    }

    private async Task<string> MatchRecognizedSpiritNameAsync(
        string recognizedText,
        EncounterSeasonDefinition season,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _spiritCatalogService.MatchSpiritNameAsync(
                recognizedText,
                season.SpiritNameMatchThreshold,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "精灵名图鉴匹配失败，本次 OCR 精灵名已跳过。");
            return string.Empty;
        }
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

    private static bool UsesEnemyNameTransitionDetection(EncounterSeasonDefinition season)
    {
        return string.Equals(
            season.DetectionMode,
            EncounterDetectionModes.EnemyNameTransition,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEncounterPlaceholderName(
        string text,
        EncounterSeasonDefinition season,
        out double similarity)
    {
        var placeholderName = GetEncounterPlaceholderName(season);
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(placeholderName))
        {
            similarity = 0;
            return false;
        }

        return TextMatchingHelper.IsSimilar(
            text,
            placeholderName,
            season.PlaceholderMatchThreshold,
            out similarity);
    }

    private static string GetEncounterPlaceholderName(EncounterSeasonDefinition season)
    {
        return string.IsNullOrWhiteSpace(season.PlaceholderName)
            ? DefaultEncounterPlaceholderName
            : season.PlaceholderName.Trim();
    }

    private static bool IsHeterochromiaTip(string tipText, out double similarity)
    {
        var normalized = TextMatchingHelper.CleanRecognizedText(tipText);
        if (normalized.Length < HeterochromiaTipMinimumTextLength)
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
            TextMatchingHelper.CalculateSimilarity(normalized, HeterochromiaTipText),
            TextMatchingHelper.CalculateSimilarity(normalized, "异色精灵"));
        return similarity >= HeterochromiaTipMatchThreshold;
    }
}
