using RocoPilot.Models.Statistics;
using RocoPilot.Services.Encounters;

namespace RocoPilot.Services.Statistics;

// 仅转换旧存储格式。更新赛季配置绝不自动入账。
internal static class StatisticsLegacyMigration
{
    public static bool Apply(StatisticsDocument document)
    {
        var changed = false;
        foreach (var account in document.Accounts)
        {
            var temporarySeason = account.Seasons.FirstOrDefault(x => x.Id == EncounterSeasonTimeline.PendingSeasonId);
            if (account.PendingEncounters.Count == 0 && !account.PendingShinyCaptures.Any(x => x.HandledAt != null)
                && !account.Seasons.Any(x => x.EncounterCountResets.Count > 0)
                && !(temporarySeason?.Encounters.Count > 0)) continue;
            account.SyncVersion ??= new StatisticsAccountVersion
            {
                LegacyFingerprint = StatisticsDocumentMerger.ComputeAccountFingerprint(account)
            };
            foreach (var old in account.PendingEncounters.Where(x => x.HandledAt is null))
            {
                // 旧版本正常赛季的未识别记录也保留一次，之后不再创建此类暂存。
                TemporaryStatisticsRules.Record(account, old.Id, old.RawText,
                    old.RawText.Length == 0 ? old.Name ?? string.Empty : string.Empty, old.DetectedAt, null);
            }
            foreach (var old in temporarySeason?.Encounters ?? [])
            {
                var id = TemporaryStatisticsRules.Record(account, Guid.NewGuid().ToString("N"), string.Empty,
                    old.Name, old.LastCapturedAt, null);
                account.Temporary!.Encounters.Single(x => x.Id == id).Count += old.Count - 1;
            }
            temporarySeason?.Encounters.Clear();
            account.PendingEncounters.Clear();
            foreach (var season in account.Seasons) season.EncounterCountResets.Clear();
            account.PendingShinyCaptures.RemoveAll(x => x.HandledAt is not null);
            TemporaryStatisticsRules.Clean(account);
            changed = true;
        }
        return changed;
    }
}
