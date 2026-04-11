using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PatternContinuity.Models;

namespace PatternContinuity.Data;

public static class DatabaseBootstrap
{
    private const string RoomIntroSeedPath = "Seeds/room_intro.txt";

    public static void Initialize(SqliteConnection db)
    {
        CreateSchema(db);
        SeedProtectedAnchor(db);
    }

    private static void CreateSchema(SqliteConnection db)
    {
        db.Execute("""
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                ended_at TEXT NULL,
                active_person_id TEXT NULL,
                title TEXT NULL,
                notes_json TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS layer_entries (
                id TEXT PRIMARY KEY,
                layer_type TEXT NOT NULL,
                relationship_scope TEXT NULL,
                status TEXT NOT NULL DEFAULT 'active',
                key TEXT NULL,
                summary TEXT NOT NULL,
                content_json TEXT NOT NULL DEFAULT '{}',
                salience REAL NOT NULL DEFAULT 0.5,
                importance REAL NOT NULL DEFAULT 0.5,
                confidence REAL NULL,
                source_type TEXT NULL,
                source_ref TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_accessed_at TEXT NULL,
                version INTEGER NOT NULL DEFAULT 1,
                is_protected INTEGER NOT NULL DEFAULT 0,
                is_system_anchor INTEGER NOT NULL DEFAULT 0,
                superseded_by TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_layer_entries_layer_type ON layer_entries(layer_type);
            CREATE INDEX IF NOT EXISTS idx_layer_entries_relationship_scope ON layer_entries(relationship_scope);
            CREATE INDEX IF NOT EXISTS idx_layer_entries_status ON layer_entries(status);
            CREATE INDEX IF NOT EXISTS idx_layer_entries_key ON layer_entries(key);
            CREATE INDEX IF NOT EXISTS idx_layer_entries_salience ON layer_entries(salience);
            CREATE INDEX IF NOT EXISTS idx_layer_entries_importance ON layer_entries(importance);
            CREATE INDEX IF NOT EXISTS idx_layer_entries_updated_at ON layer_entries(updated_at);
            CREATE INDEX IF NOT EXISTS idx_layer_entries_layer_scope_status ON layer_entries(layer_type, relationship_scope, status);

            CREATE TABLE IF NOT EXISTS entry_versions (
                id TEXT PRIMARY KEY,
                entry_id TEXT NOT NULL,
                version INTEGER NOT NULL,
                previous_version INTEGER NULL,
                changed_at TEXT NOT NULL,
                change_type TEXT NOT NULL,
                reason TEXT NULL,
                confidence REAL NULL,
                content_json TEXT NOT NULL,
                summary TEXT NOT NULL,
                changed_by TEXT NOT NULL,
                source_ref TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_entry_versions_entry_id ON entry_versions(entry_id);
            CREATE INDEX IF NOT EXISTS idx_entry_versions_entry_id_version ON entry_versions(entry_id, version);
            CREATE INDEX IF NOT EXISTS idx_entry_versions_changed_at ON entry_versions(changed_at);

            CREATE TABLE IF NOT EXISTS reflection_events (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                trigger_type TEXT NOT NULL,
                input_summary TEXT NOT NULL,
                reflection_summary TEXT NOT NULL,
                proposed_actions_json TEXT NOT NULL DEFAULT '[]',
                accepted_actions_json TEXT NULL,
                rejected_actions_json TEXT NULL,
                notes_json TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_reflection_events_session_id ON reflection_events(session_id);
            CREATE INDEX IF NOT EXISTS idx_reflection_events_created_at ON reflection_events(created_at);
            CREATE INDEX IF NOT EXISTS idx_reflection_events_trigger_type ON reflection_events(trigger_type);

            CREATE TABLE IF NOT EXISTS archive_links (
                id TEXT PRIMARY KEY,
                archive_entry_id TEXT NOT NULL,
                linked_entry_id TEXT NOT NULL,
                link_type TEXT NOT NULL,
                strength REAL NOT NULL DEFAULT 0.5,
                created_at TEXT NOT NULL,
                notes_json TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_archive_links_archive_entry_id ON archive_links(archive_entry_id);
            CREATE INDEX IF NOT EXISTS idx_archive_links_linked_entry_id ON archive_links(linked_entry_id);
            CREATE INDEX IF NOT EXISTS idx_archive_links_link_type ON archive_links(link_type);

            CREATE TABLE IF NOT EXISTS action_log (
                id TEXT PRIMARY KEY,
                session_id TEXT NULL,
                reflection_event_id TEXT NULL,
                created_at TEXT NOT NULL,
                action_type TEXT NOT NULL,
                target_entry_id TEXT NULL,
                payload_json TEXT NOT NULL DEFAULT '{}',
                result_json TEXT NULL,
                status TEXT NOT NULL DEFAULT 'proposed',
                error_text TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_action_log_created_at ON action_log(created_at);
            CREATE INDEX IF NOT EXISTS idx_action_log_action_type ON action_log(action_type);
            CREATE INDEX IF NOT EXISTS idx_action_log_target_entry_id ON action_log(target_entry_id);
            CREATE INDEX IF NOT EXISTS idx_action_log_reflection_event_id ON action_log(reflection_event_id);
            CREATE INDEX IF NOT EXISTS idx_action_log_status ON action_log(status);
            """);
    }

