using Persistence.Contracts;

namespace Persistence.Client;

/// <summary>
/// One utterance, prepared to be carried to another peer (ADR-0008 §4).
/// </summary>
/// <param name="Content">The original words, unchanged. A relay carries; it does not paraphrase.</param>
/// <param name="FromPeer">
/// The <em>original</em> speaker, never the person doing the relaying. This is the guardrail the whole
/// type exists to hold: a human forwarding Synth's words to Arden must have them arrive as Synth's.
/// <see langword="null"/> means a human originally said it, so it travels as a plain human message.
/// </param>
/// <param name="AddressedTo">The peer it's being carried to.</param>
/// <param name="MessageId">
/// The original utterance's cross-peer id, passed through unchanged so both stores record one utterance
/// under one identity (migration 007).
/// </param>
/// <param name="RelayDepth">The original's depth + 1 — this copy travelled one hop further.</param>
public record RelayedMessage(
    string Content,
    string? FromPeer,
    string? AddressedTo,
    string? MessageId,
    int RelayDepth);

/// <summary>
/// Builds the relay of an existing message from one peer's conversation to another (ADR-0008 §4).
///
/// <para>Deliberately a pure function over a <see cref="ChatHistoryItem"/> rather than logic inside the
/// TUI: the guardrails here are the substance of §4, and they must hold for any front-end that ever
/// offers a relay button — not just the one Terminal.Gui pane that has one today.</para>
/// </summary>
public static class RelayComposer
{
    /// <summary>
    /// Prepares <paramref name="original"/> to be carried to <paramref name="targetPeer"/>.
    ///
    /// <para>Three things are preserved rather than recomputed, and each is load-bearing:</para>
    /// <list type="bullet">
    /// <item><b>Attribution.</b> The message arrives as from whoever originally said it. Re-attributing it
    /// to the human who pressed the button would collapse the provenance the room is built on — the
    /// receiving peer would believe a person said something a peer said.</item>
    /// <item><b>Identity.</b> The cross-peer <c>MessageId</c> travels unchanged, so the receiving store
    /// records the same utterance under the same id rather than minting a second identity for it.</item>
    /// <item><b>Distance.</b> The hop depth is the original's + 1, read from the stored message rather
    /// than tracked by the relaying client — the message knows its own path (migration 007), and the
    /// breaker and the relayer must not drift apart on what that path was.</item>
    /// </list>
    /// </summary>
    /// <param name="original">The message as it was persisted, including its id and depth.</param>
    /// <param name="targetPeer">The peer it's being carried to.</param>
    public static RelayedMessage Compose(ChatHistoryItem original, string targetPeer)
    {
        ArgumentNullException.ThrowIfNull(original);

        if (string.IsNullOrWhiteSpace(targetPeer))
        {
            throw new ArgumentException("A relay needs a peer to carry the message to.", nameof(targetPeer));
        }

        // A human's own words aren't a relay at all — they're that person speaking, so they travel as a
        // human message and reset the hop chain, exactly as if typed. Only a peer's voice is carried.
        var spokenByPeer = IsPeerAuthored(original);

        return new RelayedMessage(
            Content: original.Content,
            FromPeer: spokenByPeer ? original.Author : null,
            AddressedTo: targetPeer.Trim(),
            MessageId: original.MessageId,
            // Depth counts peer-to-peer hops without a human turn in between, so a human's message
            // starts the chain over rather than continuing it (ADR-0008 §4).
            RelayDepth: spokenByPeer ? (original.RelayDepth ?? 0) + 1 : 0);
    }

    /// <summary>
    /// Whether a peer said this, as opposed to a person. Keyed on the role the store assigned rather than
    /// on the author's name: a peer and a person can share a name, and guessing from the name is exactly
    /// the misattribution the room's sender-<em>kind</em> distinction exists to prevent.
    /// </summary>
    private static bool IsPeerAuthored(ChatHistoryItem original) =>
        string.Equals(original.Role, "assistant", StringComparison.OrdinalIgnoreCase);
}
