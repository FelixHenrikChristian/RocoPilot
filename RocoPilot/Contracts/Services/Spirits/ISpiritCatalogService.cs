using RocoPilot.Models.Spirits;

namespace RocoPilot.Contracts.Services.Spirits;

public interface ISpiritCatalogService
{
    Task<SpiritCatalogDocument> LoadAsync(CancellationToken cancellationToken = default);

    Task<SpiritCatalogDocument> SyncAsync(
        IProgress<SpiritCatalogSyncProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<string> MatchSpiritNameAsync(string recognizedText, CancellationToken cancellationToken = default);

    Task<string> MatchSpiritNameAsync(
        string recognizedText,
        double minimumSimilarity,
        CancellationToken cancellationToken = default);

    Task<string> ResolveEvolutionRecordNameAsync(string spiritName, CancellationToken cancellationToken = default);

    string? ResolveAvatarPath(string? avatarPath);
}
