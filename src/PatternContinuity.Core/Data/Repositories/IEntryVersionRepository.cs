using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for entry version history
    /// </summary>
    public interface IEntryVersionRepository
    {
        /// <summary>
        /// Insert a new version record
        /// </summary>
        Task InsertAsync(EntryVersion version);

        /// <summary>
        /// Get all versions for a given entry
        /// </summary>
        Task<IEnumerable<EntryVersion>> GetByEntryIdAsync(string entryId);

        /// <summary>
        /// Get recent changes across all entries, optionally filtered by layer type
        /// </summary>
        Task<IEnumerable<EntryVersion>> GetRecentChangesAsync(string[]? layerTypes = null, int limit = 10);

        /// <summary>
        /// Get a specific version of an entry
        /// </summary>
        Task<EntryVersion?> GetSpecificVersionAsync(string entryId, int version);
    }
}
