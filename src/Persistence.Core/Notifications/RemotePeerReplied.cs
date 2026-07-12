using Persistence.Events;

namespace Persistence.Notifications;

/// <summary>
/// Raised after the model responds and the reply has been persisted. Subscribers
/// should display the reply to the human peer.
/// </summary>
public class RemotePeerReplied(string reply, long? messageId = null) : BaseEvent
{
    /// <summary>
    /// The model's reply text
    /// </summary>
    public string Reply { get; } = reply;

    /// <summary>
    /// The persisted ChatMessage fragment id for this reply, when it corresponds to a stored message
    /// (a real reply). A client uses it to reconcile the same message arriving via both the connect-time
    /// snapshot and the live stream. Null for system notices (parse errors, turn-ended markers) that
    /// aren't persisted as chat.
    /// </summary>
    public long? MessageId { get; } = messageId;
}
