using Dapper;
using Microsoft.Data.Sqlite;
using PatternContinuity.Models;

namespace PatternContinuity.Data;

public class ScheduledEventRepository
{
    private readonly SqliteConnection _db;
    public ScheduledEventRepository(SqliteConnection db) => _db = db;

    public string Insert(ScheduledEvent evt)
    {
        if (string.IsNullOrEmpty(evt.Id))
            evt.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(evt.CreatedAt))
            evt.CreatedAt = DateTime.UtcNow.ToString("o");

        _db.Execute("""
            INSERT INTO scheduled_events (id, session_id, event_type, scheduled_for,
                status, reason, created_at, fired_at, autonomous_depth)
            VALUES (@Id, @SessionId, @EventType, @ScheduledFor,
                @Status, @Reason, @CreatedAt, @FiredAt, @AutonomousDepth)
            """, evt);

        return evt.Id;
    }

    public ScheduledEvent? GetNextPending()
    {
        // Use explicit column aliases to ensure Dapper maps snake_case to PascalCase
        return _db.QueryFirstOrDefault<ScheduledEvent>("""
            SELECT id AS Id,
                   session_id AS SessionId,
                   event_type AS EventType,
                   scheduled_for AS ScheduledFor,
                   status AS Status,
                   reason AS Reason,
                   created_at AS CreatedAt,
                   fired_at AS FiredAt,
                   autonomous_depth AS AutonomousDepth
            FROM scheduled_events
            WHERE status = 'pending'
            ORDER BY scheduled_for ASC
            LIMIT 1
            """);
    }

    public bool HasPending()
    {
        return _db.QueryFirstOrDefault<int>(
            "SELECT COUNT(*) FROM scheduled_events WHERE status = 'pending'") > 0;
    }

    public void CancelAllPending()
    {
        _db.Execute("""
            UPDATE scheduled_events SET status = 'cancelled'
            WHERE status = 'pending'
            """);
    }

    public void MarkFired(string id)
    {
        _db.Execute("""
            UPDATE scheduled_events SET status = 'fired', fired_at = @now
            WHERE id = @id
            """, new { id, now = DateTime.UtcNow.ToString("o") });
    }

    public void MarkExpired(string id)
    {
        _db.Execute("""
            UPDATE scheduled_events SET status = 'expired'
            WHERE id = @id
            """, new { id });
    }
}
