using Persistence.Data.Entities;
using System.Data;

namespace Persistence.Data.Repositories;

/// <summary>Repository for <see cref="ActionLogEntity"/>.</summary>
public interface IActionLogRepository : IEntityRepository<ActionLogEntity>
{
    /// <summary>
    /// Creates and persists a new action log entry for the current session and working
    /// context. Session and context identifiers are read from <c>ISessionContext</c>
    /// automatically.
    /// </summary>
    Task LogAsync(
        string actionType,
        string? payload = null,
        string? result = null,
        IDbTransaction? transaction = null);

    /// <summary>
    /// Returns all action log entries recorded during the given session, ordered by
    /// <c>CreatedUtc</c> ascending.
    /// </summary>
    Task<IEnumerable<ActionLogEntity>> GetBySessionAsync(string sessionId);

    /// <summary>
    /// Returns all action log entries associated with the given working context, ordered
    /// by <c>CreatedUtc</c> ascending.
    /// </summary>
    Task<IEnumerable<ActionLogEntity>> GetByWorkingContextAsync(long workingContextId);
}
