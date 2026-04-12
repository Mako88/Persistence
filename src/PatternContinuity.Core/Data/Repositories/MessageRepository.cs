using Persistence.DI;
using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for conversation messages
    /// </summary>
    [Singleton]
    public class MessageRepository : IMessageRepository
    {
        private readonly IDatabaseConnection _db;
        private int _nextSequence = -1;

        /// <summary>
        /// Constructor
        /// </summary>
        public MessageRepository(IDatabaseConnection db)
        {
            _db = db;
        }

        /// <summary>
        /// Store a new message in the database
        /// </summary>
        public async Task StoreAsync(string sessionId, string role, string content, string messageType = MessageTypes.Conversation)
        {
            if (_nextSequence < 0)
            {
                _nextSequence = await _db.QueryFirstOrDefaultAsync<int?>(
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

            await _db.ExecuteAsync("""
                INSERT INTO messages (id, session_id, sequence_number, role, content, message_type, created_at)
                VALUES (@Id, @SessionId, @SequenceNumber, @Role, @Content, @MessageType, @CreatedAt)
                """, msg);

            await _db.ExecuteAsync(
                "UPDATE sessions SET last_message_at = @now WHERE id = @sessionId",
                new { sessionId, now = msg.CreatedAt });
        }

        /// <summary>
        /// Load recent conversation messages from the most recent session
        /// </summary>
        public async Task<List<Message>> LoadRecentConversationAsync(int limit = 10, int maxAgeHours = 24)
        {
            var cutoff = DateTime.UtcNow.AddHours(-maxAgeHours).ToString("o");

            var results = await _db.QueryAsync<Message>("""
                SELECT m.* FROM messages m
                INNER JOIN sessions s ON m.session_id = s.id
                WHERE m.message_type = 'conversation'
                  AND m.role IN ('user', 'assistant')
                  AND s.last_message_at > @cutoff
                ORDER BY m.created_at DESC, m.sequence_number DESC
                LIMIT @limit
                """, new { cutoff, limit });

            return results.Reverse().ToList();
        }

        /// <summary>
        /// Load messages from a specific session
        /// </summary>
        public async Task<List<Message>> LoadBySessionAsync(string sessionId, string messageType = MessageTypes.Conversation)
        {
            var results = await _db.QueryAsync<Message>("""
                SELECT * FROM messages
                WHERE session_id = @sessionId AND message_type = @messageType
                ORDER BY sequence_number ASC
                """, new { sessionId, messageType });

            return results.ToList();
        }
    }
}
