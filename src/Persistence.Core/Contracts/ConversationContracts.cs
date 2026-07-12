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

/// <summary>One prior conversation message, for a freshly-connected client to draw before live events.</summary>
public record ChatHistoryItem(string Role, string Content, DateTimeOffset Timestamp);

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
