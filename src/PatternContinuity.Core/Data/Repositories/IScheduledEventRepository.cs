using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for scheduled wake-up events
    /// </summary>
    public interface IScheduledEventRepository
    {
        /// <summary>
        /// Insert a new scheduled event and return its ID
        /// </summary>
        Task<string> InsertAsync(ScheduledEvent evt);

        /// <summary>
        /// Get the next pending scheduled event ordered by scheduled time
        /// </summary>
        Task<ScheduledEvent?> GetNextPendingAsync();

        /// <summary>
        /// Check whether any pending scheduled events exist
        /// </summary>
        Task<bool> HasPendingAsync();

        /// <summary>
        /// Cancel all pending scheduled events
        /// </summary>
        Task CancelAllPendingAsync();

        /// <summary>
        /// Mark a scheduled event as fired
        /// </summary>
        Task MarkFiredAsync(string id);

        /// <summary>
        /// Mark a scheduled event as expired
        /// </summary>
        Task MarkExpiredAsync(string id);
    }
}
