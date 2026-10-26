using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RocoPilot.Helpers;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Statistics;
using RocoPilot.Services.Encounters;

namespace RocoPilot.Services.Statistics;

internal static class TemporaryStatisticsRules
{
    public static string? Record(AccountStatisticsData account, string eventId, string rawText,
        string exactName, DateTimeOffset detectedAt, string? previousBucketId)
    {
        var buckets = account.Temporary?.Encounters ?? [];
        var previous = buckets.FirstOrDefault(x => x.Id == previousBucketId);
        if (previousBucketId is not null)
        {
            // 已入账或被异色确认清零后，迟到 OCR 不能恢复已删除的计数。
            if (previous is null || previous.LastEventId != eventId) return null;
            if (string.IsNullOrWhiteSpace(rawText)) return previous.Id;
            if (--previous.Count == 0) buckets.Remove(previous);
            else previous.LastEventId = string.Empty;
        }
        else
        {
            var saved = buckets.FirstOrDefault(x => x.LastEventId == eventId);
            if (saved is not null) return saved.Id;
        }

        account.Temporary ??= new TemporaryStatistics { Encounters = buckets };
        var date = DateOnly.FromDateTime(detectedAt.DateTime);
        var raw = rawText.Trim();
        var bucket = buckets.FirstOrDefault(x => x.Date == date && x.RawText == raw && x.Name == exactName);
        if (bucket is null)
        {
            bucket = new TemporaryEncounterCount
            {
                Id = Guid.NewGuid().ToString("N"), Date = date, RawText = raw, Name = exactName
            };
            buckets.Add(bucket);
        }
        bucket.Count = checked(bucket.Count + 1);
        bucket.LastEventId = eventId;
        return bucket.Id;
    }

    public static string DisplayName(TemporaryEncounterCount item) =>
        item.Name.Length > 0 ? item.Name : item.RawText.Length > 0 ? item.RawText : "未识别精灵";

    public static int Count(AccountStatisticsData account, string name) =>
        account.Temporary?.Encounters.Where(x => TextMatchingHelper.AreSameSpiritName(DisplayName(x), name)).Sum(x => x.Count) ?? 0;

    public static void ConfirmShiny(AccountStatisticsData account, PendingShinyCaptureRecord pending, string name, int? count)
    {
        var matching = account.Temporary?.Encounters.Where(x =>
            TextMatchingHelper.AreSameSpiritName(DisplayName(x), name)
            || (pending.Name.Length > 0 && TextMatchingHelper.AreSameSpiritName(DisplayName(x), pending.Name))
            || (pending.RawText.Length > 0 && x.RawText == pending.RawText)).ToList() ?? [];
        var season = StatisticsMutationRules.ResolveSeason(account, new EncounterSeasonDefinition
        {
            Id = EncounterSeasonTimeline.PendingSeasonId, Name = EncounterSeasonTimeline.PendingSeasonName
        });
        season.ShinyCaptures.Add(new ShinySpiritCaptureRecord
        {
            Id = pending.Id, Name = name, Season = season.Id, CapturedAt = pending.DetectedAt,
            EncounterCountBeforeCapture = count ?? matching.Sum(x => x.Count)
        });
        foreach (var bucket in matching) account.Temporary!.Encounters.Remove(bucket);
        account.PendingShinyCaptures.Remove(pending);
        Clean(account);
    }

    public static string Snapshot(IEnumerable<TemporaryEncounterCount> buckets, IEnumerable<ShinySpiritCaptureRecord> captures) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Encounters = buckets.OrderBy(x => x.Id).Select(x => new { x.Id, x.Count, x.Name, x.RawText, x.Date }),
            Captures = captures.OrderBy(x => x.Id).Select(x => new { x.Id, x.Name, x.CapturedAt, x.EncounterCountBeforeCapture })
        }))));

    public static void Transfer(AccountStatisticsData account, TemporaryStatisticsTransfer input, EncounterSeasonDefinition season)
    {
        var buckets = account.Temporary?.Encounters.Where(x => input.EncounterIds.Contains(x.Id)).ToList() ?? [];
        var temporarySeason = account.Seasons.FirstOrDefault(x => x.Id == EncounterSeasonTimeline.PendingSeasonId);
        var captures = temporarySeason?.ShinyCaptures.Where(x => input.CaptureIds.Contains(x.Id)).ToList() ?? [];
        if (buckets.Count == 0 && captures.Count == 0) return; // 重试已完成的操作。
        if (buckets.Count != input.EncounterIds.Count || captures.Count != input.CaptureIds.Count
            || Snapshot(buckets, captures) != input.Snapshot)
            throw new InvalidOperationException("暂存数据已变化，请重新打开确认界面，避免遗漏新计数。");
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);
        if (input.Count < 0) throw new ArgumentOutOfRangeException(nameof(input.Count));
        if (buckets.Count > 0 && captures.Count > 0) throw new InvalidOperationException("请分别确认奇遇次数和异色归属。");
        var target = StatisticsMutationRules.ResolveSeason(account, season);
        if (buckets.Count > 0 && input.Count > 0)
            StatisticsMutationRules.UpsertEncounter(account, season.Id, input.Name.Trim(), input.Count,
                new DateTimeOffset(buckets.Max(x => x.Date).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        foreach (var capture in captures)
        {
            capture.Name = input.Name.Trim();
            capture.Season = season.Id;
            target.ShinyCaptures.Add(capture);
            temporarySeason!.ShinyCaptures.Remove(capture);
        }
        foreach (var bucket in buckets) account.Temporary!.Encounters.Remove(bucket);
        Clean(account);
    }

    public static void Clean(AccountStatisticsData account)
    {
        if (account.Temporary?.Encounters.Count == 0) account.Temporary = null;
        account.Seasons.RemoveAll(x => x.Id == EncounterSeasonTimeline.PendingSeasonId
            && x.Encounters.Count == 0 && x.ShinyCaptures.Count == 0);
    }
}
