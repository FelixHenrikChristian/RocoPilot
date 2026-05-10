using RocoPilot.Models.Spirits;

namespace RocoPilot.Contracts.Services.Spirits;

public interface ISpiritCatalogService
{
    Task<SpiritCatalogDocument> LoadAsync(CancellationToken cancellationToken = default);

    Task<SpiritCatalogDocument> SyncAsync(
        IProgress<SpiritCatalogSyncProgress>? progress = null,
        CancellationToken cancellationToken = default);

    string? ResolveAvatarPath(string? avatarPath);
}
