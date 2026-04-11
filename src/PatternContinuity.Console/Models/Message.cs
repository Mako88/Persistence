namespace PatternContinuity.Models;

public class Message
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public int SequenceNumber { get; set; }
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string MessageType { get; set; } = MessageTypes.Conversation;
    public string CreatedAt { get; set; } = "";
}

public static class MessageTypes
{
    public const string Conversation = "conversation";
    public const string Reflection = "reflection";
    public const string System = "system";
    public const string Wake = "wake";
}
