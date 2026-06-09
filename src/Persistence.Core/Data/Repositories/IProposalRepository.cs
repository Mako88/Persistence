using Persistence.Data.Entities;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for <see cref="ProposalEntity"/> — the peer's pending self-changes.
/// </summary>
public interface IProposalRepository : IEntityRepository<ProposalEntity>
{
    /// <summary>
    /// Returns all open (unresolved) proposals, oldest first.
    /// </summary>
    Task<IReadOnlyList<ProposalEntity>> GetOpenAsync(CancellationToken ct = default);
}
