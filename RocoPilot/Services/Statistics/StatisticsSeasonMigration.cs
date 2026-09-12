using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Encounters;

namespace RocoPilot.Services.Statistics;

internal static class StatisticsSeasonMigration
{
    public static bool Apply(StatisticsDocument document, EncounterSeasonConfig config)
    {
        var changed = false;
        foreach (var account in document.Accounts)
        {
            foreach (var pending in account.PendingEncounters.Where(item => item.HandledAt is null))
            {
                var date = DateOnly.FromDateTime(pending.DetectedAt.DateTime);
                var season = EncounterSeasonTimeline.FindSeason(config, date);
                var seasonId = season?.Id ?? (EncounterSeasonTimeline.IsExpired(config, date)
                    ? EncounterSeasonTimeline.PendingSeasonId : pending.Season);
                if (pending.Season != seasonId)
                {
                    pending.Season = seasonId;
                    changed = true;
                }
                if (season is not null) StatisticsMutationRules.ResolveSeason(account, season);
                if (!string.IsNullOrWhiteSpace(pending.Name)
                    && StatisticsMutationRules.ConfirmPendingEncounter(account, pending.Id, pending.Name)
                        == PendingEncounterConfirmationResult.Counted)
                    changed = true;
            }

            // 异色同样保留检测日期，赛季未知时先保留待确认，避免清空旧赛季计数。
            foreach (var pending in account.PendingShinyCaptures)
            {
                var season = EncounterSeasonTimeline.ResolveForRecording(config, pending.DetectedAt,
                    new EncounterSeasonDefinition { Id = pending.Season });
                if (season.Id == pending.Season) continue;
                StatisticsMutationRules.ResolveSeason(account, season);
                pending.Season = season.Id;
                changed = true;
            }

            if (!account.PendingShinyCaptures.Any(item => item.Season == EncounterSeasonTimeline.PendingSeasonId))
                changed |= account.Seasons.RemoveAll(season => season.Id == EncounterSeasonTimeline.PendingSeasonId
                    && season.Encounters.Count == 0 && season.ShinyCaptures.Count == 0 && season.EncounterCountResets.Count == 0) > 0;
        }
        return changed;
    }
}
