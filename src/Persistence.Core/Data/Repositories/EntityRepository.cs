using Dapper;
using Dapper.Contrib.Extensions;
using InterpolatedSql.Dapper;
using Microsoft.Data.Sqlite;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Extensions;
using Persistence.Runtime;
using System.Data;
using System.Reflection;
using System.Text.Json;

namespace Persistence.Data.Repositories;

/// <summary>
/// Base repository for all entities. Manages connections, change tracking,
/// save routing (insert vs update), and audit logging.
/// </summary>
public abstract class EntityRepository<T> : IEntityRepository<T> where T : BaseEntity
{
    private readonly string connectionString;
    private readonly ISessionContext sessionContext;

    /// <summary>
    /// Constructor
    /// </summary>
    protected EntityRepository(IAppConfig config, ISessionContext sessionContext)
    {
        connectionString = SqliteConnectionString.For(config.DatabasePath);
        this.sessionContext = sessionContext;
    }

    /// <summary>
    /// The table name for <typeparamref name="T"/>, resolved from its <see cref="TableAttribute"/>
    /// </summary>
    protected string TableName
    {
        get
        {
            if (field.HasValue())
            {
                return field;
            }

            field = typeof(T).GetCustomAttribute<TableAttribute>()?.Name ?? throw new Exception("Entities must have a TableAttribute");

            return field;
        }
    }

    #region Public CRUD

    /// <summary>
    /// Fetches the entity with the given ID, or null if not found
    /// </summary>
    public async Task<T?> GetByIdAsync(long id, CancellationToken ct = default) =>
        (await GetByIdsAsync([id], ct)).FirstOrDefault();

    /// <summary>
    /// Fetches entities with the given IDs
    /// </summary>
    public async Task<IEnumerable<T>> GetByIdsAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync();

        var entities = await LoadByIdsAsync(ids, connection, ct);

        await TouchLastAccessedAsync(entities, connection);
        SetupTracking(entities);

