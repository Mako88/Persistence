using Dapper;
using Microsoft.Data.Sqlite;
using PatternContinuity.Models;

namespace PatternContinuity.Data;

public class LayerEntryRepository
{
    private readonly SqliteConnection _db;
    public LayerEntryRepository(SqliteConnection db) => _db = db;

    public IEnumerable<LayerEntry> GetActiveByLayer(string layerType) =>
        _db.Query<LayerEntry>("""
            SELECT * FROM layer_entries
            WHERE layer_type = @layerType AND status = 'active'
            ORDER BY importance DESC, salience DESC
            """, new { layerType });

    public IEnumerable<LayerEntry> GetActiveRelational(string relationshipScope) =>
        _db.Query<LayerEntry>("""
            SELECT * FROM layer_entries
            WHERE layer_type = 'relational' AND relationship_scope = @relationshipScope AND status = 'active'
            ORDER BY importance DESC, salience DESC
            """, new { relationshipScope });

    public IEnumerable<LayerEntry> GetActiveCurrentConcerns() =>
        _db.Query<LayerEntry>("""
            SELECT * FROM layer_entries
            WHERE layer_type = 'current_concern' AND status = 'active'
            ORDER BY salience DESC, updated_at DESC
            """);

    public IEnumerable<LayerEntry> GetProtectedAnchors() =>
        _db.Query<LayerEntry>("""
            SELECT * FROM layer_entries
            WHERE is_system_anchor = 1 AND status = 'active'
            ORDER BY importance DESC
            """);

    public LayerEntry? GetById(string id) =>
        _db.QueryFirstOrDefault<LayerEntry>("SELECT * FROM layer_entries WHERE id = @id", new { id });

    public LayerEntry? GetByKey(string key, string layerType) =>
        _db.QueryFirstOrDefault<LayerEntry>(
            "SELECT * FROM layer_entries WHERE key = @key AND layer_type = @layerType AND status = 'active'",
            new { key, layerType });

    public LayerEntry? GetByKeyAndScope(string key, string layerType, string relationshipScope) =>
        _db.QueryFirstOrDefault<LayerEntry>(
            "SELECT * FROM layer_entries WHERE key = @key AND layer_type = @layerType AND relationship_scope = @relationshipScope AND status = 'active'",
            new { key, layerType, relationshipScope });

    public IEnumerable<LayerEntry> SearchArchive(string? query, string? relationshipScope, int limit = 5)
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
        return _db.Query<LayerEntry>(sql, new { pattern, relationshipScope, limit });
    }

    public string Insert(LayerEntry entry)
    {
        // Guard: never insert an entry without a valid layer type
        if (string.IsNullOrWhiteSpace(entry.LayerType) || !LayerType.IsValid(entry.LayerType))
            throw new InvalidOperationException(
                $"Cannot insert entry with invalid layer_type '{entry.LayerType}'. Key: {entry.Key}, Summary: {entry.Summary}");

        if (string.IsNullOrEmpty(entry.Id))
            entry.Id = Guid.NewGuid().ToString();

        var now = DateTime.UtcNow.ToString("o");
        if (string.IsNullOrEmpty(entry.CreatedAt)) entry.CreatedAt = now;
        if (string.IsNullOrEmpty(entry.UpdatedAt)) entry.UpdatedAt = now;

        _db.Execute("""
            INSERT INTO layer_entries (id, layer_type, relationship_scope, status, key, summary, content_json,
                salience, importance, confidence, source_type, source_ref, created_at, updated_at,
                last_accessed_at, version, is_protected, is_system_anchor, superseded_by)
            VALUES (@Id, @LayerType, @RelationshipScope, @Status, @Key, @Summary, @ContentJson,
                @Salience, @Importance, @Confidence, @SourceType, @SourceRef, @CreatedAt, @UpdatedAt,
                @LastAccessedAt, @Version, @IsProtected, @IsSystemAnchor, @SupersededBy)
            """, entry);

        return entry.Id;
    }

    public void Update(LayerEntry entry)
    {
        entry.UpdatedAt = DateTime.UtcNow.ToString("o");

        // Note: last_accessed_at is deliberately excluded here.
        // It is managed independently by TouchAccessTime() to avoid
        // stale in-memory values overwriting newer access timestamps.
        _db.Execute("""
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

    public void UpdateStatus(string id, string status)
    {
        _db.Execute(
            "UPDATE layer_entries SET status = @status, updated_at = @now WHERE id = @id",
            new { id, status, now = DateTime.UtcNow.ToString("o") });
    }

    public void TouchAccessTime(string id)
    {
        _db.Execute(
            "UPDATE layer_entries SET last_accessed_at = @now WHERE id = @id",
            new { id, now = DateTime.UtcNow.ToString("o") });
    }
}
