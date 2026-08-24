using Janus.Core.Models;

namespace Janus.Core.Abstractions;

public interface IJanusRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<Asset> CreateAssetAsync(Asset asset, CancellationToken cancellationToken = default);
    Task<Asset?> UpdateAssetAsync(Asset asset, CancellationToken cancellationToken = default);
    Task<Asset?> GetAssetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Asset>> FindAssetsAsync(string query, CancellationToken cancellationToken = default);
    Task<Fact> SetFactAsync(Fact fact, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Fact>> GetFactsAsync(Guid assetId, CancellationToken cancellationToken = default);
    Task<bool> RemoveFactAsync(Guid assetId, string key, CancellationToken cancellationToken = default);
    Task<AssetEvent> RecordEventAsync(AssetEvent assetEvent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AssetEvent>> GetEventsAsync(Guid assetId, CancellationToken cancellationToken = default);
}
