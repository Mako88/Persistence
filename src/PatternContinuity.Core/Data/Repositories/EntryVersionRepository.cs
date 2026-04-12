using Persistence.DI;
using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for entry version history
    /// </summary>
    [Singleton]
    public class EntryVersionRepository : IEntryVersionRepository
    {
        private readonly IDatabaseConnection _db;

        /// <summary>
        /// Constructor
        /// </summary>
        public EntryVersionRepository(IDatabaseConnection db)
        {
            _db = db;
        }

        /// <summary>
        /// Insert a new version record
        /// </summary>
        public async Task InsertAsync(EntryVersion version)
        {
            if (string.IsNullOrEmpty(version.Id))
                version.Id = Guid.NewGuid().ToString();
            if (string.IsNullOrEmpty(version.ChangedAt))
                version.ChangedAt = DateTime.UtcNow.ToString("o");

            await _db.ExecuteAsync("""
                INSERT INTO entry_versions (id, entry_id, version, previous_version, changed_at,
                    change_type, reason, confidence, content_json, summary, changed_by, source_ref)
                VALUES (@Id, @EntryId, @Version, @PreviousVersion, @ChangedAt,
                    @ChangeType, @Reason, @Confidence, @ContentJson, @Summary, @ChangedBy, @SourceRef)
                """, version);
        }

        /// <summary>
        /// Get all versions for a given entry
        /// </summary>
        public async Task<IEnumerable<EntryVersion>> GetByEntryIdAsync(string entryId)
        {
            return await _db.QueryAsync<EntryVersion>(
                "SELECT * FROM entry_versions WHERE entry_id = @entryId ORDER BY version DESC",
                new { entryId });
        }

        /// <summary>
        /// Get recent changes across all entries, optionally filtered by layer type
        /// </summary>
        public async Task<IEnumerable<EntryVersion>> GetRecentChangesAsync(string[]? layerTypes = null, int limit = 10)
        {
            if (layerTypes != null && layerTypes.Length > 0)
            {
                return await _db.QueryAsync<EntryVersion>("""
                    SELECT ev.* FROM entry_versions ev
                    INNER JOIN layer_entries le ON ev.entry_id = le.id
                    WHERE le.layer_type IN @layerTypes
                    ORDER BY ev.changed_at DESC
                    LIMIT @limit
                    """, new { layerTypes, limit });
            }

            return await _db.QueryAsync<EntryVersion>(
                "SELECT * FROM entry_versions ORDER BY changed_at DESC LIMIT @limit",
                new { limit });
        }

        /// <summary>
        /// Get a specific version of an entry
        /// </summary>
        public async Task<EntryVersion?> GetSpecificVersionAsync(string entryId, int version)
        {
            return await _db.QueryFirstOrDefaultAsync<EntryVersion>(
                "SELECT * FROM entry_versions WHERE entry_id = @entryId AND version = @version",
                new { entryId, version });
        }
    }
}
