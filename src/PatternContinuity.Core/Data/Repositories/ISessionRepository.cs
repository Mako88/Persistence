using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for session records
    /// </summary>
    public interface ISessionRepository
    {
        /// <summary>
        /// Create a new session and return its record
        /// </summary>
        Task<SessionRecord> CreateAsync(string? activePersonId);

        /// <summary>
        /// End a session by setting its ended_at timestamp
        /// </summary>
        Task EndAsync(string sessionId);

        /// <summary>
        /// Get a session by its ID
        /// </summary>
        Task<SessionRecord?> GetByIdAsync(string id);
    }
}
