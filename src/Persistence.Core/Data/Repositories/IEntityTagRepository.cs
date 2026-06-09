using Persistence.Data.Entities;
using System.Data;

namespace Persistence.Data.Repositories;

/// <summary>
/// The single generic tag-link surface over the polymorphic <c>EntityTags</c> table. Any taggable
/// entity (fragment, working context, scheduled event, …) is identified by its entity-type name and
/// id, so adding a new taggable type needs no new junction table.
/// </summary>
public interface IEntityTagRepository
{
    /// <summary>
    /// Replaces the set of tags on the given entity with <paramref name="tagIds"/> (delete-all then
    /// insert, mirroring the previous per-entity junction behaviour). Pass the ambient
    /// <paramref name="transaction"/> when called inside an entity save so it commits atomically.
    /// </summary>
    Task SetTagsAsync(string entityType, long entityId, IReadOnlyList<long> tagIds, IDbTransaction? transaction = null);

    /// <summary>
    /// Removes all links for the given tag ids (used when a tag is deleted).
    /// </summary>
    Task RemoveTagsAsync(IReadOnlyList<long> tagIds, IDbTransaction? transaction = null);

    /// <summary>
    /// Loads the tags for a batch of entities of one type, keyed by entity id. Takes the caller's
    /// open <paramref name="connection"/> so it can participate in an in-flight multi-mapping load.
    /// </summary>
    Task<IReadOnlyDictionary<long, List<TagEntity>>> GetTagsForAsync(
        string entityType, IReadOnlyList<long> entityIds, IDbConnection connection, CancellationToken ct = default);

    /// <summary>
    /// Returns the ids of entities of the given type carrying the given tag.
    /// </summary>
    Task<IReadOnlyList<long>> GetEntityIdsWithTagAsync(string entityType, long tagId, CancellationToken ct = default);
}
