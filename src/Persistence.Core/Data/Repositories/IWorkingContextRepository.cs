using Persistence.Data.Entities;
using System.Data;

namespace Persistence.Data.Repositories;

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
