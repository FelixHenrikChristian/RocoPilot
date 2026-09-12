using System.Globalization;
using RocoPilot.Models.Encounters;

namespace RocoPilot.Services.Encounters;

internal static class EncounterSeasonTimeline
{
    public const string PendingSeasonId = "__pending_season__";
    public const string PendingSeasonName = "赛季待更新";

    public static bool TryGetDates(string? dateRange, out DateOnly start, out DateOnly end)
    {
        start = end = default;
        var dates = dateRange?.Split('-', StringSplitOptions.TrimEntries);
        return dates is { Length: 2 }
            && DateOnly.TryParseExact(dates[0], "yyyy/M/d", CultureInfo.InvariantCulture, DateTimeStyles.None, out start)
            && DateOnly.TryParseExact(dates[1], "yyyy/M/d", CultureInfo.InvariantCulture, DateTimeStyles.None, out end)
            && start <= end;
    }

    public static EncounterSeasonDefinition? FindSeason(EncounterSeasonConfig config, DateOnly date)
    {
        var matches = config.Seasons.Where(season => !string.IsNullOrWhiteSpace(season.Id)
            && TryGetDates(season.DateRange, out var start, out var end) && start <= date && date <= end).Take(2).ToList();
        // 有重叠的错误配置也不能把记录随意分给其中一个赛季。
        return matches.Count == 1 ? matches[0] : null;
    }

    public static bool IsExpired(EncounterSeasonConfig config, DateOnly date)
    {
        var latestEnd = config.Seasons
            .Where(season => !string.IsNullOrWhiteSpace(season.Id))
            .Select(season => TryGetDates(season.DateRange, out _, out var end) ? (DateOnly?)end : null)
            .Max();
        return latestEnd is { } endDate && date > endDate;
    }

    public static EncounterSeasonDefinition ResolveForRecording(
        EncounterSeasonConfig config, DateTimeOffset occurredAt, EncounterSeasonDefinition fallback)
    {
        var date = DateOnly.FromDateTime(occurredAt.DateTime);
        return FindSeason(config, date) ?? (IsExpired(config, date)
            ? new EncounterSeasonDefinition { Id = PendingSeasonId, Name = PendingSeasonName }
            : fallback);
    }
}
