using RocoPilot.Models.Statistics;

namespace RocoPilot.Services.Statistics;

internal static class StatisticsAccountVersions
{
    public static void Enable(AccountStatisticsData account)
    {
        account.SyncVersion ??= new StatisticsAccountVersion
        {
            LegacyFingerprint = StatisticsDocumentMerger.ComputeAccountFingerprint(account)
        };
    }

    public static void Advance(StatisticsAccountVersion version, string device) =>
        version.Devices[device] = checked(version.Devices.GetValueOrDefault(device) + 1);

    public static void Include(StatisticsAccountVersion target, StatisticsAccountVersion source)
    {
        foreach (var (device, counter) in source.Devices)
            target.Devices[device] = Math.Max(counter, target.Devices.GetValueOrDefault(device));
    }

    public static StatisticsAccountSnapshot Select(StatisticsAccountSnapshot local, StatisticsAccountSnapshot remote)
    {
        var left = local.Version.Devices;
        var right = remote.Version.Devices;
        // 未修改的旧数据只有升级前指纹；不能把不相关的旧文档当成同一分支。
        if ((left.Count == 0 || right.Count == 0)
            && local.Version.LegacyFingerprint != remote.Version.LegacyFingerprint)
            throw new StatisticsSyncConflictException(local, remote);

        var localNewer = left.Any(x => x.Value > right.GetValueOrDefault(x.Key));
        var remoteNewer = right.Any(x => x.Value > left.GetValueOrDefault(x.Key));
        if (localNewer && remoteNewer) throw new StatisticsSyncConflictException(local, remote);
        if (!localNewer && !remoteNewer
            && StatisticsDocumentMerger.Fingerprint(local) != StatisticsDocumentMerger.Fingerprint(remote))
            throw new StatisticsSyncConflictException(local, remote);
        return remoteNewer ? remote : local;
    }
}
