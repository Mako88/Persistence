using Persistence.Data.Entities;
using System.Data;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for <see cref="ScheduledEventEntity"/>. Loaded events have their
/// <see cref="ScheduledEventEntity.Tags"/> collection populated.
/// </summary>
public interface IScheduledEventRepository : IEntityRepository<ScheduledEventEntity>
{
    /// <summary>
    /// Returns all pending, non-deleted events whose <c>ScheduledForUtc</c> is at or before
    /// the current UTC time
    /// </summary>
    Task<IEnumerable<ScheduledEventEntity>> GetDueEventsAsync();

    /// <summary>
    /// Returns all non-deleted events associated with the given working context,
    /// regardless of status
    /// </summary>
    Task<IEnumerable<ScheduledEventEntity>> GetByWorkingContextAsync(long workingContextId);

    /// <summary>
    /// Sets <see cref="ScheduledEventEntity.Status"/> to
    /// <see cref="ScheduledEventStatus.Triggered"/>, stamps <c>TriggeredAtUtc</c>, and saves
    /// </summary>
    Task MarkTriggeredAsync(ScheduledEventEntity scheduledEvent, IDbTransaction? transaction = null, CancellationToken ct = default);

    /// <summary>
    /// Sets <see cref="ScheduledEventEntity.Status"/> to
    /// <see cref="ScheduledEventStatus.Cancelled"/> and saves
    /// </summary>
    Task CancelAsync(ScheduledEventEntity scheduledEvent, IDbTransaction? transaction = null, CancellationToken ct = default);
}
