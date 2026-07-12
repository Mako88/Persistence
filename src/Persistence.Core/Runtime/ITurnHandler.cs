namespace Persistence.Runtime;

/// <summary>
/// Processes a single conversational turn — builds the prompt from the current working
/// context, calls the model, persists the exchange, and publishes downstream events.
/// </summary>
public interface ITurnHandler
{
    /// <summary>
    /// Executes a full turn. When <paramref name="input"/> is provided, it is persisted as the
    /// initial message, attributed to <paramref name="peerName"/> (the human who sent it; null uses
    /// the configured default). When null, pending queued input is drained as the starting context
    /// instead. When <paramref name="wakeNote"/> is provided (an autonomous wake-up), it is injected
    /// as a transient system note so the turn runs with that framing but without a human-peer message.
    /// </summary>
    Task ExecuteTurnAsync(string? input = null, string? peerName = null, string? wakeNote = null, CancellationToken ct = default);

    /// <summary>
    /// Queues input from a human peer (with the sender's name, so attribution survives the wait until
    /// it's drained) to be injected into the working context before the next model call within the
    /// current turn's iteration loop.
    /// </summary>
    void EnqueueInput(string input, string? peerName = null);

    /// <summary>
    /// Queues a system note (e.g. the local peer accepted/rejected a proposal) to surface to the
    /// peer as transient context at the start of its next turn. Not attributed to the local peer.
    /// </summary>
    void EnqueueSystemNote(string note);

    /// <summary>
    /// Whether there are any pending input messages waiting to be processed.
    /// </summary>
    bool HasPendingInput { get; }
}
