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
    /// <summary>
    /// Constructor
    /// </summary>
    public ContextFragmentRepository(IAppConfig config, ISessionContext sessionContext)
        : base(config, sessionContext) { }

    // ── Public methods ───────────────────────────────────────────

    /// <summary>
    /// Returns fragments of the given type. Defaults to active only.
    /// Pass <c>activeOnly: false</c> to include all statuses.
    /// </summary>
    public async Task<IEnumerable<ContextFragmentEntity>> GetByTypeAsync(ContextFragmentType type, bool activeOnly = true) =>
        activeOnly
            ? await QueryAsync(
                $"""
                SELECT * FROM ContextFragments
                WHERE FragmentType = {type} AND Status = {ContextFragmentStatus.Active}
                """)
            : await QueryAsync(
                $"SELECT * FROM ContextFragments WHERE FragmentType = {type}");

    /// <summary>
    /// Returns all fragments tagged with the given tag ID, regardless of status
    /// </summary>
    public async Task<IEnumerable<ContextFragmentEntity>> GetByTagAsync(long tagId) =>
        await QueryAsync(
            $"""
            SELECT cf.*
            FROM ContextFragments cf
            JOIN ContextFragmentTags cft ON cf.Id = cft.ContextFragmentId
            WHERE cft.TagId = {tagId}
            """);

    /// <summary>
    /// Returns all active <see cref="ContextFragmentType.System"/> fragments. Used by the
    /// startup sequence to seed a new <see cref="WorkingContextEntity"/> with the system
    /// fragment set.
    /// </summary>
    public async Task<IEnumerable<ContextFragmentEntity>> GetSystemFragmentsAsync() =>
        await QueryAsync(
            $"""
            SELECT * FROM ContextFragments
            WHERE FragmentType = {ContextFragmentType.System} AND Status = {ContextFragmentStatus.Active}
            """);

    // ── Base overrides ───────────────────────────────────────────

    /// <summary>
    /// Loads fragments by ID with their sources and tags populated
    /// </summary>
    protected override async Task<IEnumerable<ContextFragmentEntity>> LoadByIdsAsync(
        IEnumerable<long> ids, IDbConnection connection, CancellationToken ct = default)
    {
        var idList = ids.ToList();

        var fragments = (await connection.QueryAsync<ContextFragmentEntity>(
            "SELECT * FROM ContextFragments WHERE Id IN @ids",
            new { ids = idList })).ToList();

        if (fragments.Count == 0)
        {
            return fragments;
        }

        var fragmentIds = fragments.Select(f => f.Id).ToList();

        // Sources for all fragments
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

        // Tags for all fragments
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

        foreach (var fragment in fragments)
        {
            fragment.Sources = sourcesByFragment.GetValueOrDefault(fragment.Id, []);
            fragment.Tags = tagsByFragment.GetValueOrDefault(fragment.Id, []);
        }

        return fragments;
    }

    /// <summary>
    /// Returns the INSERT statement for a context fragment
    /// </summary>
    protected override FormattableString GetInsertSql(ContextFragmentEntity entity) =>
        $"""
        INSERT INTO ContextFragments (FragmentType, Status, Content, Summary, LastAccessedUtc, Importance, Confidence, IsProtected, IsDeleted, CreatedUtc, LastModifiedUtc, Notes)
        VALUES ({entity.FragmentType}, {entity.Status}, {entity.Content}, {entity.Summary}, {entity.LastAccessedUtc}, {entity.Importance}, {entity.Confidence}, {entity.IsProtected}, {entity.IsDeleted}, {entity.CreatedUtc}, {entity.LastModifiedUtc}, {entity.Notes})
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
            LastModifiedUtc = {entity.LastModifiedUtc}, Notes = {entity.Notes}
        WHERE Id = {entity.Id}
        """;
}
