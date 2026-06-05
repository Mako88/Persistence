using Persistence.Data.Entities;

namespace Persistence.Data.Repositories;

/// <summary>
/// Read-only repository for <see cref="AuditLogEntity"/>. Audit entries are written
/// automatically by <see cref="EntityRepository{T}.SaveAsync"/> — this repository exists
/// solely for querying the audit trail.
/// </summary>
public interface IAuditLogRepository : IEntityRepository<AuditLogEntity>
{
    /// <summary>
    /// Returns all audit entries for the given target entity type and ID, ordered by
    /// <c>CreatedUtc</c> ascending.
    /// </summary>
    Task<IEnumerable<AuditLogEntity>> GetByTargetAsync(string targetType, long targetId);

    /// <summary>
    /// Returns all audit entries recorded during the given session, ordered by
    /// <c>CreatedUtc</c> ascending.
    /// </summary>
    Task<IEnumerable<AuditLogEntity>> GetBySessionAsync(string sessionId);
}
