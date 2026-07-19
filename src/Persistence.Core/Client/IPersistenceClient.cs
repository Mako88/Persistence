using Persistence.Contracts;

namespace Persistence.Client;

/// <summary>
/// A thin client's view of a running Persistence API server: submit local-peer input, fetch the
/// connect-time snapshot, and stream conversation events live. This is the transport the Console's
/// client mode (ADR-0006) drives its TUI from — no database, pipeline, or model in the client.
/// </summary>
public interface IPersistenceClient
{
    /// <summary>Submits input as the local peer this client identifies as (set at construction).</summary>
    Task SendAsync(string input, CancellationToken ct = default);

    /// <summary>
    /// Carries another peer's message to this one (ADR-0008 §4). Distinct from <see cref="SendAsync"/>
    /// because the sender is <em>not</em> this client's local peer: the message arrives attributed to
    /// whoever originally said it, with its identity and hop depth intact. Build the argument with
    /// <see cref="RelayComposer.Compose"/> rather than by hand — that's where the guardrails live.
    /// </summary>
    Task RelayAsync(RelayedMessage message, CancellationToken ct = default);

    /// <summary>Fetches the state to draw on connect (scheduled events, proposal count, chat, resume seq).</summary>
    Task<ConversationSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams conversation events after <paramref name="since"/> (Server-Sent Events), replaying any
    /// backlog first, then live, until cancelled. Pair with <see cref="GetSnapshotAsync"/>: draw the
    /// snapshot, then stream from its <see cref="ConversationSnapshot.LatestSeq"/> for a gapless resume.
    /// </summary>
    IAsyncEnumerable<ConversationEvent> StreamAsync(long since = 0, CancellationToken ct = default);
}
