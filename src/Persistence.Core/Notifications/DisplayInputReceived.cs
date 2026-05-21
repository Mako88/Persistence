using Persistence.Events;

namespace Persistence.Notifications;

/// <summary>
/// Published by the display provider when the user submits input
/// </summary>
public class DisplayInputReceived(string? input) : BaseEvent
{
    public string? Input { get; } = input;
}
