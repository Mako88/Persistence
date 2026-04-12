using Persistence.DI;
using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for action log entries
    /// </summary>
    [Singleton]
    public class ActionLogRepository : IActionLogRepository
    {
        private readonly IDatabaseConnection _db;

        /// <summary>
        /// Constructor
        /// </summary>
        public ActionLogRepository(IDatabaseConnection db)
        {
            _db = db;
        }

        /// <summary>
        /// Insert a new action log entry and return its ID
        /// </summary>
        public async Task<string> InsertAsync(ActionLogEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Id))
                entry.Id = Guid.NewGuid().ToString();
            if (string.IsNullOrEmpty(entry.CreatedAt))
                entry.CreatedAt = DateTime.UtcNow.ToString("o");

            await _db.ExecuteAsync("""
                INSERT INTO action_log (id, session_id, reflection_event_id, created_at, action_type,
                    target_entry_id, payload_json, result_json, status, error_text)
                VALUES (@Id, @SessionId, @ReflectionEventId, @CreatedAt, @ActionType,
                    @TargetEntryId, @PayloadJson, @ResultJson, @Status, @ErrorText)
                """, entry);

            return entry.Id;
        }

        /// <summary>
        /// Update the status of an action log entry
        /// </summary>
        public async Task UpdateStatusAsync(string id, string status, string? resultJson = null, string? errorText = null)
        {
            await _db.ExecuteAsync("""
                UPDATE action_log SET status = @status, result_json = @resultJson, error_text = @errorText
                WHERE id = @id
                """, new { id, status, resultJson, errorText });
        }

        /// <summary>
        /// Get an action log entry by its ID
        /// </summary>
        public async Task<ActionLogEntry?> GetByIdAsync(string id)
        {
            return await _db.QueryFirstOrDefaultAsync<ActionLogEntry>(
                "SELECT * FROM action_log WHERE id = @id", new { id });
        }

        /// <summary>
        /// Get pending proposals, optionally filtered by action type and session
        /// </summary>
        public async Task<IEnumerable<ActionLogEntry>> GetPendingProposalsAsync(string? actionType = null, string? sessionId = null)
        {
            var sql = "SELECT * FROM action_log WHERE status = 'proposed'";
            if (actionType != null)
                sql += " AND action_type = @actionType";
            if (sessionId != null)
                sql += " AND session_id = @sessionId";
            sql += " ORDER BY created_at DESC";

            return await _db.QueryAsync<ActionLogEntry>(sql, new { actionType, sessionId });
        }
    }
}
