using Persistence.Data.Entities;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for managing context fragments
/// </summary>
public interface IContextFragmentRepository : IEntityRepository<ContextFragmentEntity>
{
    /// <summary>
    /// Returns fragments of the given type. Defaults to active only.
    /// Pass <c>activeOnly: false</c> to include all statuses.
    /// </summary>
    Task<IEnumerable<ContextFragmentEntity>> GetByTypeAsync(ContextFragmentType type, bool activeOnly = true);

    /// <summary>
    /// Returns all fragments tagged with the given tag ID, regardless of status
    /// </summary>
    Task<IEnumerable<ContextFragmentEntity>> GetByTagAsync(long tagId);

    /// <summary>
    /// Returns up to <paramref name="limit"/> results ordered best-match first.
    /// </summary>
    Task<IEnumerable<ContextFragmentEntity>> SearchRelevantAsync(string query, int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes (<c>forget</c>) or restores (<c>unforget</c>) a fragment by flipping its
    /// <see cref="ContextFragmentEntity.IsDeleted"/> flag only — content, tags, and status untouched.
    /// When forgetting, an optional <paramref name="reason"/> is recorded (in Notes) for later recall.
    /// </summary>
    Task SetDeletedAsync(long id, bool deleted, string? reason = null, CancellationToken ct = default);

    /// <summary>
    /// Returns soft-deleted (forgotten) fragments, most-recently-forgotten first — the recovery
    /// surface for <c>unforget</c>. Flat rows (no tag hydration); intended for a compact listing.
    /// </summary>
    Task<IReadOnlyList<ContextFragmentEntity>> GetDeletedAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Counts what's been set aside but is still recoverable: forgotten (soft-deleted) fragments and
    /// (separately) archived-but-not-deleted ones. Feeds the sensory block's curation line.
    /// </summary>
    Task<(int Forgotten, int Archived)> CountAsideAsync(CancellationToken ct = default);
}
