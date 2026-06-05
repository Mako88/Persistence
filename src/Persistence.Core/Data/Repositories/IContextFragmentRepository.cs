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
}
