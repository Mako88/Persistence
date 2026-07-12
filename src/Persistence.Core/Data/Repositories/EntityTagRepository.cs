using Dapper;
using InterpolatedSql.Dapper;
using Microsoft.Data.Sqlite;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using System.Data;

namespace Persistence.Data.Repositories;

/// <summary>
/// Generic tag-link repository backed by the polymorphic <c>EntityTags</c> table. Junction-only
/// (no entity of its own), so it manages its own connection rather than extending
/// <see cref="EntityRepository{T}"/>, but accepts an ambient transaction so writes can join an
/// entity save.
/// </summary>
[Singleton]
public class EntityTagRepository : IEntityTagRepository
{
    private readonly string connectionString;

    /// <summary>
    /// Constructor
    /// </summary>
    public EntityTagRepository(IAppConfig config)
    {
        connectionString = SqliteConnectionString.For(config.DatabasePath);
    }

    /// <inheritdoc/>
    public async Task SetTagsAsync(string entityType, long entityId, IReadOnlyList<long> tagIds, IDbTransaction? transaction = null)
    {
        if (transaction?.Connection != null)
        {
            await SetTagsCoreAsync(transaction.Connection, transaction, entityType, entityId, tagIds);
            return;
        }

        await using var connection = await OpenAsync();
        await SetTagsCoreAsync(connection, null, entityType, entityId, tagIds);
    }

    private static async Task SetTagsCoreAsync(
        IDbConnection connection, IDbTransaction? transaction, string entityType, long entityId, IReadOnlyList<long> tagIds)
    {
        await connection.SqlBuilder(
            $"DELETE FROM EntityTags WHERE EntityType = {entityType} AND EntityId = {entityId}")
            .ExecuteAsync(transaction);

        foreach (var tagId in tagIds)
        {
            await connection.SqlBuilder(
                $"INSERT INTO EntityTags (TagId, EntityType, EntityId) VALUES ({tagId}, {entityType}, {entityId})")
                .ExecuteAsync(transaction);
        }
    }

    /// <inheritdoc/>
    public async Task RemoveTagsAsync(IReadOnlyList<long> tagIds, IDbTransaction? transaction = null)
    {
        if (tagIds.Count == 0)
        {
            return;
        }

        if (transaction?.Connection != null)
        {
            await transaction.Connection.SqlBuilder($"DELETE FROM EntityTags WHERE TagId IN {tagIds}").ExecuteAsync(transaction);
            return;
        }

        await using var connection = await OpenAsync();
        await connection.SqlBuilder($"DELETE FROM EntityTags WHERE TagId IN {tagIds}").ExecuteAsync();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<long, List<TagEntity>>> GetTagsForAsync(
        string entityType, IReadOnlyList<long> entityIds, IDbConnection connection, CancellationToken ct = default)
    {
        if (entityIds.Count == 0)
        {
            return new Dictionary<long, List<TagEntity>>();
        }

        var rows = await connection.SqlBuilder(
            $"""
            SELECT et.EntityId, t.*
            FROM EntityTags et
            JOIN Tags t ON et.TagId = t.Id
            WHERE et.EntityType = {entityType} AND et.EntityId IN {entityIds}
            """)
            .QueryAsync<long, TagEntity, (long EntityId, TagEntity Tag)>(
                (entityId, tag) => (entityId, tag),
                splitOn: "Id",
                cancellationToken: ct);

        return rows
            .GroupBy(x => x.EntityId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Tag).ToList());
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<long>> GetEntityIdsWithTagAsync(string entityType, long tagId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync();

        var ids = await connection.SqlBuilder(
            $"SELECT EntityId FROM EntityTags WHERE EntityType = {entityType} AND TagId = {tagId}")
            .QueryAsync<long>(cancellationToken: ct);

        return ids.ToList();
    }

    private Task<SqliteConnection> OpenAsync() =>
        SqliteConnectionString.OpenAsync(connectionString);
}
