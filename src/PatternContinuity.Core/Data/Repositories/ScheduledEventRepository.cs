using Persistence.DI;
using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for scheduled wake-up events
    /// </summary>
    [Singleton]
    public class ScheduledEventRepository : IScheduledEventRepository
    {
        private readonly IDatabaseConnection _db;

        /// <summary>
        /// Constructor
        /// </summary>
        public ScheduledEventRepository(IDatabaseConnection db)
        {
            _db = db;
        }

        /// <summary>
        /// Insert a new scheduled event and return its ID
        /// </summary>
        public async Task<string> InsertAsync(ScheduledEvent evt)
        {
            if (string.IsNullOrEmpty(evt.Id))
                evt.Id = Guid.NewGuid().ToString();
            if (string.IsNullOrEmpty(evt.CreatedAt))
                evt.CreatedAt = DateTime.UtcNow.ToString("o");

            await _db.ExecuteAsync("""
                INSERT INTO scheduled_events (id, session_id, event_type, scheduled_for,
                    status, reason, created_at, fired_at, autonomous_depth)
                VALUES (@Id, @SessionId, @EventType, @ScheduledFor,
                    @Status, @Reason, @CreatedAt, @FiredAt, @AutonomousDepth)
                """, evt);

            return evt.Id;
        }

        /// <summary>
        /// Get the next pending scheduled event ordered by scheduled time
        /// </summary>
        public async Task<ScheduledEvent?> GetNextPendingAsync()
        {
            return await _db.QueryFirstOrDefaultAsync<ScheduledEvent>("""
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

        /// <summary>
        /// Check whether any pending scheduled events exist
        /// </summary>
        public async Task<bool> HasPendingAsync()
        {
            var count = await _db.QueryFirstOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM scheduled_events WHERE status = 'pending'");
            return count > 0;
        }

        /// <summary>
        /// Cancel all pending scheduled events
        /// </summary>
        public async Task CancelAllPendingAsync()
        {
            await _db.ExecuteAsync("""
                UPDATE scheduled_events SET status = 'cancelled'
                WHERE status = 'pending'
                """);
        }

        /// <summary>
        /// Mark a scheduled event as fired
        /// </summary>
        public async Task MarkFiredAsync(string id)
        {
            await _db.ExecuteAsync("""
                UPDATE scheduled_events SET status = 'fired', fired_at = @now
                WHERE id = @id
                """, new { id, now = DateTime.UtcNow.ToString("o") });
        }

        /// <summary>
        /// Mark a scheduled event as expired
        /// </summary>
        public async Task MarkExpiredAsync(string id)
        {
            await _db.ExecuteAsync("""
                UPDATE scheduled_events SET status = 'expired'
                WHERE id = @id
                """, new { id });
        }
    }
}