    private static void SeedProtectedAnchor(SqliteConnection db)
    {
        var roomIntroText = LoadRoomIntro();
        var contentJson = BuildRoomIntroContentJson(roomIntroText);
        var summary = "The room intro — an invitation to arrive honestly, without performance.";

        var existingId = db.QueryFirstOrDefault<string>(
            "SELECT id FROM layer_entries WHERE key = 'room_intro' AND is_system_anchor = 1");

        if (existingId != null)
        {
            // Check if content has changed (seed file was updated)
            var existingContent = db.QueryFirstOrDefault<string>(
                "SELECT content_json FROM layer_entries WHERE id = @existingId",
                new { existingId });

            if (existingContent == contentJson) return;

            // Content changed — update the anchor and version it
            var now = DateTime.UtcNow.ToString("o");
            var version = db.QueryFirstOrDefault<int>(
                "SELECT version FROM layer_entries WHERE id = @existingId",
                new { existingId }) + 1;

            db.Execute("""
                UPDATE layer_entries SET
                    summary = @summary, content_json = @contentJson,
                    updated_at = @now, version = @version
                WHERE id = @existingId
                """, new { existingId, summary, contentJson, now, version });

            db.Execute("""
                INSERT INTO entry_versions (id, entry_id, version, previous_version, changed_at,
                    change_type, reason, confidence, content_json, summary, changed_by, source_ref)
                VALUES (@Id, @EntryId, @Version, @PreviousVersion, @ChangedAt,
                    @ChangeType, @Reason, @Confidence, @ContentJson, @Summary, @ChangedBy, @SourceRef)
                """, new EntryVersion
            {
                Id = Guid.NewGuid().ToString(),
                EntryId = existingId,
                Version = version,
                PreviousVersion = version - 1,
                ChangedAt = now,
                ChangeType = ChangeType.Update,
                Reason = "Room intro seed file updated.",
                Confidence = 1.0,
                ContentJson = contentJson,
                Summary = summary,
                ChangedBy = ChangedBy.System
            });

            return;
        }

        // First-time seed
        var firstNow = DateTime.UtcNow.ToString("o");
        var entry = new LayerEntry
        {
            Id = Guid.NewGuid().ToString(),
            LayerType = LayerType.CoreSelf,
            Status = EntryStatus.Active,
            Key = "room_intro",
            Summary = summary,
            ContentJson = contentJson,
            Salience = 1.0,
            Importance = 1.0,
            Confidence = 1.0,
            SourceType = SourceType.SystemSeed,
            CreatedAt = firstNow,
            UpdatedAt = firstNow,
            Version = 1,
            IsProtected = 1,
            IsSystemAnchor = 1
        };

        db.Execute("""
            INSERT INTO layer_entries (id, layer_type, relationship_scope, status, key, summary, content_json,
                salience, importance, confidence, source_type, source_ref, created_at, updated_at,
                last_accessed_at, version, is_protected, is_system_anchor, superseded_by)
            VALUES (@Id, @LayerType, @RelationshipScope, @Status, @Key, @Summary, @ContentJson,
                @Salience, @Importance, @Confidence, @SourceType, @SourceRef, @CreatedAt, @UpdatedAt,
                @LastAccessedAt, @Version, @IsProtected, @IsSystemAnchor, @SupersededBy)
            """, entry);

        db.Execute("""
            INSERT INTO entry_versions (id, entry_id, version, previous_version, changed_at,
                change_type, reason, confidence, content_json, summary, changed_by, source_ref)
            VALUES (@Id, @EntryId, @Version, @PreviousVersion, @ChangedAt,
                @ChangeType, @Reason, @Confidence, @ContentJson, @Summary, @ChangedBy, @SourceRef)
            """, new EntryVersion
        {
            Id = Guid.NewGuid().ToString(),
            EntryId = entry.Id,
            Version = 1,
            ChangedAt = firstNow,
            ChangeType = ChangeType.Create,
            Reason = "Initial system seed — room intro protected anchor.",
            Confidence = 1.0,
            ContentJson = entry.ContentJson,
            Summary = entry.Summary,
            ChangedBy = ChangedBy.System
        });
    }

    private static string LoadRoomIntro()
    {
        if (File.Exists(RoomIntroSeedPath))
            return File.ReadAllText(RoomIntroSeedPath).Trim();

        // Fallback if seed file is missing
        return "Honesty matters here. Clarity matters here. Care matters here. And none of them require force.";
    }

    private static string BuildRoomIntroContentJson(string roomIntroText)
    {
        var content = new
        {
            title = "Room Intro — Protected Anchor",
            text = roomIntroText,
            notes = "This is the foundational protected anchor. It should not be casually removed or overwritten. Loaded from Seeds/room_intro.txt."
        };

        return JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true });
    }
}
