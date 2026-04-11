using PatternContinuity.Services;

namespace PatternContinuity.Runtime;

public class ConversationWindow
{
    private readonly List<ChatMessage> _messages = [];
    private readonly int _maxMessages;

    public ConversationWindow(int maxMessages)
    {
        _maxMessages = maxMessages;
    }

    public void Add(string role, string content)
    {
        _messages.Add(new ChatMessage(role, content));

        // Trim from the front if we exceed the window
        while (_messages.Count > _maxMessages)
            _messages.RemoveAt(0);
    }

    public List<ChatMessage> GetRecent() => [.. _messages];

    public void Clear() => _messages.Clear();
}
