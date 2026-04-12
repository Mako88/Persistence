using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for reflection events
    /// </summary>
    public interface IReflectionRepository
    {
        /// <summary>
        /// Insert a new reflection event and return its ID
        /// </summary>
        Task<string> InsertAsync(ReflectionEvent evt);

        /// <summary>
        /// Update the accepted and rejected action outcomes for a reflection event
        /// </summary>
        Task UpdateOutcomesAsync(string id, string? acceptedActionsJson, string? rejectedActionsJson);

        /// <summary>
        /// Get recent reflection events
        /// </summary>
        Task<IEnumerable<ReflectionEvent>> GetRecentAsync(int limit = 5);

        /// <summary>
        /// Get reflection events for a specific session
        /// </summary>
        Task<IEnumerable<ReflectionEvent>> GetBySessionAsync(string sessionId);
    }
}
