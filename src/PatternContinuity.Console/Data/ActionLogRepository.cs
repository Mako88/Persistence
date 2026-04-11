using Dapper;
using Microsoft.Data.Sqlite;
using PatternContinuity.Models;

namespace PatternContinuity.Data;

public class ActionLogRepository
{
    private readonly SqliteConnection _db;
    public ActionLogRepository(SqliteConnection db) => _db = db;

    public string Insert(ActionLogEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Id))
            entry.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(entry.CreatedAt))
            entry.CreatedAt = DateTime.UtcNow.ToString("o");

        _db.Execute("""
            INSERT INTO action_log (id, session_id, reflection_event_id, created_at, action_type,
                target_entry_id, payload_json, result_json, status, error_text)
            VALUES (@Id, @SessionId, @ReflectionEventId, @CreatedAt, @ActionType,
                @TargetEntryId, @PayloadJson, @ResultJson, @Status, @ErrorText)
            """, entry);

        return entry.Id;
    }

    public void UpdateStatus(string id, string status, string? resultJson = null, string? errorText = null)
    {
        _db.Execute("""
            UPDATE action_log SET status = @status, result_json = @resultJson, error_text = @errorText
            WHERE id = @id
            """, new { id, status, resultJson, errorText });
    }

    public ActionLogEntry? GetById(string id) =>
        _db.QueryFirstOrDefault<ActionLogEntry>("SELECT * FROM action_log WHERE id = @id", new { id });

    public IEnumerable<ActionLogEntry> GetPendingProposals(string? actionType = null)
    {
        var sql = "SELECT * FROM action_log WHERE status = 'proposed'";
        if (actionType != null)
            sql += " AND action_type = @actionType";
        sql += " ORDER BY created_at DESC";

        return _db.Query<ActionLogEntry>(sql, new { actionType });
    }
}
