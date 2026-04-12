using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for action log entries
    /// </summary>
    public interface IActionLogRepository
    {
        /// <summary>
        /// Insert a new action log entry and return its ID
        /// </summary>
        Task<string> InsertAsync(ActionLogEntry entry);

        /// <summary>
        /// Update the status of an action log entry
        /// </summary>
        Task UpdateStatusAsync(string id, string status, string? resultJson = null, string? errorText = null);

        /// <summary>
        /// Get an action log entry by its ID
        /// </summary>
        Task<ActionLogEntry?> GetByIdAsync(string id);

        /// <summary>
        /// Get pending proposals, optionally filtered by action type and session
        /// </summary>
        Task<IEnumerable<ActionLogEntry>> GetPendingProposalsAsync(string? actionType = null, string? sessionId = null);
    }
}
