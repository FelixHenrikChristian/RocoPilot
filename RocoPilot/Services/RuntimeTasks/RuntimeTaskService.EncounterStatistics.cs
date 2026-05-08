using Microsoft.Extensions.Logging;

using RocoPilot.Configuration;
using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Overlay;
using RocoPilot.Models.Runtime;

namespace RocoPilot.Services;

public sealed partial class RuntimeTaskService
{
    private static readonly TimeSpan EncounterDuplicateSuppressWindow = TimeSpan.FromSeconds(6);

    private static readonly string[] BattleTipRelieveRegionIds =
    [
        "battle-tip-relieve"
    ];
    private static readonly string[] BattleEnemyNameRegionIds =
    [
        "battle-enemy-name"
    ];

    private readonly object _encounterRecordLock = new();
    private volatile bool _encounterStatisticsEnabled = true;
    private bool _hasActiveEncounterRecord;
    private string? _lastRecordedEncounterSeasonId;
    private string? _lastRecordedEncounterName;
    private DateTimeOffset _lastRecordedEncounterAt;

    public bool EncounterStatisticsEnabled => _encounterStatisticsEnabled;

    public void SetEncounterStatisticsEnabled(bool isEnabled)
    {
        _encounterStatisticsEnabled = isEnabled;
        _settingsLoaded = true;
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

    private async Task TryUpdateEncounterStatisticsAsync(
        RuntimeTaskState state,
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var season = _encounterSeasonConfigService.GetCurrentSeason();
        if (season is null || string.IsNullOrWhiteSpace(season.TipText))
        {
            return;
        }

        var tipText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleTipRelieveRegionIds,
            cancellationToken);

        if (!TextMatchingHelper.IsSimilar(
                tipText,
                season.TipText,
                season.MatchThreshold,
                out var similarity))
        {
            return;
        }

        var enemyNameText = await RecognizeRegionTextAsync(
            state,
            frame,
            BattleEnemyNameRegionIds,
            cancellationToken);
        var enemyName = TextMatchingHelper.CleanSpiritName(enemyNameText);
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            _logger.LogDebug("已匹配奇遇提示，但 battle-enemy-name 区域未识别到精灵名。相似度：{Similarity:P1}", similarity);
            return;
        }

        if (!TryReserveEncounterRecord(season.Id, enemyName, DateTimeOffset.Now))
        {
            return;
        }

        await _statisticsService.RecordEncounterAsync(season, enemyName, DateTimeOffset.Now);
        _logger.LogInformation(
            "检测到赛季奇遇：Season={SeasonId}, Type={EncounterType}, Spirit={SpiritName}, TipSimilarity={Similarity:P1}",
            season.Id,
            season.EncounterTypeName,
            enemyName,
            similarity);
    }

    private bool TryReserveEncounterRecord(string seasonId, string spiritName, DateTimeOffset now)
    {
        lock (_encounterRecordLock)
        {
            if (string.Equals(_lastRecordedEncounterSeasonId, seasonId, StringComparison.OrdinalIgnoreCase)
                && (_hasActiveEncounterRecord || now - _lastRecordedEncounterAt < EncounterDuplicateSuppressWindow))
            {
                _logger.LogDebug(
                    "奇遇统计冷却中，本次识别已忽略。LastSpirit={LastSpiritName}, CurrentSpirit={CurrentSpiritName}",
                    _lastRecordedEncounterName,
                    spiritName);
                return false;
            }

            _lastRecordedEncounterSeasonId = seasonId;
            _lastRecordedEncounterName = spiritName;
            _lastRecordedEncounterAt = now;
            _hasActiveEncounterRecord = true;
            return true;
        }
    }

    private void ResetEncounterRecordSuppression()
    {
        lock (_encounterRecordLock)
        {
            _hasActiveEncounterRecord = false;
        }
    }
}
