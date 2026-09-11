using RocoPilot.Models.Statistics;

namespace RocoPilot.Contracts.Services.Statistics;

public interface IStatisticsRemoteStore
{
    Task<StatisticsSyncRemoteInfo> ReadInfoAsync(StatisticsSyncSettings settings, string secret, CancellationToken cancellationToken);
    Task<StatisticsRemoteDownload> DownloadAsync(StatisticsSyncSettings settings, string secret, CancellationToken cancellationToken);
    Task<StatisticsRemoteUpload> UploadAsync(StatisticsSyncSettings settings, string secret, StatisticsDocument document,
        StatisticsSyncRemoteInfo expectedVersion, CancellationToken cancellationToken);
}

public sealed record StatisticsRemoteDownload(StatisticsDocument Document, StatisticsSyncRemoteInfo Info);

public sealed record StatisticsRemoteUpload(bool HasConflict, StatisticsSyncResult? Result = null);
