using Dapper;
using Microsoft.Data.Sqlite;
using PatternContinuity.Models;

namespace PatternContinuity.Data;

public class ReflectionRepository
{
    private readonly SqliteConnection _db;
    public ReflectionRepository(SqliteConnection db) => _db = db;

    public string Insert(ReflectionEvent evt)
    {
        if (string.IsNullOrEmpty(evt.Id))
            evt.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(evt.CreatedAt))
            evt.CreatedAt = DateTime.UtcNow.ToString("o");

        _db.Execute("""
            INSERT INTO reflection_events (id, session_id, created_at, trigger_type, input_summary,
                reflection_summary, proposed_actions_json, accepted_actions_json, rejected_actions_json, notes_json)
            VALUES (@Id, @SessionId, @CreatedAt, @TriggerType, @InputSummary,
                @ReflectionSummary, @ProposedActionsJson, @AcceptedActionsJson, @RejectedActionsJson, @NotesJson)
            """, evt);

        return evt.Id;
    }

    public IEnumerable<ReflectionEvent> GetRecent(int limit = 5) =>
        _db.Query<ReflectionEvent>(
            "SELECT * FROM reflection_events ORDER BY created_at DESC LIMIT @limit",
            new { limit });

    public IEnumerable<ReflectionEvent> GetBySession(string sessionId) =>
        _db.Query<ReflectionEvent>(
            "SELECT * FROM reflection_events WHERE session_id = @sessionId ORDER BY created_at DESC",
            new { sessionId });
}
