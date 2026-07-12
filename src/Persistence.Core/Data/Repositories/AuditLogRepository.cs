using Dapper;
using InterpolatedSql.Dapper;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Runtime;
using System.Data;

namespace Persistence.Data.Repositories;

/// <summary>
/// Read-only repository for <see cref="AuditLogEntity"/>. Audit entries are written
/// automatically by <see cref="EntityRepository{T}"/> — this repository exists
/// solely for querying the audit trail. Loaded entries have their
/// <see cref="AuditLogEntity.Source"/> property populated.
/// </summary>
[Singleton]
public class AuditLogRepository : EntityRepository<AuditLogEntity>, IAuditLogRepository
{
    /// <summary>
    /// Constructor
    /// </summary>
    public AuditLogRepository(IAppConfig config, ISessionContext sessionContext)
        : base(config, sessionContext) { }

    /// <summary>
    /// Returns all audit entries for the given target entity type and ID
    /// </summary>
    public async Task<IEnumerable<AuditLogEntity>> GetByTargetAsync(string targetType, long targetId) =>
        await QueryAsync(
            $"""
            SELECT * FROM AuditLogs
            WHERE TargetType = {targetType} AND TargetId = {targetId}
            ORDER BY CreatedUtc ASC
            """);

    /// <summary>
    /// Returns all audit entries recorded during the given session
    /// </summary>
    public async Task<IEnumerable<AuditLogEntity>> GetBySessionAsync(string sessionId) =>
        await QueryAsync(
            $"SELECT * FROM AuditLogs WHERE SessionId = {sessionId} ORDER BY CreatedUtc ASC");

    /// <summary>
    /// Returns the most recent self-changes, newest first. Excludes ChatMessage, System, and Thought
    /// fragment changes — conversation/scaffolding and the peer's own auto-persisted reasoning, none of
    /// which is the peer deliberately curating itself (thoughts are read directly as fragments, and a
    /// verbose thinker would otherwise crowd the digest out every turn). Joins to the fragment row for
    /// fragment-typed targets. Uses the projection overload so order is preserved and Source isn't
    /// hydrated (the digest doesn't need it).
    /// </summary>
    public async Task<IReadOnlyList<AuditLogEntity>> GetRecentSelfChangesAsync(int limit, CancellationToken ct = default) =>
        // FragmentType is stored via the same interpolation path, so the excluded types must be
        // passed as parameters (not hardcoded string literals) to match the stored representation.
        (await QueryAsync<AuditLogEntity>(
            $"""
            SELECT * FROM AuditLogs a
            WHERE a.TargetType <> 'ContextFragmentEntity'
               OR a.TargetId IN (
                    SELECT cf.Id FROM ContextFragments cf
                    WHERE cf.FragmentType NOT IN ({ContextFragmentType.ChatMessage}, {ContextFragmentType.System}, {ContextFragmentType.Thought}, {ContextFragmentType.WorkingNote})
               )
            ORDER BY a.CreatedUtc DESC
            LIMIT {limit}
            """,
            ct)).ToList();

    #region Base overrides

    /// <summary>
    /// Audit logs are append-only and their table has no LastAccessedUtc column, so reads
    /// must not attempt to stamp it.
    /// </summary>
    protected override bool TracksLastAccessed => false;

    /// <summary>
    /// Loads audit log entries by ID with their Source property populated
    /// </summary>
    protected override async Task<IEnumerable<AuditLogEntity>> LoadByIdsAsync(
        IEnumerable<long> ids, IDbConnection connection, CancellationToken ct = default)
    {
        var idList = ids.ToList();

        var entries = (await connection.SqlBuilder(
            $"SELECT * FROM AuditLogs WHERE Id IN {idList}")
            .QueryAsync<AuditLogEntity>(cancellationToken: ct)).ToList();

        if (entries.Count == 0)
        {
            return entries;
        }

        var sourceIds = entries.Select(e => e.SourceId).Distinct().ToList();

        var sources = (await connection.SqlBuilder(
            $"SELECT * FROM Sources WHERE Id IN {sourceIds}")
            .QueryAsync<SourceEntity>(cancellationToken: ct)).ToDictionary(s => s.Id);

        foreach (var entry in entries)
        {
            entry.Source = sources.GetValueOrDefault(entry.SourceId);
        }

        return entries;
    }

    /// <summary>
    /// Returns the INSERT statement for an audit log entry. The AuditLogs table is append-only
    /// and minimal — it has no LastAccessedUtc / LastModifiedUtc / IsDeleted columns.
    /// </summary>
    protected override FormattableString GetInsertSql(AuditLogEntity entity) =>
        $"""
        INSERT INTO AuditLogs (SessionId, WorkingContextId, EventType, TargetType, TargetId, SourceId, OldData, NewData, CreatedUtc)
        VALUES ({entity.SessionId}, {entity.WorkingContextId}, {entity.EventType}, {entity.TargetType}, {entity.TargetId}, {entity.SourceId}, {entity.OldData}, {entity.NewData}, {entity.CreatedUtc})
        """;

    /// <summary>
    /// Audit entries are immutable; updates are not supported.
    /// </summary>
    protected override FormattableString GetUpdateSql(AuditLogEntity entity) =>
        throw new NotSupportedException("Audit log entries are append-only and cannot be updated.");

    #endregion
}
