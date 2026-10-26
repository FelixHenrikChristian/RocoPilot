using RocoPilot.Models.Statistics;

namespace RocoPilot.Services.Statistics;

// 现有同步以账号为单位；版本向量只记录设备数量，与奇遇次数和处理批数无关。
internal static class StatisticsAccountVersions
{
    public static void Advance(AccountStatisticsData account, string device)
    {
        var version = account.SyncVersion ??= new();
        version.Devices[device] = checked(version.Devices.GetValueOrDefault(device) + 1);
        version.Devices = version.Devices.OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    }

    public static bool TrySelect(AccountStatisticsData local, AccountStatisticsData remote, out AccountStatisticsData selected, string? baselineFingerprint = null)
    {
        selected = local;
        if (local.SyncVersion is null && remote.SyncVersion is null) return false;
        if (local.SyncVersion is null || remote.SyncVersion is null)
        {
            var current = local.SyncVersion is not null ? local : remote;
            var legacy = local.SyncVersion is null ? local : remote;
            var fingerprint = StatisticsDocumentMerger.ComputeAccountFingerprint(legacy);
            if (current.SyncVersion!.LegacyFingerprint == fingerprint || baselineFingerprint == fingerprint)
            {
                selected = current;
                return true;
            }
            throw Conflict(local.Uid, "旧版数据与升级时的快照不同");
        }
        var left = local.SyncVersion.Devices;
        var right = remote.SyncVersion.Devices;
        var localNewer = left.Any(x => x.Value > right.GetValueOrDefault(x.Key));
        var remoteNewer = right.Any(x => x.Value > left.GetValueOrDefault(x.Key));
        if (localNewer && remoteNewer) throw Conflict(local.Uid, "两台设备都有尚未合并的修改");
        if (!localNewer && !remoteNewer
            && StatisticsDocumentMerger.ComputeAccountFingerprint(local) != StatisticsDocumentMerger.ComputeAccountFingerprint(remote))
            throw Conflict(local.Uid, "相同版本的内容不一致");
        selected = remoteNewer ? remote : local;
        return true;
    }

    private static InvalidOperationException Conflict(string uid, string reason) => new(
        $"账号 {uid} 同步冲突：{reason}。本地和云端数据均已保留，未覆盖或重复入账。请先导出两端统计备份并核对后导入选定的数据；旧版客户端需先升级。");
}