        return entities;
    }

    /// <summary>
    /// Save the given entity
    /// </summary>
    public async Task SaveAsync(T entity, IDbTransaction? transaction = null, CancellationToken ct = default) =>
        await SaveAsync([entity], transaction, ct);

    /// <summary>
    /// Save the given entities
    /// </summary>
    public async Task SaveAsync(IEnumerable<T> entities, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        if (transaction?.Connection != null)
        {
            await SaveInternalAsync(entities, transaction, ct);
            return;
        }

        await using var connection = await OpenConnectionAsync();
        await using var newTransaction = connection.BeginTransaction();

        await SaveInternalAsync(entities, newTransaction, ct);
        await newTransaction.CommitAsync(ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> inside a single transaction on a fresh connection, committing on
    /// success and rolling back (via dispose) on exception. The open transaction is passed to the
    /// callback so it can be threaded into <see cref="SaveAsync"/> on this or other repositories,
    /// keeping connection ownership in the repository layer.
    /// </summary>
    public async Task<TResult> RunInTransactionAsync<TResult>(
        Func<IDbTransaction, Task<TResult>> work, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var result = await work(transaction);

        await transaction.CommitAsync(ct);
        return result;
    }

    #endregion

    #region Abstract — every repo must provide its own SQL

    /// <summary>
    /// Returns the INSERT statement for the entity. Do not include a trailing
    /// semicolon — the base appends <c>SELECT last_insert_rowid()</c> automatically.
    /// </summary>
    protected abstract FormattableString GetInsertSql(T entity);

    /// <summary>
    /// Returns the UPDATE statement for the entity
    /// </summary>
    protected abstract FormattableString GetUpdateSql(T entity);

    #endregion

    #region Virtual — override for entities with children

    /// <summary>
    /// Loads entities by ID. Default does a plain SELECT.
    /// Override to do multi-mapping JOINs for entities with sub-entities.
    /// </summary>
    protected virtual async Task<IEnumerable<T>> LoadByIdsAsync(
        IEnumerable<long> ids, IDbConnection connection, CancellationToken ct = default) =>
        await connection.SqlBuilder($"SELECT * FROM {TableName:raw} WHERE Id IN {ids.ToList()}")
            .QueryAsync<T>(cancellationToken: ct);

    /// <summary>
    /// Saves sub-entities after the parent has been inserted or updated.
    /// Default is a no-op.
    /// </summary>
    protected virtual Task SaveSubEntitiesAsync(T entity, IDbTransaction transaction, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Tracks sub-entities loaded alongside the parent. Default is a no-op; repositories
    /// that hydrate children (e.g. a working context's fragments) override this so the
    /// children are recognised as existing rows rather than new inserts.
    /// </summary>
    protected virtual void TrackSubEntities(T entity) { }

    /// <summary>
    /// Whether reads should stamp <see cref="BaseEntity.LastAccessedUtc"/>. Default true.
    /// Override to false for append-only/immutable tables whose schema has no such column
    /// (e.g. audit logs), so querying them doesn't attempt an UPDATE on a missing column.
    /// </summary>
    protected virtual bool TracksLastAccessed => true;

    #endregion

    #region Protected helpers

    /// <summary>
    /// Executes a query and returns tracked entities.
    /// Hydration goes through <see cref="LoadByIdsAsync"/> so entities with
    /// children are fully populated.
    /// </summary>
    protected async Task<IEnumerable<T>> QueryAsync(FormattableString sql, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync();

        var flat = await connection.SqlBuilder(sql).QueryAsync<T>(cancellationToken: ct);
        var ids = flat.Select(e => e.Id).ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var entities = await LoadByIdsAsync(ids, connection, ct);

        await TouchLastAccessedAsync(entities, connection);
        SetupTracking(entities);

        return entities;
    }

    /// <summary>
    /// Executes a non-entity query and returns the results
    /// </summary>
    protected async Task<IEnumerable<Q>> QueryAsync<Q>(FormattableString sql, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync();

        return await connection.SqlBuilder(sql).QueryAsync<Q>(cancellationToken: ct);
    }

    /// <summary>
    /// Executes a query and returns the first tracked entity, or null.
    /// Hydration goes through <see cref="LoadByIdsAsync"/> so entities with
    /// children are fully populated.
    /// </summary>
    protected async Task<T?> QueryFirstOrDefaultAsync(FormattableString sql, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync();

        var entity = await connection.SqlBuilder(sql).QueryFirstOrDefaultAsync<T>(cancellationToken: ct);

        if (entity == null)
        {
            return null;
        }

        var hydrated = await LoadByIdsAsync([entity.Id], connection, ct);

        await TouchLastAccessedAsync(hydrated, connection);
        SetupTracking(hydrated);

        return hydrated.FirstOrDefault();
    }

    /// <summary>
    /// Executes a non-query SQL statement. Returns the number of rows affected.
    /// </summary>
    protected async Task<int> ExecuteAsync(FormattableString sql, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        if (transaction?.Connection != null)
        {
            return await transaction.Connection.SqlBuilder(sql).ExecuteAsync(transaction, cancellationToken: ct);
        }

        await using var connection = await OpenConnectionAsync();
        return await connection.SqlBuilder(sql).ExecuteAsync(cancellationToken: ct);
    }

    /// <summary>
    /// Executes a scalar query, returning the first column of the first row
    /// </summary>
    protected async Task<Q?> ExecuteScalarAsync<Q>(FormattableString sql, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        if (transaction?.Connection != null)
        {
            return await transaction.Connection.SqlBuilder(sql).ExecuteScalarAsync<Q>(transaction, cancellationToken: ct);
        }

        await using var connection = await OpenConnectionAsync();
        return await connection.SqlBuilder(sql).ExecuteScalarAsync<Q>(cancellationToken: ct);
    }

    /// <summary>
    /// Marks a single entity as existing (not new) and snapshots its state so subsequent
    /// saves can detect whether it actually changed. Without this, an entity hydrated from
    /// the database keeps the constructor default <see cref="BaseEntity.IsNew"/> = true and
    /// would be re-inserted on the next save.
    /// </summary>
    /// <remarks>
    /// Generic on the concrete type so the snapshot is serialized with the <em>same</em> contract
    /// the change-detection in <see cref="SaveInternalAsync"/> uses (<c>Serialize(entity)</c> where
    /// <c>entity</c> is statically <typeparamref name="TEntity"/>). <see cref="JsonSerializer"/>
    /// picks its contract from the compile-time type, not the runtime type: snapshotting as the base
    /// <see cref="BaseEntity"/> would capture only the base fields, so the comparison against the
    /// full-shape serialization would never match and a genuinely-unchanged entity would be
    /// re-saved and re-audited on every save. Sub-entities tracked by a parent repository must pass
    /// the type their own repository saves them as (see
    /// <see cref="WorkingContextRepository.TrackSubEntities"/>).
    /// </remarks>
    protected static void Track<TEntity>(TEntity entity) where TEntity : BaseEntity
    {
        entity.IsNew = false;
        entity.OriginalState = JsonSerializer.Serialize(entity);
    }

    #endregion

    #region Private

    /// <summary>
    /// Saves entities within an existing transaction. Inserts new entities, updates
    /// modified ones (detected via JSON change tracking), and always calls
    /// <see cref="SaveSubEntitiesAsync"/> regardless of whether the parent changed —
    /// sub-entities can change independently of their parent's scalar fields.
    /// </summary>
    private async Task SaveInternalAsync(IEnumerable<T> entities, IDbTransaction transaction, CancellationToken ct = default)
    {
        foreach (var entity in entities)
        {
            var isUnchanged = !entity.IsNew
                && entity.OriginalState != null
                && JsonSerializer.Serialize(entity) == entity.OriginalState;

            if (!isUnchanged)
            {
                entity.LastModifiedUtc = DateTimeOffset.UtcNow;

                var isNew = entity.IsNew;

                if (isNew)
                {
                    entity.Id = await transaction.Connection!
                        .SqlBuilder($"{GetInsertSql(entity)}; SELECT last_insert_rowid()")
                        .ExecuteScalarAsync<long>(transaction, cancellationToken: ct);

                    entity.IsNew = false;
                }
                else
                {
                    await transaction.Connection!
                        .SqlBuilder(GetUpdateSql(entity))
                        .ExecuteAsync(transaction, cancellationToken: ct);
                }

                await WriteAuditAsync(isNew ? AuditEventType.Created : AuditEventType.Modified, entity, transaction, ct: ct);

                // Re-snapshot after a successful write so a second save within the same
                // unit of work correctly sees the entity as unchanged. Must run after the
                // audit, which relies on the pre-save OriginalState as its OldData.
                Track(entity);
            }

            // Always save sub-entities — they can change independently of the
            // parent's scalar fields (e.g. fragments added to a working context
            // without modifying the context's own Name/Summary)
            await SaveSubEntitiesAsync(entity, transaction, ct);
        }
    }

    /// <summary>
    /// Updates LastAccessedUtc on the entity objects and in the database directly,
    /// bypassing save/audit. Must be called before SetupTracking so the snapshot
    /// reflects the updated timestamp.
    /// </summary>
    private async Task TouchLastAccessedAsync(IEnumerable<T> entities, IDbConnection connection)
    {
        if (!TracksLastAccessed)
        {
            return;
        }

        var entityList = entities.ToList();

        if (entityList.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var entity in entityList)
        {
            entity.LastAccessedUtc = now;
        }

        var ids = entityList.Select(e => e.Id).ToList();

        await connection.SqlBuilder(
            $"UPDATE {TableName:raw} SET LastAccessedUtc = {now} WHERE Id IN {ids}")
            .ExecuteAsync();
    }

    /// <summary>
    /// Marks entities (and any tracked sub-entities) as not new and snapshots their
    /// current state for change detection.
    /// </summary>
    private void SetupTracking(IEnumerable<T> entities)
    {
        foreach (var entity in entities)
        {
            Track(entity);
            TrackSubEntities(entity);
        }
    }

    /// <summary>
    /// Writes an audit log entry for a create or update operation. Skips silently
    /// during early bootstrap when the System source hasn't been created yet —
    /// those seed operations don't need audit trails.
    /// </summary>
    private async Task WriteAuditAsync(
        AuditEventType eventType,
        T entity,
        IDbTransaction transaction,
        long? sourceId = null,
        CancellationToken ct = default)
    {
        var resolvedSourceId = sourceId ?? sessionContext.SystemSourceId;

        // During bootstrap the System source doesn't exist yet — skip audit
        if (resolvedSourceId == 0)
        {
            return;
        }

        // WorkingContextId is nullable in the schema — pass null during bootstrap
        // when no context has been loaded yet (Id is still 0)
        long? workingContextId = sessionContext.WorkingContextId == 0
            ? null
            : sessionContext.WorkingContextId;

        await transaction.Connection!.SqlBuilder($"""
            INSERT INTO AuditLogs (SessionId, WorkingContextId, EventType, TargetType, TargetId, SourceId, OldData, NewData, CreatedUtc)
            VALUES (
                {sessionContext.SessionId},
                {workingContextId},
                {eventType},
                {typeof(T).Name},
                {entity.Id},
                {resolvedSourceId},
                {(eventType == AuditEventType.Created ? null : entity.OriginalState)},
                {JsonSerializer.Serialize(entity)},
                {DateTimeOffset.UtcNow})
            """).ExecuteAsync(transaction, cancellationToken: ct);
    }

    /// <summary>
    /// Opens and returns a new SQLite connection using the configured connection string
    /// </summary>
    private Task<SqliteConnection> OpenConnectionAsync() =>
        SqliteConnectionString.OpenAsync(connectionString);

    #endregion
}
