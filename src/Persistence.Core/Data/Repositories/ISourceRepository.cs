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
    /// Returns the id of the source with the given name <em>and</em> type, creating it if absent.
    ///
    /// <para>The type is part of the identity, not decoration: in the room (ADR-0008) a message can
    /// arrive from another digital peer, and it must be sourced as a <see cref="SourceType.DigitalPeer"/>
    /// so the receiving peer can tell a peer's voice from a person's. Matching on name alone would let
    /// a peer called "Ember" collide with a person called "Ember" and quietly relabel one as the other.</para>
    /// </summary>
    Task<long> EnsureNamedSourceAsync(string name, SourceType type, CancellationToken ct = default);

    /// <summary>
    /// Returns all non-deleted sources
    /// </summary>
    Task<IEnumerable<SourceEntity>> GetAllAsync(CancellationToken ct = default);
}
