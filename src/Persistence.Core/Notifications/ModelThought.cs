using Persistence.Events;

namespace Persistence.Notifications;

/// <summary>
/// Raised when the remote peer records an open thought via a Think action. Distinct from
/// <see cref="ModelReasoningDelta"/> (the provider's streamed built-in reasoning): this is a
/// deliberate, complete thought the remote peer chose to externalize into its context.
/// </summary>
public class ModelThought(string thought) : BaseEvent
{
    /// <summary>
    /// The thought text the remote peer recorded.
    /// </summary>
    public string Thought { get; } = thought;
}
