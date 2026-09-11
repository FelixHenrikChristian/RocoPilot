using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Statistics;

namespace RocoPilot.Tests.TestDoubles;

internal sealed class StatisticsRemoteStoreStub : IStatisticsRemoteStore
{
    public Func<StatisticsSyncSettings, CancellationToken, Task<StatisticsSyncRemoteInfo>> OnRead { get; set; } =
        (_, _) => Task.FromResult(new StatisticsSyncRemoteInfo());
    public Func<StatisticsSyncSettings, CancellationToken, Task<StatisticsRemoteDownload>> OnDownload { get; set; } =
        (_, _) => throw new NotSupportedException();
    public Func<StatisticsDocument, StatisticsSyncRemoteInfo, CancellationToken, Task<StatisticsRemoteUpload>> OnUpload { get; set; } =
        (_, _, _) => throw new NotSupportedException();
    public Task<StatisticsSyncRemoteInfo> ReadInfoAsync(StatisticsSyncSettings settings, string secret, CancellationToken cancellationToken) => OnRead(settings, cancellationToken);
    public Task<StatisticsRemoteDownload> DownloadAsync(StatisticsSyncSettings settings, string secret, CancellationToken cancellationToken) => OnDownload(settings, cancellationToken);
    public Task<StatisticsRemoteUpload> UploadAsync(StatisticsSyncSettings settings, string secret, StatisticsDocument document,
        StatisticsSyncRemoteInfo expectedVersion, CancellationToken cancellationToken) => OnUpload(document, expectedVersion, cancellationToken);
}

internal sealed class StatisticsCredentialsStub : IStatisticsSyncCredentialStore
{
    public string? Password { get; private set; } = "test-secret";
    public string? Read(string userName) => Password;
    public void Save(string userName, string password) => Password = password;
}
