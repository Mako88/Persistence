using Persistence.Events;

namespace Persistence.Notifications;

/// <summary>
/// Raised while streaming a model response, carrying an incremental chunk of the
/// reasoning summary. Subscribers append it to the reasoning view as it arrives.
/// </summary>
public class ModelReasoningDelta(string delta) : BaseEvent
{
    /// <summary>
    /// The incremental reasoning-summary text.
    /// </summary>
    public string Delta { get; } = delta;
}
