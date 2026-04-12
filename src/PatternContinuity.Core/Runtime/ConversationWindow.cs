using PatternContinuity.Data;
using PatternContinuity.Models;
using PatternContinuity.Services;

namespace PatternContinuity.Runtime;

public class ConversationWindow
{
    private readonly List<ChatMessage> _messages = [];
    private readonly MessageRepository _messageRepo;
    private readonly string _sessionId;
    private readonly int _maxMessages;

    public ConversationWindow(MessageRepository messageRepo, string sessionId, int maxMessages)
    {
        _messageRepo = messageRepo;
        _sessionId = sessionId;
        _maxMessages = maxMessages;
    }

    /// <summary>
    /// Warm the conversation window from the most recent persisted messages.
    /// Only loads user/assistant conversation messages, not reflection or system noise.
    /// Only loads if the most recent session had activity within maxAgeHours.
    /// </summary>
    public int WarmFromHistory(int maxAgeHours = 24)
    {
        var recent = _messageRepo.LoadRecentConversation(_maxMessages, maxAgeHours);
        foreach (var msg in recent)
            _messages.Add(new ChatMessage(msg.Role, msg.Content));
        return recent.Count;
    }

    public void Add(string role, string content, string messageType = MessageTypes.Conversation)
    {
        // Only add conversation messages to the in-memory window for replay
        if (messageType == MessageTypes.Conversation)
        {
            _messages.Add(new ChatMessage(role, content));

            // Trim from the front if we exceed the window
            while (_messages.Count > _maxMessages)
                _messages.RemoveAt(0);
        }

        // Persist all types to DB for audit trail
        _messageRepo.Store(_sessionId, role, content, messageType);
    }

    public List<ChatMessage> GetRecent() => [.. _messages];

    public void Clear() => _messages.Clear();
}
