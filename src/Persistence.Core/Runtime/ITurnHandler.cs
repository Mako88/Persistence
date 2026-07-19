using Persistence.Data.Entities;
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
    /// <param name="senderType">Whether a person or another digital peer is speaking (ADR-0008 §2).</param>
    /// <param name="addressedTo">Who the message is directed at; null is a broadcast to the room.</param>
    /// <param name="relayDepth">Peer-to-peer hops taken so far without a human speaking (ADR-0008 §4).</param>
    /// <param name="messageId">
    /// The utterance's cross-peer id, passed through unchanged when this is a relay of something already
    /// said. Null mints a new one — a fresh utterance.
    /// </param>
    Task ExecuteTurnAsync(string? input = null, string? peerName = null, string? wakeNote = null,
        SourceType senderType = SourceType.HumanPeer, string? addressedTo = null, int relayDepth = 0,
        string? messageId = null, CancellationToken ct = default);

    /// <summary>
    /// Queues input from a human peer (with the sender's name, so attribution survives the wait until
    /// it's drained) to be injected into the working context before the next model call within the
    /// current turn's iteration loop.
    /// </summary>
    void EnqueueInput(string input, string? peerName = null,
        SourceType senderType = SourceType.HumanPeer, string? addressedTo = null,
        string? messageId = null, int relayDepth = 0);

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
