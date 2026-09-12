using RocoPilot.Contracts.Services.Encounters;
using RocoPilot.Contracts.Services.Spirits;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Models.Encounters;
using RocoPilot.Models.Spirits;
using RocoPilot.Models.Statistics;

namespace RocoPilot.Tests.TestDoubles;

internal sealed class StatisticsSeasonConfigStub : IEncounterSeasonConfigService
{
    public int LoadCount { get; private set; }
    public EncounterSeasonConfig Load()
    {
        LoadCount++;
        return new EncounterSeasonConfig();
    }
    public EncounterSeasonDefinition? GetCurrentSeason() => null;
}

internal sealed class StatisticsUidCoordinatorStub : IStatisticsUidCoordinatorService
{
    public event EventHandler? PendingConfirmationChanged { add { } remove { } }
    public StatisticsUidConfirmationRequest? PendingConfirmation => null;
    public void MarkPendingConfirmationPresented() { }
    public Task<StatisticsUidDetectionResult> RetryDetectionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> ConfirmUidAsync(string uid, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class StatisticsSyncStub : IStatisticsSyncService
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public event EventHandler<StatisticsSyncStatusChangedEventArgs>? StatusChanged;
    public StatisticsSyncStatus CurrentStatus { get; private set; } = new();
    public void SetStatus(StatisticsSyncStatus status)
    {
        CurrentStatus = status;
        StatusChanged?.Invoke(this, new StatisticsSyncStatusChangedEventArgs(status));
    }
    public IReadOnlyList<StatisticsSyncProviderOption> GetProviders() => [];
    public Task<StatisticsSyncStatus> LoadStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(CurrentStatus);
    public Task<StatisticsSyncSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<StatisticsSyncStatus> SaveSettingsAsync(StatisticsSyncSettings settings, string? password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<StatisticsSyncRemoteInfo> RefreshRemoteInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<StatisticsSyncResult> TestConnectionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> DownloadRemoteChangesIfNeededAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<StatisticsSyncResult> UploadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<StatisticsSyncResult> DownloadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class StatisticsSpiritCatalogStub : ISpiritCatalogService
{
    public SpiritCatalogDocument Document { get; set; } = new();
    public Func<Task>? BeforeLoad { get; set; }
    public int LoadCount { get; private set; }
    public async Task<SpiritCatalogDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadCount++;
        if (BeforeLoad is { } beforeLoad) await beforeLoad();
        return Document;
    }
    public string? ResolveAvatarPath(string? avatarPath) => null;
    public IReadOnlyList<SpiritCatalogSourceOption> GetSources() => [];
    public Task<SpiritCatalogDocument> LoadAsync(string sourceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpiritCatalogDocument> SyncAsync(IProgress<SpiritCatalogSyncProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpiritCatalogDocument> SyncAsync(string sourceId, IProgress<SpiritCatalogSyncProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> MatchSpiritNameAsync(string recognizedText, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> MatchSpiritNameAsync(string recognizedText, double minimumSimilarity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> ResolveEvolutionRecordNameAsync(string spiritName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
