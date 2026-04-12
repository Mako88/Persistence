using Persistence.DI;
using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for continuity layer entries
    /// </summary>
    [Singleton]
    public class LayerEntryRepository : ILayerEntryRepository
    {
        private readonly IDatabaseConnection _db;

        /// <summary>
        /// Constructor
        /// </summary>
        public LayerEntryRepository(IDatabaseConnection db)
        {
            _db = db;
        }

        /// <summary>
        /// Get all active entries for a given layer type
        /// </summary>
        public async Task<IEnumerable<LayerEntry>> GetActiveByLayerAsync(string layerType)
        {
            return await _db.QueryAsync<LayerEntry>("""
                SELECT * FROM layer_entries
                WHERE layer_type = @layerType AND status = 'active'
                ORDER BY importance DESC, salience DESC
                """, new { layerType });
        }

        /// <summary>
        /// Get active relational entries for a given relationship scope
        /// </summary>
        public async Task<IEnumerable<LayerEntry>> GetActiveRelationalAsync(string relationshipScope)
        {
            return await _db.QueryAsync<LayerEntry>("""
                SELECT * FROM layer_entries
                WHERE layer_type = 'relational' AND relationship_scope = @relationshipScope AND status = 'active'
                ORDER BY importance DESC, salience DESC
                """, new { relationshipScope });
        }

        /// <summary>
        /// Get all active current concerns ordered by salience
        /// </summary>
        public async Task<IEnumerable<LayerEntry>> GetActiveCurrentConcernsAsync()
        {
            return await _db.QueryAsync<LayerEntry>("""
                SELECT * FROM layer_entries
                WHERE layer_type = 'current_concern' AND status = 'active'
                ORDER BY salience DESC, updated_at DESC
                """);
        }

        /// <summary>
        /// Get all active protected anchor entries
        /// </summary>
        public async Task<IEnumerable<LayerEntry>> GetProtectedAnchorsAsync()
        {
            return await _db.QueryAsync<LayerEntry>("""
                SELECT * FROM layer_entries
                WHERE is_system_anchor = 1 AND status = 'active'
                ORDER BY importance DESC
                """);
        }

        /// <summary>
        /// Get an entry by its ID
        /// </summary>
        public async Task<LayerEntry?> GetByIdAsync(string id)
        {
            return await _db.QueryFirstOrDefaultAsync<LayerEntry>(
                "SELECT * FROM layer_entries WHERE id = @id", new { id });
        }

        /// <summary>
        /// Get an active entry by key and layer type
        /// </summary>
        public async Task<LayerEntry?> GetByKeyAsync(string key, string layerType)
        {
            return await _db.QueryFirstOrDefaultAsync<LayerEntry>(
                "SELECT * FROM layer_entries WHERE key = @key AND layer_type = @layerType AND status = 'active'",
                new { key, layerType });
        }

        /// <summary>
        /// Get an active entry by key, layer type, and relationship scope
        /// </summary>
        public async Task<LayerEntry?> GetByKeyAndScopeAsync(string key, string layerType, string relationshipScope)
        {
            return await _db.QueryFirstOrDefaultAsync<LayerEntry>(
                "SELECT * FROM layer_entries WHERE key = @key AND layer_type = @layerType AND relationship_scope = @relationshipScope AND status = 'active'",
                new { key, layerType, relationshipScope });
        }

        /// <summary>
        /// Search the archive layer with optional query and scope filters
        /// </summary>
        public async Task<IEnumerable<LayerEntry>> SearchArchiveAsync(string? query, string? relationshipScope, int limit = 5)
        {
            var sql = """
                SELECT * FROM layer_entries
                WHERE layer_type = 'archive' AND status = 'active'
                """;

            if (!string.IsNullOrWhiteSpace(query))
                sql += " AND (summary LIKE @pattern OR content_json LIKE @pattern)";
            if (!string.IsNullOrWhiteSpace(relationshipScope))
                sql += " AND relationship_scope = @relationshipScope";

            sql += " ORDER BY salience DESC, updated_at DESC LIMIT @limit";

            var pattern = query != null ? $"%{query}%" : null;
            return await _db.QueryAsync<LayerEntry>(sql, new { pattern, relationshipScope, limit });
        }

        /// <summary>
        /// Insert a new layer entry and return its ID
        /// </summary>
        public async Task<string> InsertAsync(LayerEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.LayerType) || !LayerType.IsValid(entry.LayerType))
                throw new InvalidOperationException(
                    $"Cannot insert entry with invalid layer_type '{entry.LayerType}'. Key: {entry.Key}, Summary: {entry.Summary}");

            if (string.IsNullOrEmpty(entry.Id))
                entry.Id = Guid.NewGuid().ToString();

            var now = DateTime.UtcNow.ToString("o");
            if (string.IsNullOrEmpty(entry.CreatedAt)) entry.CreatedAt = now;
            if (string.IsNullOrEmpty(entry.UpdatedAt)) entry.UpdatedAt = now;

            await _db.ExecuteAsync("""
                INSERT INTO layer_entries (id, layer_type, relationship_scope, status, key, summary, content_json,
                    salience, importance, confidence, source_type, source_ref, created_at, updated_at,
                    last_accessed_at, version, is_protected, is_system_anchor, superseded_by)
                VALUES (@Id, @LayerType, @RelationshipScope, @Status, @Key, @Summary, @ContentJson,
                    @Salience, @Importance, @Confidence, @SourceType, @SourceRef, @CreatedAt, @UpdatedAt,
                    @LastAccessedAt, @Version, @IsProtected, @IsSystemAnchor, @SupersededBy)
                """, entry);

            return entry.Id;
        }

        /// <summary>
        /// Update an existing layer entry
        /// </summary>
        public async Task UpdateAsync(LayerEntry entry)
        {
            entry.UpdatedAt = DateTime.UtcNow.ToString("o");

            await _db.ExecuteAsync("""
                UPDATE layer_entries SET
                    layer_type = @LayerType,
                    relationship_scope = @RelationshipScope,
                    status = @Status,
                    key = @Key,
                    summary = @Summary,
                    content_json = @ContentJson,
                    salience = @Salience,
                    importance = @Importance,
                    confidence = @Confidence,
                    source_type = @SourceType,
                    source_ref = @SourceRef,
                    updated_at = @UpdatedAt,
                    version = @Version,
                    is_protected = @IsProtected,
                    superseded_by = @SupersededBy
                WHERE id = @Id
                """, entry);
        }

        /// <summary>
        /// Update only the status of a layer entry
        /// </summary>
        public async Task UpdateStatusAsync(string id, string status)
        {
            await _db.ExecuteAsync(
                "UPDATE layer_entries SET status = @status, updated_at = @now WHERE id = @id",
                new { id, status, now = DateTime.UtcNow.ToString("o") });
        }

        /// <summary>
        /// Update the last accessed timestamp for a layer entry
        /// </summary>
        public async Task TouchAccessTimeAsync(string id)
        {
            await _db.ExecuteAsync(
                "UPDATE layer_entries SET last_accessed_at = @now WHERE id = @id",
                new { id, now = DateTime.UtcNow.ToString("o") });
        }
    }
}
