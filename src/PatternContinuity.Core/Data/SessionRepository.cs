using Dapper;
using Microsoft.Data.Sqlite;
using PatternContinuity.Models;

namespace PatternContinuity.Data;

public class SessionRepository
{
    private readonly SqliteConnection _db;
    public SessionRepository(SqliteConnection db) => _db = db;

    public SessionRecord Create(string? activePersonId)
    {
        var session = new SessionRecord
        {
            Id = Guid.NewGuid().ToString(),
            StartedAt = DateTime.UtcNow.ToString("o"),
            ActivePersonId = activePersonId
        };

        _db.Execute("""
            INSERT INTO sessions (id, started_at, ended_at, active_person_id, title, notes_json)
            VALUES (@Id, @StartedAt, @EndedAt, @ActivePersonId, @Title, @NotesJson)
            """, session);

        return session;
    }

    public void End(string sessionId)
    {
        _db.Execute(
            "UPDATE sessions SET ended_at = @EndedAt WHERE id = @Id",
            new { Id = sessionId, EndedAt = DateTime.UtcNow.ToString("o") });
    }

    public SessionRecord? GetById(string id) =>
        _db.QueryFirstOrDefault<SessionRecord>("SELECT * FROM sessions WHERE id = @id", new { id });
}
