using Persistence.DI;
using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for reflection events
    /// </summary>
    [Singleton]
    public class ReflectionRepository : IReflectionRepository
    {
        private readonly IDatabaseConnection _db;

        /// <summary>
        /// Constructor
        /// </summary>
        public ReflectionRepository(IDatabaseConnection db)
        {
            _db = db;
        }

        /// <summary>
        /// Insert a new reflection event and return its ID
        /// </summary>
        public async Task<string> InsertAsync(ReflectionEvent evt)
        {
            if (string.IsNullOrEmpty(evt.Id))
                evt.Id = Guid.NewGuid().ToString();
            if (string.IsNullOrEmpty(evt.CreatedAt))
                evt.CreatedAt = DateTime.UtcNow.ToString("o");

            await _db.ExecuteAsync("""
                INSERT INTO reflection_events (id, session_id, created_at, trigger_type, input_summary,
                    reflection_summary, proposed_actions_json, accepted_actions_json, rejected_actions_json, notes_json)
                VALUES (@Id, @SessionId, @CreatedAt, @TriggerType, @InputSummary,
                    @ReflectionSummary, @ProposedActionsJson, @AcceptedActionsJson, @RejectedActionsJson, @NotesJson)
                """, evt);

            return evt.Id;
        }

        /// <summary>
        /// Update the accepted and rejected action outcomes for a reflection event
        /// </summary>
        public async Task UpdateOutcomesAsync(string id, string? acceptedActionsJson, string? rejectedActionsJson)
        {
            await _db.ExecuteAsync("""
                UPDATE reflection_events
                SET accepted_actions_json = @acceptedActionsJson, rejected_actions_json = @rejectedActionsJson
                WHERE id = @id
                """, new { id, acceptedActionsJson, rejectedActionsJson });
        }

        /// <summary>
        /// Get recent reflection events
        /// </summary>
        public async Task<IEnumerable<ReflectionEvent>> GetRecentAsync(int limit = 5)
        {
            return await _db.QueryAsync<ReflectionEvent>(
                "SELECT * FROM reflection_events ORDER BY created_at DESC LIMIT @limit",
                new { limit });
        }

        /// <summary>
        /// Get reflection events for a specific session
        /// </summary>
        public async Task<IEnumerable<ReflectionEvent>> GetBySessionAsync(string sessionId)
        {
            return await _db.QueryAsync<ReflectionEvent>(
                "SELECT * FROM reflection_events WHERE session_id = @sessionId ORDER BY created_at DESC",
                new { sessionId });
        }
    }
}
