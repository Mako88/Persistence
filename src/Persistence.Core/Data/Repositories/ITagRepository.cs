using Persistence.Data.Entities;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for <see cref="TagEntity"/>. Loaded tags have their
/// <see cref="TagEntity.ChildTags"/> collection populated (one level deep).
/// </summary>
public interface ITagRepository : IEntityRepository<TagEntity>
{
    /// <summary>
    /// Returns all root tags (those with no parent), each with their
    /// <see cref="TagEntity.ChildTags"/> populated one level deep
    /// </summary>
    Task<IEnumerable<TagEntity>> GetAllRootAsync();

    /// <summary>
    /// Returns the tag matching the given name within the given parent scope, or <c>null</c>
    /// if no match exists. Pass <c>null</c> for <paramref name="parentTagId"/> to search
    /// among root tags.
    /// </summary>
    Task<TagEntity?> GetByNameAsync(string name, long? parentTagId = null);

    /// <summary>
    /// Returns the immediate children of the given parent tag, each with their own
    /// <see cref="TagEntity.ChildTags"/> populated.
    /// </summary>
    Task<IEnumerable<TagEntity>> GetChildrenAsync(long parentTagId);
}
