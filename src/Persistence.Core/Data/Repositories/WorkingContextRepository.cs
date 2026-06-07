using Dapper;
using InterpolatedSql.Dapper;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Runtime;
using System.Data;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for <see cref="WorkingContextEntity"/>. Every load fully populates
/// <see cref="WorkingContextEntity.ContextFragments"/> including each fragment's Sources
/// and Tags. Saving a context cascades to its fragments and junction table entries
/// in a single transaction.
/// </summary>
[Singleton]
public class WorkingContextRepository : EntityRepository<WorkingContextEntity>, IWorkingContextRepository
{
    private readonly IContextFragmentRepository fragmentRepo;

    /// <summary>
    /// Constructor
    /// </summary>
    public WorkingContextRepository(IAppConfig config, ISessionContext sessionContext, IContextFragmentRepository fragmentRepo)
        : base(config, sessionContext)
    {
        this.fragmentRepo = fragmentRepo;
    }

    #region Public methods

    /// <summary>
    /// Returns the most recently accessed <see cref="WorkingContextEntity"/>, fully
    /// populated, or <c>null</c> if no contexts exist yet
    /// </summary>
    public async Task<WorkingContextEntity?> GetMostRecentAsync() =>
        await QueryFirstOrDefaultAsync(
            $"SELECT * FROM WorkingContexts ORDER BY LastAccessedUtc DESC LIMIT 1");

    /// <summary>
    /// Creates, persists, and returns a new empty <see cref="WorkingContextEntity"/> with
    /// the given name. An audit entry is written in the same transaction.
    /// </summary>
    public async Task<WorkingContextEntity> CreateAsync(string name)
    {
        var now = DateTimeOffset.UtcNow;

        var context = new WorkingContextEntity
        {
            Name = name,
            Summary = string.Empty,
            CreatedUtc = now,
            LastModifiedUtc = now,
        };

        await SaveAsync(context);

        return context;
    }

    /// <summary>
    /// Removes a fragment from a context's junction table
    /// </summary>
    public async Task RemoveFragmentAsync(
        long contextId,
        long fragmentId,
        IDbTransaction? transaction = null) =>
        await ExecuteAsync(
            $"""
            DELETE FROM WorkingContextFragments
            WHERE WorkingContextId = {contextId} AND ContextFragmentId = {fragmentId}
            """,
            transaction);

    #endregion

    #region Base overrides

    /// <summary>
    /// Saves the context's fragments and junction table entries. New or modified
    /// fragments are persisted via the fragment repository, and junction rows are
    /// upserted. Transient fragment types (ActionResponse, ScratchPad) are
    /// skipped — they exist only in the in-memory context and are never written to
    /// the database.
    /// </summary>
    protected override async Task SaveSubEntitiesAsync(
        WorkingContextEntity entity, IDbTransaction transaction, CancellationToken ct = default)
    {
        foreach (var (order, fragment) in entity.ContextFragments)
        {
            if (IsTransientType(fragment.FragmentType))
            {
                continue;
            }

            // Save the fragment itself (insert or update) — the fragment repo's
            // change tracking handles skipping unmodified entities
            await fragmentRepo.SaveAsync(fragment, transaction, ct);

            // Upsert the junction row (weight and order may have changed)
            await ExecuteAsync(
                $"""
                INSERT INTO WorkingContextFragments (WorkingContextId, ContextFragmentId, Weight, "Order")
                VALUES ({entity.Id}, {fragment.Id}, {fragment.Weight}, {fragment.Order})
                ON CONFLICT(WorkingContextId, ContextFragmentId) DO UPDATE SET
                    Weight = {fragment.Weight},
                    "Order" = {fragment.Order}
                """,
                transaction,
                ct);
        }
    }

