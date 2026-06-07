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
    public Task CreateSystemSourceIfNotExists() =>
        EnsureSourceAsync(SourceType.System, "System", id => sessionContext.SystemSourceId = id);

    /// <summary>
    /// Creates a LocalPeer source if none exists and stores its ID in the session context
    /// </summary>
    public Task CreateLocalPeerSourceIfNotExists() =>
        EnsureSourceAsync(SourceType.LocalPeer, "Local Peer", id => sessionContext.LocalPeerSourceId = id);

    /// <summary>
    /// Creates a RemotePeer source if none exists and stores its ID in the session context
    /// </summary>
    public Task CreateRemotePeerSourceIfNotExists() =>
        EnsureSourceAsync(SourceType.RemotePeer, "Remote Peer", id => sessionContext.RemotePeerSourceId = id);

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

    #region Base overrides

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

    #endregion

    #region Private

    /// <summary>
    /// Ensures a single source of the given type exists (creating it if absent) and reports its id
    /// to the caller — the shared core of the System/LocalPeer/RemotePeer seeding methods.
    /// </summary>
    private async Task EnsureSourceAsync(SourceType type, string name, Action<long> setId)
    {
        var sourceId = await ExecuteScalarAsync<long?>(
            $"SELECT Id FROM Sources WHERE SourceType = {type} LIMIT 1");

        if (sourceId == null)
        {
            var now = DateTimeOffset.UtcNow;

            var source = new SourceEntity
            {
                SourceType = type,
                Name = name,
                CreatedUtc = now,
                LastModifiedUtc = now,
            };

            await SaveAsync(source);
            sourceId = source.Id;
        }

        setId(sourceId.Value);
    }

    #endregion
}
