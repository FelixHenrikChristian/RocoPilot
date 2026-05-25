using RocoPilot.Models.Statistics;

namespace RocoPilot.Contracts.Services.Statistics;

public interface IStatisticsSyncService
{
    event EventHandler<StatisticsSyncStatusChangedEventArgs>? StatusChanged;

    StatisticsSyncStatus CurrentStatus { get; }

    IReadOnlyList<StatisticsSyncProviderOption> GetProviders();

    Task<StatisticsSyncSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);

    Task<StatisticsSyncStatus> LoadStatusAsync(CancellationToken cancellationToken = default);

    Task<StatisticsSyncStatus> SaveSettingsAsync(
        StatisticsSyncSettings settings,
        string? password,
        CancellationToken cancellationToken = default);

    Task<StatisticsSyncRemoteInfo> RefreshRemoteInfoAsync(CancellationToken cancellationToken = default);

    Task<StatisticsSyncResult> TestConnectionAsync(CancellationToken cancellationToken = default);

    Task<StatisticsSyncResult> UploadAsync(CancellationToken cancellationToken = default);

    Task<StatisticsSyncResult> DownloadAsync(CancellationToken cancellationToken = default);
}

public sealed class StatisticsSyncStatusChangedEventArgs : EventArgs
{
    public StatisticsSyncStatusChangedEventArgs(StatisticsSyncStatus status)
    {
        Status = status;
    }

    public StatisticsSyncStatus Status { get; }
}
