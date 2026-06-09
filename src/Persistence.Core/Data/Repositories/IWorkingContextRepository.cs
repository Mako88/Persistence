using Persistence.Data.Entities;
using System.Data;

namespace Persistence.Data.Repositories;

/// <summary>
/// A lightweight, fragment-count-only view of a working context — enough to list/browse
/// contexts without hydrating every fragment (and its sources and tags) for each one.
/// Mapped by column name (not constructor position) so Dapper applies the registered
/// <c>DateTimeOffset</c> type handler to <see cref="LastAccessedUtc"/>, as it does for entities.
/// </summary>
public class WorkingContextSummary
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public int FragmentCount { get; set; }
    public DateTimeOffset LastAccessedUtc { get; set; }
}

/// <summary>
/// Repository for <see cref="WorkingContextEntity"/> and its junction-table operations.
/// Loaded contexts always have their <see cref="WorkingContextEntity.ContextFragments"/>
/// collection populated, including each fragment's <see cref="ContextFragmentEntity.Sources"/>
/// and <see cref="ContextFragmentEntity.Tags"/>.
/// </summary>
public interface IWorkingContextRepository : IEntityRepository<WorkingContextEntity>
{
    /// <summary>
    /// Returns the most recently accessed <see cref="WorkingContextEntity"/>, fully
    /// populated, or <c>null</c> if no contexts exist yet.
    /// </summary>
    Task<WorkingContextEntity?> GetMostRecentAsync();

    /// <summary>
    /// Returns a lightweight summary of every non-deleted working context (most recently
    /// accessed first), without hydrating fragments — for listing/browsing contexts.
    /// </summary>
    Task<IReadOnlyList<WorkingContextSummary>> GetSummariesAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates, persists, and returns a new empty <see cref="WorkingContextEntity"/> with
    /// the given name. An audit entry is written in the same transaction.
    /// </summary>
    Task<WorkingContextEntity> CreateAsync(string name);

    /// <summary>
    /// Removes a fragment from a context's junction table. Add and update operations
    /// are handled by cascade save — this explicit method exists because deletes
    /// require knowing what to remove rather than diffing in-memory state.
    /// </summary>
    Task RemoveFragmentAsync(long contextId, long fragmentId, IDbTransaction? transaction = null);

}
