using Persistence.Data.Repositories;
using Persistence.Models;
using Persistence.Services;

namespace Persistence.Runtime
{
    /// <summary>
    /// Manages the in-memory conversation window and persists messages to the database
    /// </summary>
    public class ConversationWindow
    {
        private readonly List<ChatMessage> _messages = [];
        private readonly IMessageRepository _messageRepo;
        private readonly string _sessionId;
        private readonly int _maxMessages;

        /// <summary>
        /// Constructor
        /// </summary>
        public ConversationWindow(IMessageRepository messageRepo, string sessionId, int maxMessages)
        {
            _messageRepo = messageRepo;
            _sessionId = sessionId;
            _maxMessages = maxMessages;
        }

        /// <summary>
        /// Warm the conversation window from the most recent persisted messages
        /// </summary>
        public async Task<int> WarmFromHistoryAsync(int maxAgeHours = 24)
        {
            var recent = await _messageRepo.LoadRecentConversationAsync(_maxMessages, maxAgeHours);
            foreach (var msg in recent)
                _messages.Add(new ChatMessage(msg.Role, msg.Content));
            return recent.Count;
        }

        /// <summary>
        /// Add a message to the window and persist it to the database
        /// </summary>
        public async Task AddAsync(string role, string content, string messageType = MessageTypes.Conversation)
        {
            if (messageType == MessageTypes.Conversation)
            {
                _messages.Add(new ChatMessage(role, content));

                while (_messages.Count > _maxMessages)
                    _messages.RemoveAt(0);
            }

            await _messageRepo.StoreAsync(_sessionId, role, content, messageType);
        }

        /// <summary>
        /// Get the recent messages in the conversation window
        /// </summary>
        public List<ChatMessage> GetRecent() => [.. _messages];

        /// <summary>
        /// Clear the in-memory conversation window
        /// </summary>
        public void Clear() => _messages.Clear();
    }
}
