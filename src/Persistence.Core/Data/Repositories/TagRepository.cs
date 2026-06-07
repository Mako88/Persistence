using Dapper;
using InterpolatedSql.Dapper;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Runtime;
using System.Data;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for <see cref="TagEntity"/>. All load paths populate
/// <see cref="TagEntity.ChildTags"/> one level deep via a single batched <c>IN</c> query.
/// Children are not recursively populated.
/// </summary>
[Singleton]
public class TagRepository : EntityRepository<TagEntity>, ITagRepository
{
    /// <summary>
    /// Constructor
    /// </summary>
    public TagRepository(IAppConfig config, ISessionContext sessionContext)
        : base(config, sessionContext) { }

    /// <summary>
    /// Returns all root tags (those with no parent), with children populated
    /// </summary>
    public async Task<IEnumerable<TagEntity>> GetAllRootAsync() =>
        await QueryAsync($"SELECT * FROM Tags WHERE ParentTagId IS NULL");

    /// <summary>
    /// Returns the tag matching the given name within the given parent scope, or null
    /// </summary>
    public async Task<TagEntity?> GetByNameAsync(string name, long? parentTagId = null)
    {
        if (parentTagId == null)
        {
            return await QueryFirstOrDefaultAsync(
                $"SELECT * FROM Tags WHERE Name = {name} AND ParentTagId IS NULL");
        }

        return await QueryFirstOrDefaultAsync(
            $"SELECT * FROM Tags WHERE Name = {name} AND ParentTagId = {parentTagId}");
    }

    /// <summary>
    /// Returns the immediate children of the given parent tag
    /// </summary>
    public async Task<IEnumerable<TagEntity>> GetChildrenAsync(long parentTagId) =>
        await QueryAsync($"SELECT * FROM Tags WHERE ParentTagId = {parentTagId}");

    /// <summary>
    /// Deletes the given tag and all its descendants along with their fragment associations,
    /// leaving the fragments themselves untouched; returns the number of tags deleted
    /// </summary>
    public async Task<int> DeleteTreeAsync(long tagId, CancellationToken ct = default)
    {
        // Collect the tag and all descendants (depth-first), then remove their fragment
        // associations and the tag rows. Fragments are never touched.
        var toDelete = new List<long>();
        var queue = new Queue<long>();
        queue.Enqueue(tagId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            toDelete.Add(current);

            foreach (var child in await GetChildrenAsync(current))
            {
                queue.Enqueue(child.Id);
            }
        }

        await ExecuteAsync($"DELETE FROM ContextFragmentTags WHERE TagId IN {toDelete}", ct: ct);
        await ExecuteAsync($"DELETE FROM Tags WHERE Id IN {toDelete}", ct: ct);

        return toDelete.Count;
    }

    #region Base overrides

    /// <summary>
    /// Loads tags by ID with their ChildTags collection populated one level deep
    /// </summary>
    protected override async Task<IEnumerable<TagEntity>> LoadByIdsAsync(
        IEnumerable<long> ids, IDbConnection connection, CancellationToken ct = default)
    {
        var idList = ids.ToList();

        var tags = (await connection.SqlBuilder(
            $"SELECT * FROM Tags WHERE Id IN {idList}")
            .QueryAsync<TagEntity>(cancellationToken: ct)).ToList();

        if (tags.Count == 0)
        {
            return tags;
        }

        var tagIds = tags.Select(t => t.Id).ToList();

        var children = (await connection.SqlBuilder(
            $"SELECT * FROM Tags WHERE ParentTagId IN {tagIds}")
            .QueryAsync<TagEntity>(cancellationToken: ct)).ToList();

        var childrenByParent = children
            .GroupBy(c => c.ParentTagId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var tag in tags)
        {
            tag.ChildTags = childrenByParent.GetValueOrDefault(tag.Id, []);
        }

        return tags;
    }

    /// <summary>
    /// Returns the INSERT statement for a tag entity
    /// </summary>
    protected override FormattableString GetInsertSql(TagEntity entity) =>
        $"""
        INSERT INTO Tags (Name, ParentTagId, Description, LastAccessedUtc, CreatedUtc, LastModifiedUtc, Notes)
        VALUES ({entity.Name}, {entity.ParentTagId}, {entity.Description}, {entity.LastAccessedUtc}, {entity.CreatedUtc}, {entity.LastModifiedUtc}, {entity.Notes})
        """;

    /// <summary>
    /// Returns the UPDATE statement for a tag entity
    /// </summary>
    protected override FormattableString GetUpdateSql(TagEntity entity) =>
        $"""
        UPDATE Tags
        SET Name = {entity.Name}, ParentTagId = {entity.ParentTagId}, Description = {entity.Description},
            LastAccessedUtc = {entity.LastAccessedUtc},
            LastModifiedUtc = {entity.LastModifiedUtc}, Notes = {entity.Notes}
        WHERE Id = {entity.Id}
        """;

    #endregion
}
