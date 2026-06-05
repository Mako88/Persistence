using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Runtime;
using System.Data;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for <see cref="ActionLogEntity"/>.
/// </summary>
[Singleton]
public class ActionLogRepository : EntityRepository<ActionLogEntity>, IActionLogRepository
{
    private readonly ISessionContext sessionContext;

    /// <summary>
    /// Constructor
    /// </summary>
    public ActionLogRepository(IAppConfig config, ISessionContext sessionContext)
        : base(config, sessionContext)
    {
        this.sessionContext = sessionContext;
    }

    /// <summary>
    /// Creates and persists a new action log entry for the current session
    /// </summary>
    public async Task LogAsync(
        string actionType,
        string? payload = null,
        string? result = null,
        IDbTransaction? transaction = null)
    {
        var entry = new ActionLogEntity
        {
            SessionId = sessionContext.SessionId,
            WorkingContextId = sessionContext.WorkingContextId,
            ActionType = actionType,
            Payload = payload,
            Result = result,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

        await SaveAsync(entry, transaction);
    }

    /// <summary>
    /// Returns all action log entries recorded during the given session
    /// </summary>
    public async Task<IEnumerable<ActionLogEntity>> GetBySessionAsync(string sessionId) =>
        await QueryAsync(
            $"SELECT * FROM ActionLogs WHERE SessionId = {sessionId} ORDER BY CreatedUtc ASC");

    /// <summary>
    /// Returns all action log entries for the given working context
    /// </summary>
    public async Task<IEnumerable<ActionLogEntity>> GetByWorkingContextAsync(long workingContextId) =>
        await QueryAsync(
            $"SELECT * FROM ActionLogs WHERE WorkingContextId = {workingContextId} ORDER BY CreatedUtc ASC");

    // ── Base overrides ───────────────────────────────────────────

    /// <summary>
    /// Returns the INSERT statement for an action log entry
    /// </summary>
    protected override FormattableString GetInsertSql(ActionLogEntity entity) =>
        $"""
        INSERT INTO ActionLogs (SessionId, WorkingContextId, ActionType, Payload, Result, LastAccessedUtc, IsDeleted, CreatedUtc, LastModifiedUtc, Notes)
        VALUES ({entity.SessionId}, {entity.WorkingContextId}, {entity.ActionType}, {entity.Payload}, {entity.Result}, {entity.LastAccessedUtc}, {entity.IsDeleted}, {entity.CreatedUtc}, {entity.LastModifiedUtc}, {entity.Notes})
        """;

    /// <summary>
    /// Returns the UPDATE statement for an action log entry
    /// </summary>
    protected override FormattableString GetUpdateSql(ActionLogEntity entity) =>
        $"""
        UPDATE ActionLogs
        SET ActionType = {entity.ActionType}, Payload = {entity.Payload}, Result = {entity.Result},
            LastAccessedUtc = {entity.LastAccessedUtc}, IsDeleted = {entity.IsDeleted},
            LastModifiedUtc = {entity.LastModifiedUtc}, Notes = {entity.Notes}
        WHERE Id = {entity.Id}
        """;
}
