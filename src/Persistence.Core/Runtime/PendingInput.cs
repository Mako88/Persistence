namespace Persistence.Runtime;

/// <summary>
/// A single queued human-peer message together with who sent it. Attribution has to travel *with*
/// the message: turns are serialized, so several people (John, a colleague, later another client)
/// can post while one turn is running, and their messages queue up. If the sender's identity lived
/// only in shared session state — set when the message arrived, read when it's finally processed —
/// a later arrival would overwrite it and the queued message would be misattributed. Carrying the
/// name on the item itself keeps each message's author correct no matter when it drains. A null or
/// empty <see cref="PeerName"/> falls back to the configured default at persist time. See ADR-0007.
/// </summary>
public record PendingInput(
    string Content,
    string? PeerName,
    Data.Entities.SourceType SenderType = Data.Entities.SourceType.HumanPeer,
    string? AddressedTo = null);
