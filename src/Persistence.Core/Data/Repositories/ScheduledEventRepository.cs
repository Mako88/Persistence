using Dapper;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Runtime;
using System.Data;

namespace Persistence.Data.Repositories;

/// <summary>
/// Repository for <see cref="ScheduledEventEntity"/>. All load paths fully populate
/// <see cref="ScheduledEventEntity.Tags"/>.
/// </summary>
[Singleton]
public class ScheduledEventRepository : EntityRepository<ScheduledEventEntity>, IScheduledEventRepository
{
    /// <summary>
    /// Constructor
    /// </summary>
    public ScheduledEventRepository(IAppConfig config, ISessionContext sessionContext)
        : base(config, sessionContext) { }

    // ── Public methods ───────────────────────────────────────────

    /// <summary>
    /// Returns all pending, non-deleted events whose <c>ScheduledForUtc</c> is at or before
    /// the current UTC time
    /// </summary>
    public async Task<IEnumerable<ScheduledEventEntity>> GetDueEventsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        return await QueryAsync(
            $"""
            SELECT * FROM ScheduledEvents
            WHERE ScheduledForUtc <= {now}
                AND Status = {ScheduledEventStatus.Pending}
                AND IsDeleted = 0
            """);
    }

    /// <summary>
    /// Returns all non-deleted events associated with the given working context,
    /// regardless of status
    /// </summary>
    public async Task<IEnumerable<ScheduledEventEntity>> GetByWorkingContextAsync(long workingContextId) =>
        await QueryAsync(
            $"SELECT * FROM ScheduledEvents WHERE WorkingContextId = {workingContextId} AND IsDeleted = 0");

    /// <summary>
    /// Sets status to <see cref="ScheduledEventStatus.Triggered"/>, stamps
    /// <c>TriggeredAtUtc</c>, and saves
    /// </summary>
    public async Task MarkTriggeredAsync(
        ScheduledEventEntity scheduledEvent,
        IDbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        scheduledEvent.Status = ScheduledEventStatus.Triggered;
        scheduledEvent.TriggeredAtUtc = DateTimeOffset.UtcNow;
        await SaveAsync(scheduledEvent, transaction, ct);
    }

    /// <summary>
    /// Sets status to <see cref="ScheduledEventStatus.Cancelled"/> and saves
    /// </summary>
    public async Task CancelAsync(
        ScheduledEventEntity scheduledEvent,
        IDbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        scheduledEvent.Status = ScheduledEventStatus.Cancelled;
        await SaveAsync(scheduledEvent, transaction, ct);
    }

    // ── Base overrides ───────────────────────────────────────────

    /// <summary>
    /// Loads scheduled events by ID with their tags populated
    /// </summary>
    protected override async Task<IEnumerable<ScheduledEventEntity>> LoadByIdsAsync(
        IEnumerable<long> ids, IDbConnection connection, CancellationToken ct = default)
    {
        var idList = ids.ToList();

        var events = (await connection.QueryAsync<ScheduledEventEntity>(
            "SELECT * FROM ScheduledEvents WHERE Id IN @ids",
            new { ids = idList })).ToList();

        if (events.Count == 0)
        {
            return events;
        }

        var eventIds = events.Select(e => e.Id).ToList();

        // Tags for all events
        var tagRows = await connection.QueryAsync<long, TagEntity, (long EventId, TagEntity Tag)>(
            """
            SELECT st.ScheduledEventId, t.*
            FROM ScheduledEventTags st
            JOIN Tags t ON st.TagId = t.Id
            WHERE st.ScheduledEventId IN @ids
            """,
            (eventId, tag) => (eventId, tag),
            new { ids = eventIds },
            splitOn: "Id");

        var tagsByEvent = tagRows
            .GroupBy(x => x.EventId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Tag).ToList());

        foreach (var evt in events)
        {
            evt.Tags = tagsByEvent.GetValueOrDefault(evt.Id, []);
        }

        return events;
    }

    /// <summary>
    /// Returns the INSERT statement for a scheduled event
    /// </summary>
    protected override FormattableString GetInsertSql(ScheduledEventEntity entity) =>
        $"""
        INSERT INTO ScheduledEvents (Name, WorkingContextId, ScheduledForUtc, TriggeredAtUtc, Status, IsDeleted, CreatedUtc, LastModifiedUtc, LastAccessedUtc, Notes)
        VALUES ({entity.Name}, {entity.WorkingContextId}, {entity.ScheduledForUtc}, {entity.TriggeredAtUtc}, {entity.Status}, {entity.IsDeleted}, {entity.CreatedUtc}, {entity.LastModifiedUtc}, {entity.LastAccessedUtc}, {entity.Notes})
        """;

    /// <summary>
    /// Returns the UPDATE statement for a scheduled event
    /// </summary>
    protected override FormattableString GetUpdateSql(ScheduledEventEntity entity) =>
        $"""
        UPDATE ScheduledEvents
        SET Name = {entity.Name}, WorkingContextId = {entity.WorkingContextId}, ScheduledForUtc = {entity.ScheduledForUtc},
            TriggeredAtUtc = {entity.TriggeredAtUtc}, Status = {entity.Status}, IsDeleted = {entity.IsDeleted},
            LastModifiedUtc = {entity.LastModifiedUtc}, LastAccessedUtc = {entity.LastAccessedUtc}, Notes = {entity.Notes}
        WHERE Id = {entity.Id}
        """;
}
