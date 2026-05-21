using Persistence.Events;

namespace Persistence.Notifications;

/// <summary>
/// Raised after the model responds and the reply has been persisted. Subscribers
/// should display the reply to the user.
/// </summary>
public class DigitalColleagueReplied(string reply) : BaseEvent
{
    /// <summary>The model's reply text</summary>
    public string Reply { get; } = reply;
}
