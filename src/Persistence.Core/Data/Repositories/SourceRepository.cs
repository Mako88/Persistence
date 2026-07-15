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
    private readonly IAppConfig config;

    /// <summary>
    /// Constructor
    /// </summary>
    public SourceRepository(IAppConfig config, ISessionContext sessionContext)
        : base(config, sessionContext)
    {
        this.sessionContext = sessionContext;
        this.config = config;
    }

    /// <summary>
    /// Creates a System source if none exists and stores its ID in the session context
    /// </summary>
    public Task CreateSystemSourceIfNotExists() =>
        EnsureSourceAsync(SourceType.System, "System", id => sessionContext.SystemSourceId = id);

    /// <summary>
    /// Ensures the digital-peer source — the runtime's own voice — exists, is named after this peer, and
    /// has its id on the session. Human peers no longer have a single shared source (each is resolved by
    /// name per message, see <see cref="EnsureLocalPeerSourceAsync"/>), so only System and the digital
    /// peer are seeded here.
    ///
    /// <para><b>It also renames a store still carrying the old placeholder.</b> Before peers had names,
    /// this row was created literally named "Remote Peer" — so every message the peer ever sent reads
    /// back authored by "Remote Peer", however the client labels it live. Renaming here (rather than in a
    /// migration) is deliberate: the right name differs per store, which is precisely what a static SQL
    /// migration can't know, and this already runs at startup with the config to hand. It's one row —
    /// <c>Sources</c> is normalised, with <c>ContextFragmentSources</c> pointing many fragments at one
    /// source — so a single rename re-attributes the peer's whole history.</para>
    ///
    /// <para>Only the built-in placeholder is replaced. A source someone deliberately named (an import's
    /// provenance, say) is left alone: this heals what the system got wrong, it doesn't overwrite what a
    /// human decided.</para>
    /// </summary>
    public async Task CreateRemotePeerSourceIfNotExists()
    {
        var name = PeerIdentity.ResolveName(config);

        var existing = await QueryFirstOrDefaultAsync(
            $"SELECT * FROM Sources WHERE SourceType = {SourceType.DigitalPeer} LIMIT 1");

        if (existing is null)
        {
            var now = DateTimeOffset.UtcNow;
            var source = new SourceEntity
            {
                SourceType = SourceType.DigitalPeer,
                Name = name,
                CreatedUtc = now,
                LastModifiedUtc = now,
            };

            await SaveAsync(source);
            sessionContext.RemotePeerSourceId = source.Id;
            return;
        }

        if (string.Equals(existing.Name, PeerIdentity.LegacyDefaultName, StringComparison.Ordinal)
            && !string.Equals(name, PeerIdentity.LegacyDefaultName, StringComparison.Ordinal))
        {
            existing.Name = name;
            existing.LastModifiedUtc = DateTimeOffset.UtcNow;
            await SaveAsync(existing);
        }

        sessionContext.RemotePeerSourceId = existing.Id;
    }

    /// <summary>
    /// Returns the source with the given name (case-insensitive), or null if not found
    /// </summary>
    public async Task<SourceEntity?> GetByNameAsync(string name, CancellationToken ct = default) =>
        await QueryFirstOrDefaultAsync($"SELECT * FROM Sources WHERE Name = {name} COLLATE NOCASE", ct);

    /// <inheritdoc />
    public async Task<long> EnsureLocalPeerSourceAsync(string name, CancellationToken ct = default)
    {
        var id = await ExecuteScalarAsync<long?>(
            $"SELECT Id FROM Sources WHERE SourceType = {SourceType.HumanPeer} AND Name = {name} COLLATE NOCASE LIMIT 1");

        if (id == null)
        {
            var now = DateTimeOffset.UtcNow;
            var source = new SourceEntity
            {
                SourceType = SourceType.HumanPeer,
                Name = name,
                CreatedUtc = now,
                LastModifiedUtc = now,
            };

            await SaveAsync(source);
            id = source.Id;
        }

        return id.Value;
    }

    /// <summary>
    /// Returns all sources
    /// </summary>
    public async Task<IEnumerable<SourceEntity>> GetAllAsync(CancellationToken ct = default) =>
        await QueryAsync($"SELECT * FROM Sources", ct);

    #region Base overrides

    /// <summary>
    /// Returns the INSERT statement for a source entity
    /// </summary>
    protected override FormattableString GetInsertSql(SourceEntity entity) =>
        $"""
        INSERT INTO Sources (SourceType, Name, LastAccessedUtc, CreatedUtc, LastModifiedUtc, Notes)
        VALUES ({entity.SourceType}, {entity.Name}, {entity.LastAccessedUtc}, {entity.CreatedUtc}, {entity.LastModifiedUtc}, {entity.Notes})
        """;

    /// <summary>
    /// Returns the UPDATE statement for a source entity
    /// </summary>
    protected override FormattableString GetUpdateSql(SourceEntity entity) =>
        $"""
        UPDATE Sources
        SET SourceType = {entity.SourceType}, Name = {entity.Name},
            LastAccessedUtc = {entity.LastAccessedUtc},
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
