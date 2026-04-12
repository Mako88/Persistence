using Persistence.Models;

namespace Persistence.Data.Repositories
{
    /// <summary>
    /// Repository for conversation messages
    /// </summary>
    public interface IMessageRepository
    {
        /// <summary>
        /// Store a new message in the database
        /// </summary>
        Task StoreAsync(string sessionId, string role, string content, string messageType = MessageTypes.Conversation);

        /// <summary>
        /// Load recent conversation messages from the most recent session
        /// </summary>
        Task<List<Message>> LoadRecentConversationAsync(int limit = 10, int maxAgeHours = 24);

        /// <summary>
        /// Load messages from a specific session
        /// </summary>
        Task<List<Message>> LoadBySessionAsync(string sessionId, string messageType = MessageTypes.Conversation);
    }
}
