using System.Text.Json;
using System.Text.Json.Serialization;

using RocoPilot.Helpers;
using RocoPilot.Models.Statistics;

namespace RocoPilot.Services.Statistics;

internal static class StatisticsDocumentMerger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static StatisticsDocument Merge(StatisticsDocument localDocument, StatisticsDocument remoteDocument)
    {
        var merged = CloneAndNormalize(localDocument);
        var remote = CloneAndNormalize(remoteDocument);

        foreach (var remoteAccount in remote.Accounts)
        {
            var localAccount = merged.Accounts.FirstOrDefault(account =>
                string.Equals(account.Uid, remoteAccount.Uid, StringComparison.OrdinalIgnoreCase));
            if (localAccount is null)
            {
                merged.Accounts.Add(remoteAccount);
                continue;
            }

            MergeAccount(localAccount, remoteAccount);
        }

        return StatisticsDocumentNormalizer.Normalize(merged);
    }

    private static void MergeAccount(AccountStatisticsData localAccount, AccountStatisticsData remoteAccount)
    {
        foreach (var remoteSeason in remoteAccount.Seasons)
        {
            var localSeason = localAccount.Seasons.FirstOrDefault(season =>
                string.Equals(season.Id, remoteSeason.Id, StringComparison.OrdinalIgnoreCase));
            if (localSeason is null)
            {
                localAccount.Seasons.Add(remoteSeason);
                continue;
            }

            MergeSeason(localSeason, remoteSeason);
        }

        MergePendingShinyCaptures(localAccount, remoteAccount);
    }

    private static void MergeSeason(SeasonStatisticsData localSeason, SeasonStatisticsData remoteSeason)
    {
        localSeason.Name = PreferNonEmpty(localSeason.Name, remoteSeason.Name);
        localSeason.DateRange = PreferNonEmpty(localSeason.DateRange, remoteSeason.DateRange);
        localSeason.EncounterTypeName = PreferNonEmpty(localSeason.EncounterTypeName, remoteSeason.EncounterTypeName);

        MergeEncounters(localSeason, remoteSeason);
        MergeShinyCaptures(localSeason, remoteSeason);
    }

    private static void MergeEncounters(SeasonStatisticsData localSeason, SeasonStatisticsData remoteSeason)
    {
        foreach (var remoteEncounter in remoteSeason.Encounters)
        {
            var localEncounter = localSeason.Encounters.FirstOrDefault(encounter =>
                TextMatchingHelper.AreSameSpiritName(encounter.Name, remoteEncounter.Name));
            if (localEncounter is null)
            {
                localSeason.Encounters.Add(remoteEncounter);
                continue;
            }

            var latest = localEncounter.LastCapturedAt >= remoteEncounter.LastCapturedAt
                ? localEncounter
                : remoteEncounter;
            localEncounter.Name = latest.Name;
            localEncounter.Count = Math.Max(localEncounter.Count, remoteEncounter.Count);
            localEncounter.Season = string.IsNullOrWhiteSpace(latest.Season)
                ? localSeason.Id
                : latest.Season;
            localEncounter.LastCapturedAt = Max(localEncounter.LastCapturedAt, remoteEncounter.LastCapturedAt);
        }
    }

    private static void MergeShinyCaptures(SeasonStatisticsData localSeason, SeasonStatisticsData remoteSeason)
    {
        foreach (var remoteCapture in remoteSeason.ShinyCaptures)
        {
            var localCapture = localSeason.ShinyCaptures.FirstOrDefault(capture =>
                string.Equals(capture.Id, remoteCapture.Id, StringComparison.OrdinalIgnoreCase));
            if (localCapture is null)
            {
                localSeason.ShinyCaptures.Add(remoteCapture);
                continue;
            }

            if (remoteCapture.CapturedAt > localCapture.CapturedAt)
            {
                localCapture.Name = remoteCapture.Name;
                localCapture.Season = remoteCapture.Season;
                localCapture.CapturedAt = remoteCapture.CapturedAt;
                localCapture.EncounterCountBeforeCapture = remoteCapture.EncounterCountBeforeCapture;
            }
        }
    }

    private static void MergePendingShinyCaptures(
        AccountStatisticsData localAccount,
        AccountStatisticsData remoteAccount)
    {
        foreach (var remoteCapture in remoteAccount.PendingShinyCaptures)
        {
            var localCapture = localAccount.PendingShinyCaptures.FirstOrDefault(capture =>
                string.Equals(capture.Id, remoteCapture.Id, StringComparison.OrdinalIgnoreCase));
            if (localCapture is null)
            {
                localAccount.PendingShinyCaptures.Add(remoteCapture);
                continue;
            }

            if (remoteCapture.DetectedAt > localCapture.DetectedAt)
            {
                localCapture.Name = remoteCapture.Name;
                localCapture.Season = remoteCapture.Season;
                localCapture.DetectedAt = remoteCapture.DetectedAt;
            }
        }
    }

    private static string PreferNonEmpty(string localValue, string remoteValue)
    {
        return string.IsNullOrWhiteSpace(localValue)
            ? remoteValue
            : localValue;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
    {
        return left >= right ? left : right;
    }

    private static StatisticsDocument CloneAndNormalize(StatisticsDocument document)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var clone = JsonSerializer.Deserialize<StatisticsDocument>(json, JsonOptions) ?? new StatisticsDocument();
        return StatisticsDocumentNormalizer.Normalize(clone);
    }
}
