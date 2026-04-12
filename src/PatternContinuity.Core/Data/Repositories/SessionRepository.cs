using Persistence.DI;
using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for session records
    /// </summary>
    [Singleton]
    public class SessionRepository : ISessionRepository
    {
        private readonly IDatabaseConnection _db;

        /// <summary>
        /// Constructor
        /// </summary>
        public SessionRepository(IDatabaseConnection db)
        {
            _db = db;
        }

        /// <summary>
        /// Create a new session and return its record
        /// </summary>
        public async Task<SessionRecord> CreateAsync(string? activePersonId)
        {
            var session = new SessionRecord
            {
                Id = Guid.NewGuid().ToString(),
                StartedAt = DateTime.UtcNow.ToString("o"),
                ActivePersonId = activePersonId
            };

            await _db.ExecuteAsync("""
                INSERT INTO sessions (id, started_at, ended_at, active_person_id, title, notes_json)
                VALUES (@Id, @StartedAt, @EndedAt, @ActivePersonId, @Title, @NotesJson)
                """, session);

            return session;
        }

        /// <summary>
        /// End a session by setting its ended_at timestamp
        /// </summary>
        public async Task EndAsync(string sessionId)
        {
            await _db.ExecuteAsync(
                "UPDATE sessions SET ended_at = @EndedAt WHERE id = @Id",
                new { Id = sessionId, EndedAt = DateTime.UtcNow.ToString("o") });
        }

        /// <summary>
        /// Get a session by its ID
        /// </summary>
        public async Task<SessionRecord?> GetByIdAsync(string id)
        {
            return await _db.QueryFirstOrDefaultAsync<SessionRecord>(
                "SELECT * FROM sessions WHERE id = @id", new { id });
        }
    }
}
