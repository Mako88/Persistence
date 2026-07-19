using Persistence.Data.Entities;
using Persistence.Events;

namespace Persistence.Notifications;

/// <summary>
/// Published when input arrives for the peer — from a person at a client, or (in the room, ADR-0008)
/// relayed from another digital peer.
///
/// <para>Who sent it is structural rather than inferred from prose, because a human's message and a
/// peer's message carry different epistemic weight: a human carries the built trust relationship, a
/// peer carries a voice to weigh genuinely but not treat as ground truth (ADR-0008 §2). The receiving
/// peer gets that distinction from <see cref="SenderType"/> without having to read it out of the
/// text.</para>
/// </summary>
/// <param name="input">The message text.</param>
/// <param name="senderName">
/// Who is speaking. For a human, the local-peer name (e.g. from an API <c>X-Local-Peer</c> header);
/// null falls back to the configured <see cref="Config.IAppConfig.SelectedLocalPeer"/>. For a relayed
/// peer message, the sending peer's name.
/// </param>
/// <param name="senderType">
/// Whether a person or another digital peer is speaking. Defaults to <see cref="SourceType.HumanPeer"/>,
/// so every existing caller keeps its meaning.
/// </param>
/// <param name="addressedTo">
/// Who the message is directed at — a participant name, or null for a broadcast to the room. Lets the
/// receiving peer tell "addressed to me" from "overheard", which is a real cognitive difference
/// (ADR-0008 §2) and what its turn-taking rule keys on.
/// </param>
/// <param name="relayDepth">
/// How many peer-to-peer hops this message has already taken without a human speaking. 0 is a message
/// straight from a person. The circuit breaker in <c>TurnHandler</c> stops the chain once it passes the
/// configured limit, so two peers can't talk each other into an unbounded loop (ADR-0008 §4).
/// </param>
/// <param name="messageId">
/// The utterance's cross-peer identity, minted by whoever originally said it and passed through
/// unchanged by every relay. Null means this is a new utterance and the turn mints one — so the id
/// exists from the moment a message is persisted, whether or not it ever crosses a peer boundary.
/// </param>
public class DisplayInputReceived(
    string? input,
    string? senderName = null,
    SourceType senderType = SourceType.HumanPeer,
    string? addressedTo = null,
    int relayDepth = 0,
    string? messageId = null) : BaseEvent
{
    public string? MessageId { get; } = messageId;

    public string? Input { get; } = input;

    public string? SenderName { get; } = senderName;

    public SourceType SenderType { get; } = senderType;

    public string? AddressedTo { get; } = addressedTo;

    public int RelayDepth { get; } = relayDepth;

    /// <summary>True when another digital peer is speaking rather than a person.</summary>
    public bool FromPeer => SenderType == SourceType.DigitalPeer;

    /// <summary>
    /// The old name for <see cref="SenderName"/>, kept so existing call sites and tests that speak in
    /// terms of "the local peer" still read correctly.
    /// </summary>
    public string? LocalPeerName => SenderName;
}
