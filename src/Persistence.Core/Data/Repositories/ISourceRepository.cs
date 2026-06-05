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
    /// Create a source with the LocalPeer type if none exist
    /// </summary>
    Task CreateLocalPeerSourceIfNotExists();

    /// <summary>
    /// Create a source with the RemotePeer type if none exist
    /// </summary>
    Task CreateRemotePeerSourceIfNotExists();

    /// <summary>
    /// Returns the source with the given name, or null if not found
    /// </summary>
    Task<SourceEntity?> GetByNameAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Returns all non-deleted sources
    /// </summary>
    Task<IEnumerable<SourceEntity>> GetAllAsync(CancellationToken ct = default);
}
