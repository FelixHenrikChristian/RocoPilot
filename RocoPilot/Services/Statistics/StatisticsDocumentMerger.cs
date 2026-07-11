using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using RocoPilot.Helpers;
using RocoPilot.Models.Statistics;

namespace RocoPilot.Services.Statistics;

internal sealed record StatisticsDocumentMergeResult(
    StatisticsDocument Document,
    IReadOnlyList<string> ConflictingAccountUids);

internal static class StatisticsDocumentMerger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static StatisticsDocumentMergeResult Merge(
        StatisticsDocument localDocument,
        StatisticsDocument remoteDocument,
        IReadOnlyDictionary<string, string>? lastSyncedAccountFingerprints,
        bool preferRemoteAccountsWithoutBaseline)
    {
        var local = CloneAndNormalize(localDocument);
        var remote = CloneAndNormalize(remoteDocument);

        if (lastSyncedAccountFingerprints is null)
        {
            var mergedWithoutBaseline = preferRemoteAccountsWithoutBaseline
                ? MergeWithRemoteAccountPriority(local, remote)
                : MergeLegacy(local, remote);
            return new StatisticsDocumentMergeResult(mergedWithoutBaseline, []);
        }

        return MergeWithBaseline(local, remote, lastSyncedAccountFingerprints);
    }

    public static Dictionary<string, string> ComputeAccountFingerprints(StatisticsDocument document)
    {
        var normalized = CloneAndNormalize(document);
        return normalized.Accounts.ToDictionary(
            account => account.Uid,
            ComputeAccountFingerprint,
            StringComparer.OrdinalIgnoreCase);
    }

    private static StatisticsDocumentMergeResult MergeWithBaseline(
        StatisticsDocument local,
        StatisticsDocument remote,
        IReadOnlyDictionary<string, string> lastSyncedAccountFingerprints)
    {
        var baseline = NormalizeFingerprints(lastSyncedAccountFingerprints);
        var localAccounts = local.Accounts.ToDictionary(account => account.Uid, StringComparer.OrdinalIgnoreCase);
        var remoteAccounts = remote.Accounts.ToDictionary(account => account.Uid, StringComparer.OrdinalIgnoreCase);
        var localFingerprints = localAccounts.ToDictionary(
            pair => pair.Key,
            pair => ComputeAccountFingerprint(pair.Value),
            StringComparer.OrdinalIgnoreCase);
        var remoteFingerprints = remoteAccounts.ToDictionary(
            pair => pair.Key,
            pair => ComputeAccountFingerprint(pair.Value),
            StringComparer.OrdinalIgnoreCase);
        var accountUids = new HashSet<string>(baseline.Keys, StringComparer.OrdinalIgnoreCase);
        accountUids.UnionWith(localAccounts.Keys);
        accountUids.UnionWith(remoteAccounts.Keys);

        var mergedAccounts = new List<AccountStatisticsData>();
        var conflictingAccountUids = new List<string>();
        foreach (var uid in accountUids.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            localAccounts.TryGetValue(uid, out var localAccount);
            remoteAccounts.TryGetValue(uid, out var remoteAccount);
            baseline.TryGetValue(uid, out var baselineFingerprint);
            localFingerprints.TryGetValue(uid, out var localFingerprint);
            remoteFingerprints.TryGetValue(uid, out var remoteFingerprint);

            AccountStatisticsData? selectedAccount;
            if (AreSameFingerprint(localFingerprint, remoteFingerprint))
            {
                selectedAccount = localAccount ?? remoteAccount;
            }
            else
            {
                var localChanged = !AreSameFingerprint(localFingerprint, baselineFingerprint);
                var remoteChanged = !AreSameFingerprint(remoteFingerprint, baselineFingerprint);
                if (localChanged && remoteChanged)
                {
                    // The user workflow guarantees that one account is not played on two devices at once.
                    // If that guarantee is broken, prefer the version just read from the cloud and surface a warning.
                    selectedAccount = remoteAccount;
                    conflictingAccountUids.Add(uid);
                }
                else
                {
                    selectedAccount = remoteChanged ? remoteAccount : localAccount;
                }
            }

            if (selectedAccount is not null)
            {
                mergedAccounts.Add(selectedAccount);
            }
        }

        var merged = new StatisticsDocument
        {
            Info = remote.Info,
            Accounts = mergedAccounts
        };
        return new StatisticsDocumentMergeResult(
            StatisticsDocumentNormalizer.Normalize(merged),
            conflictingAccountUids);
    }

    private static StatisticsDocument MergeWithRemoteAccountPriority(
        StatisticsDocument local,
        StatisticsDocument remote)
    {
        foreach (var remoteAccount in remote.Accounts)
        {
            var localAccountIndex = local.Accounts.FindIndex(account =>
                string.Equals(account.Uid, remoteAccount.Uid, StringComparison.OrdinalIgnoreCase));
            if (localAccountIndex < 0)
            {
                local.Accounts.Add(remoteAccount);
            }
            else
            {
                local.Accounts[localAccountIndex] = remoteAccount;
            }
        }

        return StatisticsDocumentNormalizer.Normalize(local);
    }

    private static StatisticsDocument MergeLegacy(StatisticsDocument local, StatisticsDocument remote)
    {
        foreach (var remoteAccount in remote.Accounts)
        {
            var localAccount = local.Accounts.FirstOrDefault(account =>
                string.Equals(account.Uid, remoteAccount.Uid, StringComparison.OrdinalIgnoreCase));
            if (localAccount is null)
            {
                local.Accounts.Add(remoteAccount);
                continue;
            }

            MergeAccount(localAccount, remoteAccount);
        }

        return StatisticsDocumentNormalizer.Normalize(local);
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

    private static Dictionary<string, string> NormalizeFingerprints(
        IReadOnlyDictionary<string, string> fingerprints)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (uid, fingerprint) in fingerprints)
        {
            if (!string.IsNullOrWhiteSpace(uid) && !string.IsNullOrWhiteSpace(fingerprint))
            {
                normalized[uid.Trim()] = fingerprint.Trim();
            }
        }

        return normalized;
    }

    private static string ComputeAccountFingerprint(AccountStatisticsData account)
    {
        var json = JsonSerializer.Serialize(account, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static bool AreSameFingerprint(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
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