    /// <summary>
    /// Loads working contexts by ID with their fragments, sources, and tags fully populated
    /// </summary>
    protected override async Task<IEnumerable<WorkingContextEntity>> LoadByIdsAsync(
        IEnumerable<long> ids, IDbConnection connection, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        var contextMap = new Dictionary<long, WorkingContextEntity>();

        // 1. Contexts with their fragments (including junction Weight/Order)
        await connection.QueryAsync<WorkingContextEntity, WeightedContextFragment, WorkingContextEntity>(
            """
            SELECT wc.*, cf.*, wcf.Weight, wcf."Order"
            FROM WorkingContexts wc
            LEFT JOIN WorkingContextFragments wcf ON wc.Id = wcf.WorkingContextId
            LEFT JOIN ContextFragments cf ON wcf.ContextFragmentId = cf.Id
            WHERE wc.Id IN @ids
            ORDER BY wcf."Order"
            """,
            (context, fragment) =>
            {
                if (!contextMap.TryGetValue(context.Id, out var existing))
                {
                    existing = context;
                    contextMap[context.Id] = existing;
                }

                if (fragment?.Id > 0)
                {
                    existing.ContextFragments[fragment.Order] = fragment;
                }

                return existing;
            },
            new { ids = idList },
            splitOn: "Id");

        var contexts = contextMap.Values.ToList();

        var fragmentIds = contexts
            .SelectMany(c => c.ContextFragments.Values)
            .Select(f => f.Id)
            .Distinct()
            .ToList();

        if (fragmentIds.Count == 0)
        {
            return contexts;
        }

        // 2. Sources for all fragments
        var sourceRows = await connection.QueryAsync<long, SourceEntity, (long FragmentId, SourceEntity Source)>(
            """
            SELECT cfs.ContextFragmentId, s.*
            FROM ContextFragmentSources cfs
            JOIN Sources s ON cfs.SourceId = s.Id
            WHERE cfs.ContextFragmentId IN @ids
            """,
            (fragmentId, source) => (fragmentId, source),
            new { ids = fragmentIds },
            splitOn: "Id");

        var sourcesByFragment = sourceRows
            .GroupBy(x => x.FragmentId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Source).ToList());

        // 3. Tags for all fragments
        var tagRows = await connection.QueryAsync<long, TagEntity, (long FragmentId, TagEntity Tag)>(
            """
            SELECT cft.ContextFragmentId, t.*
            FROM ContextFragmentTags cft
            JOIN Tags t ON cft.TagId = t.Id
            WHERE cft.ContextFragmentId IN @ids
            """,
            (fragmentId, tag) => (fragmentId, tag),
            new { ids = fragmentIds },
            splitOn: "Id");

        var tagsByFragment = tagRows
            .GroupBy(x => x.FragmentId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Tag).ToList());

        // Assign sources and tags to each fragment
        foreach (var context in contexts)
        {
            foreach (var fragment in context.ContextFragments.Values)
            {
                fragment.Sources = sourcesByFragment.GetValueOrDefault(fragment.Id, []);
                fragment.Tags = tagsByFragment.GetValueOrDefault(fragment.Id, []);
            }
        }

        return contexts;
    }

    /// <summary>
    /// Returns the INSERT statement for a working context
    /// </summary>
    protected override FormattableString GetInsertSql(WorkingContextEntity entity) =>
        $"""
        INSERT INTO WorkingContexts (Name, Summary, LastAccessedUtc, IsDeleted, CreatedUtc, LastModifiedUtc, Notes)
        VALUES ({entity.Name}, {entity.Summary}, {entity.LastAccessedUtc}, {entity.IsDeleted}, {entity.CreatedUtc}, {entity.LastModifiedUtc}, {entity.Notes})
        """;

    /// <summary>
    /// Returns the UPDATE statement for a working context
    /// </summary>
    protected override FormattableString GetUpdateSql(WorkingContextEntity entity) =>
        $"""
        UPDATE WorkingContexts
        SET Name = {entity.Name}, Summary = {entity.Summary}, LastAccessedUtc = {entity.LastAccessedUtc},
            IsDeleted = {entity.IsDeleted}, LastModifiedUtc = {entity.LastModifiedUtc}, Notes = {entity.Notes}
        WHERE Id = {entity.Id}
        """;

    /// <summary>
    /// Tracks fragments hydrated with the context so they're recognised as existing rows.
    /// Without this they'd keep the constructor default <c>IsNew = true</c> and be
    /// re-inserted (duplicated) on every save.
    /// </summary>
    protected override void TrackSubEntities(WorkingContextEntity entity)
    {
        foreach (var fragment in entity.ContextFragments.Values)
        {
            Track(fragment);
        }
    }

    #endregion

    #region Private

    /// <summary>
    /// Returns true for fragment types that exist only in the in-memory context
    /// and should never be written to the database
    /// </summary>
    private static bool IsTransientType(ContextFragmentType type) => type is
        ContextFragmentType.ActionResponse or
        ContextFragmentType.ScratchPad;

    #endregion
}
