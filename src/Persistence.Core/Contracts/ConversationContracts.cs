namespace Persistence.Contracts;

/// <summary>
/// The wire contracts shared between the API server and its clients (the thin-client Console, the web
/// client). Kept in Core — referenced by every project — so server and client speak one definition.
/// </summary>
/// <remarks>
/// One item the system emitted during the conversation, in order. <see cref="Seq"/> is a monotonic
/// per-process sequence number a client tracks to resume without gaps or duplicates.
/// </remarks>
public record ConversationEvent(long Seq, string Kind, string Text, string? Detail = null);

/// <summary>A pending scheduled event, slimmed for a client's Schedule pane.</summary>
public record ScheduledEventView(long Id, string Name, DateTimeOffset ScheduledForUtc, string? WakePrompt, string Status);

/// <summary>
/// One prior conversation message, for a freshly-connected client to draw before live events.
/// <see cref="Id"/> is the persisted fragment id — a stable key a client uses to reconcile a message
/// that appears in both this backfill and the live stream (so the benign snapshot/stream overlap dedups).
/// <see cref="Author"/> is who sent it (a peer's name), for attributed multi-peer display; <see cref="Role"/>
/// stays the coarse user/assistant distinction for role-based rendering.
/// </summary>
/// <remarks>
/// <see cref="Id"/> and <see cref="MessageId"/> are two ids with two jobs, and the difference matters to a
/// client that relays. <see cref="Id"/> is this store's row id — meaningless in any other peer's store.
/// <see cref="MessageId"/> is the utterance's cross-peer identity (ADR-0008): a relaying client passes it
/// on unchanged so the receiving peer records the same utterance under the same id, alongside
/// <see cref="RelayDepth"/> + 1. Both are null for messages persisted before migration 007.
/// </remarks>
public record ChatHistoryItem(long Id, string Role, string Author, string Content, DateTimeOffset Timestamp,
    string? MessageId = null, int? RelayDepth = null);

/// <summary>
/// The state a newly-connected client needs to draw before subscribing to the live stream: the standing
/// snapshots (pending scheduled events, open-proposal count, recent chat) plus <see cref="LatestSeq"/>,
/// which the client passes to <c>?since=</c> so the stream resumes with no gap and no duplicate.
/// </summary>
public record ConversationSnapshot(
    long LatestSeq,
    int OpenProposalCount,
    IReadOnlyList<ScheduledEventView> ScheduledEvents,
    IReadOnlyList<ChatHistoryItem> ChatHistory,
    string Provider,
    string Model,
    string SessionId);
