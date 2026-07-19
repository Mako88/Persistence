using Dapper;
using InterpolatedSql.Dapper;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Runtime;
using System.Data;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for <see cref="ContextFragmentEntity"/>. Every load fully populates
/// <see cref="ContextFragmentEntity.Sources"/> and <see cref="ContextFragmentEntity.Tags"/>.
/// </summary>
[Singleton]
public class ContextFragmentRepository : EntityRepository<ContextFragmentEntity>, IContextFragmentRepository
{
    private readonly ISessionContext sessionContext;
    private readonly IEntityTagRepository entityTagRepo;

    /// <summary>
    /// Constructor
    /// </summary>
    public ContextFragmentRepository(IAppConfig config, ISessionContext sessionContext, IEntityTagRepository entityTagRepo)
        : base(config, sessionContext)
    {
        this.sessionContext = sessionContext;
        this.entityTagRepo = entityTagRepo;
    }

    #region Public methods

    /// <summary>
    /// Returns fragments of the given type. Defaults to active only.
    /// Pass <c>activeOnly: false</c> to include all statuses.
    /// </summary>
    public async Task<IEnumerable<ContextFragmentEntity>> GetByTypeAsync(ContextFragmentType type, bool activeOnly = true) =>
        activeOnly
            ? await QueryAsync(
                $"""
                SELECT * FROM ContextFragments
                WHERE FragmentType = {type} AND Status = {ContextFragmentStatus.Active} AND IsDeleted = 0
                """)
            : await QueryAsync(
                $"SELECT * FROM ContextFragments WHERE FragmentType = {type} AND IsDeleted = 0");

    /// <summary>
    /// Returns all non-deleted fragments tagged with the given tag ID, regardless of status
    /// </summary>
    public async Task<IEnumerable<ContextFragmentEntity>> GetByTagAsync(long tagId) =>
        await QueryAsync(
            $"""
            SELECT cf.*
            FROM ContextFragments cf
            JOIN EntityTags et ON cf.Id = et.EntityId AND et.EntityType = {nameof(ContextFragmentEntity)}
            WHERE et.TagId = {tagId} AND cf.IsDeleted = 0
            """);

    /// <summary>
    /// Returns up to <paramref name="limit"/> results ordered best-match first.
    /// </summary>
    public async Task<IEnumerable<ContextFragmentEntity>> SearchRelevantAsync(
        string query, int limit = 20, CancellationToken ct = default)
    {
        var rankedIds = await QueryAsync<long>(
            $"""
            SELECT rowid FROM ContextFragments_fts
            WHERE ContextFragments_fts MATCH {query}
            ORDER BY rank
            LIMIT {limit}
            """);

        if (!rankedIds.Any())
        {
            return [];
        }

        // Exclude forgotten (soft-deleted) fragments: the FTS index is built on content, so flipping
        // IsDeleted doesn't drop a row from the index — without this filter a forgotten fragment would
        // still resurface through every relevance-search caller (list_fragments relevant_to, recall).
        var entities = (await GetByIdsAsync(rankedIds, ct))
            .Where(e => !e.IsDeleted)
            .ToDictionary(e => e.Id);

        return rankedIds.Where(id => entities.ContainsKey(id)).Select(id => entities[id]);
    }

    #endregion

    #region Base overrides

    /// <summary>
    /// Loads fragments by ID with their sources and tags populated
    /// </summary>
    protected override async Task<IEnumerable<ContextFragmentEntity>> LoadByIdsAsync(
        IEnumerable<long> ids, IDbConnection connection, CancellationToken ct = default)
    {
        var idList = ids.ToList();

        var fragments = (await connection.SqlBuilder(
            $"SELECT * FROM ContextFragments WHERE Id IN {idList}")
            .QueryAsync<ContextFragmentEntity>(cancellationToken: ct)).ToList();

        if (fragments.Count == 0)
        {
            return fragments;
        }

        var fragmentIds = fragments.Select(f => f.Id).ToList();

        // Sources for all fragments
        var sourceRows = await connection.SqlBuilder(
            $"""
            SELECT cfs.ContextFragmentId, s.*
            FROM ContextFragmentSources cfs
            JOIN Sources s ON cfs.SourceId = s.Id
            WHERE cfs.ContextFragmentId IN {fragmentIds}
            """)
            .QueryAsync<long, SourceEntity, (long FragmentId, SourceEntity Source)>(
                (fragmentId, source) => (fragmentId, source),
                splitOn: "Id",
                cancellationToken: ct);

        var sourcesByFragment = sourceRows
            .GroupBy(x => x.FragmentId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Source).ToList());

        // Tags for all fragments (via the generic EntityTags table)
        var tagsByFragment = await entityTagRepo.GetTagsForAsync(nameof(ContextFragmentEntity), fragmentIds, connection, ct);

        foreach (var fragment in fragments)
        {
            fragment.Sources = sourcesByFragment.GetValueOrDefault(fragment.Id, []);
            fragment.Tags = tagsByFragment.GetValueOrDefault(fragment.Id, []);
        }

