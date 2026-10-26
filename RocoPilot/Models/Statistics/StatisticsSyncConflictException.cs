namespace RocoPilot.Models.Statistics;

// Account 为 null 表示账号已删除。快照用于展示和确认前的内容校验。
public sealed record StatisticsAccountSnapshot(
    string Uid, AccountStatisticsData? Account, StatisticsAccountVersion Version);

public sealed class StatisticsSyncConflictException(
    StatisticsAccountSnapshot local, StatisticsAccountSnapshot remote)
    : InvalidOperationException($"账号 {local.Uid} 的本地和云端修改存在冲突，双方数据均已保留。请在云同步窗口点击上传或合并，核对后选择要保留的账号版本。")
{
    public StatisticsAccountSnapshot Local { get; } = local;
    public StatisticsAccountSnapshot Remote { get; } = remote;
}
