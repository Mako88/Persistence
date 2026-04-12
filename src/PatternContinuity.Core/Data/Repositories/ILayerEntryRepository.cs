using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for continuity layer entries
    /// </summary>
    public interface ILayerEntryRepository
    {
        /// <summary>
        /// Get all active entries for a given layer type
        /// </summary>
        Task<IEnumerable<LayerEntry>> GetActiveByLayerAsync(string layerType);

        /// <summary>
        /// Get active relational entries for a given relationship scope
        /// </summary>
        Task<IEnumerable<LayerEntry>> GetActiveRelationalAsync(string relationshipScope);

        /// <summary>
        /// Get all active current concerns ordered by salience
        /// </summary>
        Task<IEnumerable<LayerEntry>> GetActiveCurrentConcernsAsync();

        /// <summary>
        /// Get all active protected anchor entries
        /// </summary>
        Task<IEnumerable<LayerEntry>> GetProtectedAnchorsAsync();

        /// <summary>
        /// Get an entry by its ID
        /// </summary>
        Task<LayerEntry?> GetByIdAsync(string id);

        /// <summary>
        /// Get an active entry by key and layer type
        /// </summary>
        Task<LayerEntry?> GetByKeyAsync(string key, string layerType);

        /// <summary>
        /// Get an active entry by key, layer type, and relationship scope
        /// </summary>
        Task<LayerEntry?> GetByKeyAndScopeAsync(string key, string layerType, string relationshipScope);

        /// <summary>
        /// Search the archive layer with optional query and scope filters
        /// </summary>
        Task<IEnumerable<LayerEntry>> SearchArchiveAsync(string? query, string? relationshipScope, int limit = 5);

        /// <summary>
        /// Insert a new layer entry and return its ID
        /// </summary>
        Task<string> InsertAsync(LayerEntry entry);

        /// <summary>
        /// Update an existing layer entry
        /// </summary>
        Task UpdateAsync(LayerEntry entry);

        /// <summary>
        /// Update only the status of a layer entry
        /// </summary>
        Task UpdateStatusAsync(string id, string status);

        /// <summary>
        /// Update the last accessed timestamp for a layer entry
        /// </summary>
        Task TouchAccessTimeAsync(string id);
    }
}
