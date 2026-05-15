using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;
using RocoPilot.Models.Spirits;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private static readonly TimeSpan EncounterDuplicateSuppressWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PendingShinyDuplicateSuppressWindow = TimeSpan.FromSeconds(12);

    private const string HeterochromiaTipText = "发现异色精灵";
    private const double HeterochromiaTipMatchThreshold = 0.78;
    private const int HeterochromiaTipMinimumTextLength = 4;

    private static readonly string[] BattleTipRelieveRegionIds =
    [
        "battle-tip-relieve"
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
    private volatile bool _encounterStatisticsEnabled = true;
    private volatile SpiritEvolutionRecordMode _encounterStatisticsEvolutionRecordMode = SpiritEvolutionRecordMode.Lowest;
    private bool _hasActiveEncounterRecord;
    private string? _lastRecordedEncounterSeasonId;
    private string? _lastRecordedEncounterName;
    private DateTimeOffset _lastRecordedEncounterAt;
    private bool _hasActivePendingShinyRecord;
    private string? _lastPendingShinySeasonId;
    private string? _lastPendingShinyName;
    private DateTimeOffset _lastPendingShinyAt;

    public bool EncounterStatisticsEnabled => _encounterStatisticsEnabled;

    public SpiritEvolutionRecordMode EncounterStatisticsEvolutionRecordMode => _encounterStatisticsEvolutionRecordMode;

    public void SetEncounterStatisticsEnabled(bool isEnabled)
    {
        _encounterStatisticsEnabled = isEnabled;
        _settingsLoaded = true;
        _ = SaveEncounterStatisticsEnabledAsync(isEnabled);
    }

    public void SetEncounterStatisticsEvolutionRecordMode(SpiritEvolutionRecordMode mode)
    {
        _encounterStatisticsEvolutionRecordMode = NormalizeEncounterStatisticsEvolutionRecordMode(mode);
        _settingsLoaded = true;
        _ = SaveEncounterStatisticsEvolutionRecordModeAsync(_encounterStatisticsEvolutionRecordMode);
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

    private async Task SaveEncounterStatisticsEvolutionRecordModeAsync(SpiritEvolutionRecordMode mode)
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(SettingsKeys.EncounterStatisticsEvolutionRecordMode, mode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存奇遇统计形态归一设置失败。");
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

        if (string.IsNullOrWhiteSpace(season.TipText))
        {
            return;
        }

        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleTipRelieveRegionIds,
            cancellationToken,
            "奇遇统计");

        var isTipMatch = TextMatchingHelper.IsSimilar(
                tipText,
                season.TipText,
                season.MatchThreshold,
                out var similarity);
        _logger.LogDebug(
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
        var enemyName = await MatchRecognizedSpiritNameAsync(enemyNameText, cancellationToken);
        _logger.LogDebug(
            "奇遇统计筛选：EnemyNameRaw={EnemyNameRaw}, Matched={SpiritName}, IsValid={IsValid}",
            FormatLogText(enemyNameText),
            enemyName,
            !string.IsNullOrWhiteSpace(enemyName));
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            _logger.LogDebug("已匹配奇遇提示，但 battle-enemy-name 区域未识别到精灵名。相似度：{Similarity:P1}", similarity);
            return;
        }

        enemyName = await ResolveEncounterStatisticsRecordNameAsync(enemyName, cancellationToken);
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            return;
        }

        if (!TryReserveEncounterRecord(season.Id, enemyName, DateTimeOffset.Now))
        {
            return;
        }

        var previousCount = GetEncounterCount(season.Id, enemyName);
        await _statisticsService.RecordEncounterAsync(season, enemyName, DateTimeOffset.Now);
        var currentCount = GetEncounterCount(season.Id, enemyName);
        if (currentCount > previousCount)
        {
            _logger.LogInformation(
                "奇遇统计：{SpiritName} 奇遇 +1（当前 {Count}）",
                enemyName,
                currentCount);
        }

        _logger.LogDebug(
            "奇遇统计已记录：Season={SeasonId}, Type={EncounterType}, Spirit={SpiritName}, PreviousCount={PreviousCount}, CurrentCount={CurrentCount}, TipSimilarity={Similarity:P1}",
            season.Id,
            season.EncounterTypeName,
            enemyName,
            previousCount,
            currentCount,
            similarity);
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
        _logger.LogDebug(
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
        var enemyName = await MatchRecognizedSpiritNameAsync(enemyNameText, cancellationToken);
        _logger.LogDebug(
            "异色识别筛选：EnemyNameRaw={EnemyNameRaw}, Matched={SpiritName}, IsValid={IsValid}",
            FormatLogText(enemyNameText),
            enemyName,
            !string.IsNullOrWhiteSpace(enemyName));
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            _logger.LogDebug("已匹配异色提示，但 battle-enemy-name 区域未识别到精灵名。相似度：{Similarity:P1}", similarity);
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
                _logger.LogDebug(
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
                _logger.LogDebug(
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
    }

    private int GetEncounterCount(string seasonId, string spiritName)
    {
        var record = _statisticsService
            .GetSelectedAccountSeasonEncounters(seasonId)
            .FirstOrDefault(record => TextMatchingHelper.AreSameSpiritName(record.Name, spiritName));
        return record?.Count ?? 0;
    }

    private async Task<string> MatchRecognizedSpiritNameAsync(
        string recognizedText,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _spiritCatalogService.MatchSpiritNameAsync(recognizedText, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "精灵名图鉴匹配失败，已使用 OCR 原始结果。");
            return TextMatchingHelper.NormalizeSpiritNameForMatching(recognizedText);
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

        var recordMode = _encounterStatisticsEvolutionRecordMode;
        try
        {
            var resolvedName = await _spiritCatalogService.ResolveEvolutionRecordNameAsync(
                normalizedName,
                recordMode,
                cancellationToken);
            return string.IsNullOrWhiteSpace(resolvedName)
                ? normalizedName
                : resolvedName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "精灵进化链统计名解析失败，已使用匹配精灵名。Spirit={SpiritName}, Mode={RecordMode}",
                normalizedName,
                recordMode);
            return normalizedName;
        }
    }

    private static SpiritEvolutionRecordMode NormalizeEncounterStatisticsEvolutionRecordMode(
        SpiritEvolutionRecordMode? mode)
    {
        return mode switch
        {
            SpiritEvolutionRecordMode.Highest => SpiritEvolutionRecordMode.Highest,
            _ => SpiritEvolutionRecordMode.Lowest
        };
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
