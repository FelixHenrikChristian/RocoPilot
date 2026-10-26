using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Encounters;

namespace RocoPilot.Services.Statistics;

// 仅转换旧存储格式。更新赛季配置绝不自动入账。
internal static class StatisticsLegacyMigration
{
    public static void Apply(StatisticsDocument document)
    {
        foreach (var account in document.Accounts)
        {
            var temporarySeason = account.Seasons.FirstOrDefault(x => x.Id == EncounterSeasonTimeline.PendingSeasonId);
            if (account.PendingEncounters.Count == 0 && !account.PendingShinyCaptures.Any(x => x.HandledAt != null)
                && !account.Seasons.Any(x => x.EncounterCountResets.Count > 0)
                && !(temporarySeason?.Encounters.Count > 0)) continue;
            var existingBucketIds = account.Temporary?.Encounters.Select(x => x.Id).ToHashSet() ?? [];
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
            // 同一旧文档在不同设备迁移时得到相同快照，不制造同步冲突。
            foreach (var bucket in account.Temporary?.Encounters.Where(x => !existingBucketIds.Contains(x.Id)) ?? [])
            {
                bucket.Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { bucket.Date, bucket.RawText, bucket.Name })))).ToLowerInvariant();
                bucket.LastEventId = string.Empty;
            }
            temporarySeason?.Encounters.Clear();
            account.PendingEncounters.Clear();
            foreach (var season in account.Seasons) season.EncounterCountResets.Clear();
            account.PendingShinyCaptures.RemoveAll(x => x.HandledAt is not null);
            TemporaryStatisticsRules.Clean(account);
        }
    }
}