        return fragments;
    }

    /// <summary>
    /// Syncs the ContextFragmentTags junction table to match the in-memory Tags list
    /// </summary>
    protected override async Task SaveSubEntitiesAsync(
        ContextFragmentEntity entity, IDbTransaction transaction, CancellationToken ct = default)
    {
        await ExecuteAsync(
            $"DELETE FROM ContextFragmentSources WHERE ContextFragmentId = {entity.Id}",
            transaction, ct);

        if (entity.Sources.Count == 0)
        {
            entity.Sources.Add(new SourceEntity
            {
                Id = sessionContext.SystemSourceId,
                SourceType = SourceType.System,
                CreatedUtc = entity.CreatedUtc,
                LastModifiedUtc = entity.LastModifiedUtc,
            });
        }

        foreach (var source in entity.Sources)
        {
            await ExecuteAsync(
                $"INSERT INTO ContextFragmentSources (SourceId, ContextFragmentId) VALUES ({source.Id}, {entity.Id})",
                transaction, ct);
        }

        await entityTagRepo.SetTagsAsync(
            nameof(ContextFragmentEntity), entity.Id, entity.Tags.Select(t => t.Id).ToList(), transaction);
    }

    /// <inheritdoc />
    public async Task SetDeletedAsync(long id, bool deleted, string? reason = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // On forget, record the (optional) reason in the free-form Notes field so list_forgotten can show
        // "why did I let this go" later. On unforget we leave Notes as the historical record.
        if (deleted && !string.IsNullOrWhiteSpace(reason))
        {
            await ExecuteAsync(
                $"UPDATE ContextFragments SET IsDeleted = 1, Notes = {reason.Trim()}, LastModifiedUtc = {now} WHERE Id = {id}",
                ct: ct);
        }
        else
        {
            await ExecuteAsync(
                $"UPDATE ContextFragments SET IsDeleted = {deleted}, LastModifiedUtc = {now} WHERE Id = {id}",
                ct: ct);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContextFragmentEntity>> GetDeletedAsync(int limit = 20, CancellationToken ct = default) =>
        // Flat query (not the hydrating QueryAsync): the entity reload filters/joins aren't needed for a
        // recovery listing, and going flat avoids re-touching LastAccessed on fragments merely being reviewed.
        (await QueryAsync<ContextFragmentEntity>(
            $"SELECT * FROM ContextFragments WHERE IsDeleted = 1 ORDER BY LastModifiedUtc DESC LIMIT {limit}",
            ct)).ToList();

    /// <inheritdoc />
    public async Task<(int Forgotten, int Archived)> CountAsideAsync(CancellationToken ct = default)
    {
        var forgotten = (await QueryAsync<int>(
            $"SELECT COUNT(*) FROM ContextFragments WHERE IsDeleted = 1", ct)).FirstOrDefault();

        var archived = (await QueryAsync<int>(
            $"SELECT COUNT(*) FROM ContextFragments WHERE IsDeleted = 0 AND Status = {ContextFragmentStatus.Archived}", ct))
            .FirstOrDefault();

        return (forgotten, archived);
    }

    /// <summary>
    /// Returns the INSERT statement for a context fragment
    /// </summary>
    protected override FormattableString GetInsertSql(ContextFragmentEntity entity) =>
        $"""
        INSERT INTO ContextFragments (FragmentType, Status, Content, Summary, LastAccessedUtc, Importance, Confidence, IsProtected, IsDeleted, CreatedUtc, LastModifiedUtc, Notes, AddressedTo, MessageId, RelayDepth)
        VALUES ({entity.FragmentType}, {entity.Status}, {entity.Content}, {entity.Summary}, {entity.LastAccessedUtc}, {entity.Importance}, {entity.Confidence}, {entity.IsProtected}, {entity.IsDeleted}, {entity.CreatedUtc}, {entity.LastModifiedUtc}, {entity.Notes}, {entity.AddressedTo}, {entity.MessageId}, {entity.RelayDepth})
        """;

    /// <summary>
    /// Returns the UPDATE statement for a context fragment
    /// </summary>
    protected override FormattableString GetUpdateSql(ContextFragmentEntity entity) =>
        $"""
        UPDATE ContextFragments
        SET FragmentType = {entity.FragmentType}, Status = {entity.Status}, Content = {entity.Content},
            Summary = {entity.Summary}, LastAccessedUtc = {entity.LastAccessedUtc}, Importance = {entity.Importance},
            Confidence = {entity.Confidence}, IsProtected = {entity.IsProtected}, IsDeleted = {entity.IsDeleted},
            LastModifiedUtc = {entity.LastModifiedUtc}, Notes = {entity.Notes}, AddressedTo = {entity.AddressedTo},
            MessageId = {entity.MessageId}, RelayDepth = {entity.RelayDepth}
        WHERE Id = {entity.Id}
        """;

    #endregion
}
