using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using RocoPilot.Models.Statistics;

namespace RocoPilot.Services.Statistics;

internal static class StatisticsDocumentMerger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                typeInfo =>
                {
                    // 新增的空集合不改变旧账号的指纹，避免升级后误判为云端冲突。
                    foreach (var property in typeInfo.Properties.Where(property =>
                        (typeInfo.Type == typeof(AccountStatisticsData) && property.Name == "pendingEncounters")
                        || (typeInfo.Type == typeof(SeasonStatisticsData) && property.Name == "encounterCountResets")))
                        property.ShouldSerialize = (_, value) => value is System.Collections.ICollection { Count: > 0 };
                    if (typeInfo.Type == typeof(PendingShinyCaptureRecord))
                        foreach (var property in typeInfo.Properties.Where(property => property.Name == "rawText"))
                            property.ShouldSerialize = (_, value) => value is string { Length: > 0 };
                }
            }
        }
    };

    public static StatisticsDocument Prepare(StatisticsDocument source, IReadOnlyDictionary<string, string>? legacyBaseline = null)
    {
        var document = JsonSerializer.Deserialize<StatisticsDocument>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;
        var legacy = document.Info?.Version != StatisticsDocumentFormats.CurrentVersion;
        StatisticsDocumentNormalizer.Normalize(document);
        foreach (var account in document.Accounts) StatisticsAccountVersions.Enable(account);
        // 升级前已删除但尚未同步的账号不能因缺少标记被恢复。
        // 旧指纹无法还原版本历史，因此迁移成待核对的删除状态，不猜测先后。
        if (legacy && legacyBaseline is not null)
            foreach (var (uid, fingerprint) in legacyBaseline)
                if (Find(document, uid) is null)
                    document.DeletedAccounts[uid] = new StatisticsAccountVersion { LegacyFingerprint = fingerprint };
        StatisticsLegacyMigration.Apply(document);
        return StatisticsDocumentNormalizer.Normalize(document);
    }

    public static StatisticsDocument Merge(StatisticsDocument localDocument, StatisticsDocument remoteDocument)
    {
        var local = Prepare(localDocument);
        var remote = Prepare(remoteDocument);
        var result = new StatisticsDocument { Info = local.Info };
        var states = Snapshots(local).ToDictionary(x => x.Uid, StringComparer.OrdinalIgnoreCase);
        foreach (var incoming in Snapshots(remote))
            states[incoming.Uid] = states.TryGetValue(incoming.Uid, out var current)
                ? StatisticsAccountVersions.Select(current, incoming) : incoming;
        foreach (var state in states.Values) Put(result, state);
        return StatisticsDocumentNormalizer.Normalize(result);
    }

    internal static IEnumerable<StatisticsAccountSnapshot> Snapshots(StatisticsDocument document) =>
        document.Accounts.Select(x => new StatisticsAccountSnapshot(x.Uid, x, x.SyncVersion!))
            .Concat(document.DeletedAccounts.Select(x => new StatisticsAccountSnapshot(x.Key, null, x.Value)));

    internal static StatisticsAccountSnapshot? Find(StatisticsDocument document, string uid) =>
        Snapshots(document).FirstOrDefault(x => string.Equals(x.Uid, uid, StringComparison.OrdinalIgnoreCase));

    internal static void Put(StatisticsDocument document, StatisticsAccountSnapshot state)
    {
        document.Accounts.RemoveAll(x => string.Equals(x.Uid, state.Uid, StringComparison.OrdinalIgnoreCase));
        document.DeletedAccounts.Remove(state.Uid);
        if (state.Account is { } account)
        {
            account.SyncVersion = state.Version;
            document.Accounts.Add(account);
        }
        else document.DeletedAccounts[state.Uid] = state.Version;
    }

    public static Dictionary<string, string> ComputeAccountFingerprints(StatisticsDocument document) =>
        Snapshots(Prepare(document)).ToDictionary(x => x.Uid, Fingerprint, StringComparer.OrdinalIgnoreCase);

    internal static string Fingerprint(StatisticsAccountSnapshot? state) => state is null ? string.Empty
        : state.Account is { } account ? ComputeAccountFingerprint(account)
        : "deleted:" + Hash(JsonSerializer.Serialize(state.Version, JsonOptions));

    internal static string ComputeAccountFingerprint(AccountStatisticsData account) =>
        Hash(JsonSerializer.Serialize(account, JsonOptions));

    private static string Hash(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
}
