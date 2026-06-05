using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Runtime;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for <see cref="SourceEntity"/>
/// </summary>
[Singleton]
public class SourceRepository : EntityRepository<SourceEntity>, ISourceRepository
{
    private readonly ISessionContext sessionContext;

    /// <summary>
    /// Constructor
    /// </summary>
    public SourceRepository(IAppConfig config, ISessionContext sessionContext)
        : base(config, sessionContext)
    {
        this.sessionContext = sessionContext;
    }

    /// <summary>
    /// Creates a System source if none exists and stores its ID in the session context
    /// </summary>
    public async Task CreateSystemSourceIfNotExists()
    {
        var systemSourceId = await ExecuteScalarAsync<long?>(
            $"SELECT Id FROM Sources WHERE SourceType = {SourceType.System} LIMIT 1");

        if (systemSourceId == null)
        {
            var now = DateTimeOffset.UtcNow;

            var source = new SourceEntity
            {
                SourceType = SourceType.System,
                Name = "System",
                CreatedUtc = now,
                LastModifiedUtc = now,
            };

            await SaveAsync(source);
            systemSourceId = source.Id;
        }

        sessionContext.SystemSourceId = systemSourceId.Value;
    }

    /// <summary>
    /// Creates a LocalPeer source if none exists and stores its ID in the session context
    /// </summary>
    public async Task CreateLocalPeerSourceIfNotExists()
    {
        var localPeerSourceId = await ExecuteScalarAsync<long?>(
            $"SELECT Id FROM Sources WHERE SourceType = {SourceType.LocalPeer} LIMIT 1");

        if (localPeerSourceId == null)
        {
            var now = DateTimeOffset.UtcNow;

            var source = new SourceEntity
            {
                SourceType = SourceType.LocalPeer,
                Name = "Local Peer",
                CreatedUtc = now,
                LastModifiedUtc = now,
            };

            await SaveAsync(source);
            localPeerSourceId = source.Id;
        }

        sessionContext.LocalPeerSourceId = localPeerSourceId.Value;
    }

    /// <summary>
    /// Creates a RemotePeer source if none exists and stores its ID in the session context
    /// </summary>
    public async Task CreateRemotePeerSourceIfNotExists()
    {
        var remotePeerSourceId = await ExecuteScalarAsync<long?>(
            $"SELECT Id FROM Sources WHERE SourceType = {SourceType.RemotePeer} LIMIT 1");

        if (remotePeerSourceId == null)
        {
            var now = DateTimeOffset.UtcNow;

            var source = new SourceEntity
            {
                SourceType = SourceType.RemotePeer,
                Name = "Remote Peer",
                CreatedUtc = now,
                LastModifiedUtc = now,
            };

            await SaveAsync(source);
            remotePeerSourceId = source.Id;
        }

        sessionContext.RemotePeerSourceId = remotePeerSourceId.Value;
    }

    /// <summary>
    /// Returns the source with the given name (case-insensitive), or null if not found
    /// </summary>
    public async Task<SourceEntity?> GetByNameAsync(string name, CancellationToken ct = default) =>
        await QueryFirstOrDefaultAsync($"SELECT * FROM Sources WHERE Name = {name} COLLATE NOCASE AND IsDeleted = 0", ct);

    /// <summary>
    /// Returns all non-deleted sources
    /// </summary>
    public async Task<IEnumerable<SourceEntity>> GetAllAsync(CancellationToken ct = default) =>
        await QueryAsync($"SELECT * FROM Sources WHERE IsDeleted = 0", ct);

    // ── Base overrides ───────────────────────────────────────────

    /// <summary>
    /// Returns the INSERT statement for a source entity
    /// </summary>
    protected override FormattableString GetInsertSql(SourceEntity entity) =>
        $"""
        INSERT INTO Sources (SourceType, Name, LastAccessedUtc, IsDeleted, CreatedUtc, LastModifiedUtc, Notes)
        VALUES ({entity.SourceType}, {entity.Name}, {entity.LastAccessedUtc}, {entity.IsDeleted}, {entity.CreatedUtc}, {entity.LastModifiedUtc}, {entity.Notes})
        """;

    /// <summary>
    /// Returns the UPDATE statement for a source entity
    /// </summary>
    protected override FormattableString GetUpdateSql(SourceEntity entity) =>
        $"""
        UPDATE Sources
        SET SourceType = {entity.SourceType}, Name = {entity.Name},
            LastAccessedUtc = {entity.LastAccessedUtc}, IsDeleted = {entity.IsDeleted},
            LastModifiedUtc = {entity.LastModifiedUtc}, Notes = {entity.Notes}
        WHERE Id = {entity.Id}
        """;
}
