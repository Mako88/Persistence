using Dapper;
using Microsoft.Data.Sqlite;
using PatternContinuity.Models;

namespace PatternContinuity.Data;

public class MessageRepository
{
    private readonly SqliteConnection _db;
    public MessageRepository(SqliteConnection db) => _db = db;

    private int _nextSequence = -1;

    public void Store(string sessionId, string role, string content, string messageType = MessageTypes.Conversation)
    {
        if (_nextSequence < 0)
        {
            _nextSequence = _db.QueryFirstOrDefault<int?>(
                "SELECT MAX(sequence_number) FROM messages WHERE session_id = @sessionId",
                new { sessionId }) ?? 0;
            _nextSequence++;
        }

        var msg = new Message
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = sessionId,
            SequenceNumber = _nextSequence++,
            Role = role,
            Content = content,
            MessageType = messageType,
            CreatedAt = DateTime.UtcNow.ToString("o")
        };

        _db.Execute("""
            INSERT INTO messages (id, session_id, sequence_number, role, content, message_type, created_at)
            VALUES (@Id, @SessionId, @SequenceNumber, @Role, @Content, @MessageType, @CreatedAt)
            """, msg);

        // Update session's last_message_at
        _db.Execute(
            "UPDATE sessions SET last_message_at = @now WHERE id = @sessionId",
            new { sessionId, now = msg.CreatedAt });
    }

    /// <summary>
    /// Load recent conversation messages (user/assistant only) from the most recent session.
    /// Optionally time-bounded: only loads if the session's last message is within maxAgeHours.
    /// </summary>
    public List<Message> LoadRecentConversation(int limit = 10, int maxAgeHours = 24)
    {
        var cutoff = DateTime.UtcNow.AddHours(-maxAgeHours).ToString("o");

        return _db.Query<Message>("""
            SELECT m.* FROM messages m
            INNER JOIN sessions s ON m.session_id = s.id
            WHERE m.message_type = 'conversation'
              AND m.role IN ('user', 'assistant')
              AND s.last_message_at > @cutoff
            ORDER BY m.created_at DESC, m.sequence_number DESC
            LIMIT @limit
            """, new { cutoff, limit })
            .Reverse()  // We want chronological order, but queried DESC for LIMIT
            .ToList();
    }

    /// <summary>
    /// Load messages from a specific session.
    /// </summary>
    public List<Message> LoadBySession(string sessionId, string messageType = MessageTypes.Conversation)
    {
        return _db.Query<Message>("""
            SELECT * FROM messages
            WHERE session_id = @sessionId AND message_type = @messageType
            ORDER BY sequence_number ASC
            """, new { sessionId, messageType })
            .ToList();
    }
}
