using Persistence.Data.Entities;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for managing sources
/// </summary>
public interface ISourceRepository : IEntityRepository<SourceEntity>
{
    /// <summary>
    /// Create a source with the System type if none exist
    /// </summary>
    Task CreateSystemSourceIfNotExists();

    /// <summary>
    /// Create the digital-peer source (the runtime's own voice) if none exists
    /// </summary>
    Task CreateRemotePeerSourceIfNotExists();

    /// <summary>
    /// Returns the source with the given name, or null if not found
    /// </summary>
    Task<SourceEntity?> GetByNameAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns the id of the <see cref="SourceType.HumanPeer"/> source with the given name, creating
    /// it if absent — so each named local peer gets its own source for message attribution.
    /// </summary>
    Task<long> EnsureLocalPeerSourceAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns all non-deleted sources
    /// </summary>
    Task<IEnumerable<SourceEntity>> GetAllAsync(CancellationToken ct = default);
}
