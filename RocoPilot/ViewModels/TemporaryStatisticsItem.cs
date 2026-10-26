using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Encounters;
using RocoPilot.Services.Statistics;

namespace RocoPilot.ViewModels;

public sealed record TemporaryStatisticsItem(string AccountUid, string Name, string SuggestedSeasonId,
    int Count, bool IsShiny, string Snapshot, IReadOnlyList<string> EncounterIds, IReadOnlyList<string> CaptureIds)
{
    public string Kind => IsShiny ? "异色" : "奇遇";

    internal static IReadOnlyList<TemporaryStatisticsItem> Build(AccountStatisticsData? account, EncounterSeasonConfig config)
    {
        if (account is null) return [];
        string Season(DateOnly date) => EncounterSeasonTimeline.FindSeason(config, date)?.Id ?? string.Empty;
        var encounters = (account.Temporary?.Encounters ?? []).GroupBy(x =>
            (Name: TemporaryStatisticsRules.DisplayName(x), Season: Season(x.Date)))
            .Select(g => new TemporaryStatisticsItem(account.Uid, g.Key.Name, g.Key.Season, g.Sum(x => x.Count), false,
                TemporaryStatisticsRules.Snapshot(g, []), g.Select(x => x.Id).ToList(), []));
        var shinies = account.Seasons.Where(x => x.Id == EncounterSeasonTimeline.PendingSeasonId)
            .SelectMany(x => x.ShinyCaptures).GroupBy(x => (x.Name, Season: Season(DateOnly.FromDateTime(x.CapturedAt.DateTime))))
            .Select(g => new TemporaryStatisticsItem(account.Uid, g.Key.Name, g.Key.Season, g.Count(), true,
                TemporaryStatisticsRules.Snapshot([], g), [], g.Select(x => x.Id).ToList()));
        return encounters.Concat(shinies).OrderBy(x => x.IsShiny).ThenBy(x => x.SuggestedSeasonId).ThenBy(x => x.Name).ToList();
    }
}
