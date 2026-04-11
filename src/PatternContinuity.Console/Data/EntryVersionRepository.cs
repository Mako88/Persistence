using Dapper;
using Microsoft.Data.Sqlite;
using PatternContinuity.Models;

namespace PatternContinuity.Data;

public class EntryVersionRepository
{
    private readonly SqliteConnection _db;
    public EntryVersionRepository(SqliteConnection db) => _db = db;

    public void Insert(EntryVersion version)
    {
        if (string.IsNullOrEmpty(version.Id))
            version.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(version.ChangedAt))
            version.ChangedAt = DateTime.UtcNow.ToString("o");

        _db.Execute("""
            INSERT INTO entry_versions (id, entry_id, version, previous_version, changed_at,
                change_type, reason, confidence, content_json, summary, changed_by, source_ref)
            VALUES (@Id, @EntryId, @Version, @PreviousVersion, @ChangedAt,
                @ChangeType, @Reason, @Confidence, @ContentJson, @Summary, @ChangedBy, @SourceRef)
            """, version);
    }

    public IEnumerable<EntryVersion> GetByEntryId(string entryId) =>
        _db.Query<EntryVersion>(
            "SELECT * FROM entry_versions WHERE entry_id = @entryId ORDER BY version DESC",
            new { entryId });

    public IEnumerable<EntryVersion> GetRecentChanges(string[]? layerTypes = null, int limit = 10)
    {
        if (layerTypes != null && layerTypes.Length > 0)
        {
            return _db.Query<EntryVersion>("""
                SELECT ev.* FROM entry_versions ev
                INNER JOIN layer_entries le ON ev.entry_id = le.id
                WHERE le.layer_type IN @layerTypes
                ORDER BY ev.changed_at DESC
                LIMIT @limit
                """, new { layerTypes, limit });
        }

        return _db.Query<EntryVersion>(
            "SELECT * FROM entry_versions ORDER BY changed_at DESC LIMIT @limit",
            new { limit });
    }

    public EntryVersion? GetSpecificVersion(string entryId, int version) =>
        _db.QueryFirstOrDefault<EntryVersion>(
            "SELECT * FROM entry_versions WHERE entry_id = @entryId AND version = @version",
            new { entryId, version });
}
