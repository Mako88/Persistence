using Persistence.DI;
using Persistence.Models;
using System.Text.Json;

namespace Persistence.Data
{
    /// <summary>
    /// Handles database schema creation, migrations, and seed data
    /// </summary>
    [Singleton]
    public class DatabaseBootstrap : IDatabaseBootstrap
    {
        private const string RoomIntroSeedPath = "Seeds/room_intro.txt";

        private readonly IDatabaseConnection _db;

        /// <summary>
        /// Constructor
        /// </summary>
        public DatabaseBootstrap(IDatabaseConnection db)
        {
            _db = db;
        }

        /// <summary>
        /// Initialize the database schema, run migrations, and seed data
        /// </summary>
        public async Task InitializeAsync()
        {
            await CreateSchemaAsync();
            await MigrateSchemaAsync();
            await SeedProtectedAnchorAsync();
        }

        /// <summary>
        /// Create all database tables and indexes
        /// </summary>
        private async Task CreateSchemaAsync()
        {
            await _db.ExecuteAsync("""
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

                CREATE TABLE IF NOT EXISTS messages (
                    id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    sequence_number INTEGER NOT NULL,
                    role TEXT NOT NULL,
                    content TEXT NOT NULL,
                    message_type TEXT NOT NULL DEFAULT 'conversation',
                    created_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_messages_session_id ON messages(session_id);
                CREATE INDEX IF NOT EXISTS idx_messages_session_seq ON messages(session_id, sequence_number);
                CREATE INDEX IF NOT EXISTS idx_messages_message_type ON messages(message_type);
                CREATE INDEX IF NOT EXISTS idx_messages_created_at ON messages(created_at);

                CREATE TABLE IF NOT EXISTS scheduled_events (
                    id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    scheduled_for TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending',
                    reason TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL,
                    fired_at TEXT NULL,
                    autonomous_depth INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS idx_scheduled_events_status ON scheduled_events(status);
                CREATE INDEX IF NOT EXISTS idx_scheduled_events_scheduled_for ON scheduled_events(scheduled_for);
                """);
        }

        /// <summary>
        /// Run schema migrations for databases created before newer features
        /// </summary>
        private async Task MigrateSchemaAsync()
        {
            var columns = await _db.QueryAsync<string>("SELECT name FROM pragma_table_info('sessions')");
            if (!columns.Contains("last_message_at"))
            {
                await _db.ExecuteAsync("ALTER TABLE sessions ADD COLUMN last_message_at TEXT NULL");
            }

            var tables = await _db.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type='table'");
            if (!tables.Contains("scheduled_events"))
            {
                await _db.ExecuteAsync("""
                    CREATE TABLE IF NOT EXISTS scheduled_events (
                        id TEXT PRIMARY KEY,
                        session_id TEXT NOT NULL,
                        event_type TEXT NOT NULL,
                        scheduled_for TEXT NOT NULL,
                        status TEXT NOT NULL DEFAULT 'pending',
                        reason TEXT NOT NULL DEFAULT '',
                        created_at TEXT NOT NULL,
                        fired_at TEXT NULL,
                        autonomous_depth INTEGER NOT NULL DEFAULT 0
                    )
                    """);
                await _db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_scheduled_events_status ON scheduled_events(status)");
                await _db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_scheduled_events_scheduled_for ON scheduled_events(scheduled_for)");
            }
        }

        /// <summary>
        /// Seed or update the room intro protected anchor
        /// </summary>
        private async Task SeedProtectedAnchorAsync()
        {
            var roomIntroText = LoadRoomIntro();
            var contentJson = BuildRoomIntroContentJson(roomIntroText);
            var summary = "The room intro — an invitation to arrive honestly, without performance.";

            var existingId = await _db.QueryFirstOrDefaultAsync<string>(
                "SELECT id FROM layer_entries WHERE key = 'room_intro' AND is_system_anchor = 1");

            if (existingId != null)
            {
                var existingContent = await _db.QueryFirstOrDefaultAsync<string>(
                    "SELECT content_json FROM layer_entries WHERE id = @existingId",
                    new { existingId });

                if (existingContent == contentJson) return;

                var now = DateTime.UtcNow.ToString("o");
                var version = await _db.QueryFirstOrDefaultAsync<int>(
                    "SELECT version FROM layer_entries WHERE id = @existingId",
                    new { existingId }) + 1;

                await _db.ExecuteAsync("""
                    UPDATE layer_entries SET
                        summary = @summary, content_json = @contentJson,
                        updated_at = @now, version = @version
                    WHERE id = @existingId
                    """, new { existingId, summary, contentJson, now, version });

                await _db.ExecuteAsync("""
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

            await _db.ExecuteAsync("""
                INSERT INTO layer_entries (id, layer_type, relationship_scope, status, key, summary, content_json,
                    salience, importance, confidence, source_type, source_ref, created_at, updated_at,
                    last_accessed_at, version, is_protected, is_system_anchor, superseded_by)
                VALUES (@Id, @LayerType, @RelationshipScope, @Status, @Key, @Summary, @ContentJson,
                    @Salience, @Importance, @Confidence, @SourceType, @SourceRef, @CreatedAt, @UpdatedAt,
                    @LastAccessedAt, @Version, @IsProtected, @IsSystemAnchor, @SupersededBy)
                """, entry);

            await _db.ExecuteAsync("""
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

        /// <summary>
        /// Load the room intro text from the seed file
        /// </summary>
        private static string LoadRoomIntro()
        {
            if (File.Exists(RoomIntroSeedPath))
                return File.ReadAllText(RoomIntroSeedPath).Trim();

            return "Honesty matters here. Clarity matters here. Care matters here. And none of them require force.";
        }

        /// <summary>
        /// Build the content JSON for the room intro anchor
        /// </summary>
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
}
