using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Contracts;
using Persistence.Runtime;
using System.Text.Json;
using System.Threading.Channels;

namespace Persistence.Api;

/// <summary>
/// <see cref="IDisplayProvider"/> for the API front-end. Instead of rendering, it appends every
/// emitted item to a sequence-numbered rolling log that clients read by polling
/// <c>?since=&lt;seq&gt;</c>. This one shape serves both a real model (turns complete on their
/// own) and an out-of-band remote peer (turns park on the broker until answered), and is the
/// direct precursor to SSE streaming — push instead of poll.
/// </summary>
[Singleton(typeof(IDisplayProvider), UiMode.Api)]
[Singleton(typeof(ApiDisplayProvider))]
public class ApiDisplayProvider : IDisplayProvider
{
    private readonly IEventBus eventBus;

    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object sync = new();
    private readonly List<ConversationEvent> log = [];

    private long seq;

    // Latest standing snapshots pushed by the orchestrator (guarded by `sync`). The event log carries
    // incremental output; these carry current whole-state that a fresh client needs on connect. Chat
    // history is NOT held here — it's queried fresh on connect (see IConversationHistoryProvider).
    private IReadOnlyList<ScheduledEventView> scheduledEvents = [];
    private int openProposalCount;

    /// <summary>
    /// Raised after a new event is appended, so the SSE endpoint can push it live. Handlers run
    /// outside the lock and must not throw. Polling via <see cref="EventsSince"/> still works for
    /// clients that prefer it; streaming is an additional consumer of the same log.
    /// </summary>
    public event Action<ConversationEvent>? EventAppended;

    private readonly IAppConfig config;
    private readonly ISessionContext sessionContext;

    /// <summary>
    /// Constructor
    /// </summary>
    public ApiDisplayProvider(IEventBus eventBus, IAppConfig config, ISessionContext sessionContext)
    {
        this.eventBus = eventBus;
        this.config = config;
        this.sessionContext = sessionContext;
    }

    #region Lifecycle

    /// <summary>
    /// Subscribes to turn events and returns a task that stays alive for the host's lifetime
    /// (completed on cancellation). The web host, not this provider, drives the process loop.
    /// </summary>
    public Task Start(CancellationToken ct)
    {
        eventBus.Subscribe<RemotePeerReplied>((_, e) => { ShowReply(e.Reply); return Task.CompletedTask; });
        eventBus.Subscribe<ModelThought>((_, e) => { ShowThought(e.Thought); return Task.CompletedTask; });
        eventBus.Subscribe<ToolInvoked>((_, e) => { ShowToolUse(e.Tool, e.Request, e.Result); return Task.CompletedTask; });
        eventBus.Subscribe<ModelReasoningDelta>((_, e) => { ShowReasoningDelta(e.Delta); return Task.CompletedTask; });
        eventBus.Subscribe<ScheduledEventTriggered>((_, e) => { ShowWakeUpEvent(e.Event); return Task.CompletedTask; });
        eventBus.Subscribe<ContextBudgetUpdated>((_, e) => { UpdateBudget(e.UsedTokens, e.BudgetTokens, e.PercentFull); return Task.CompletedTask; });

        ct.Register(() => stopped.TrySetResult());
        return stopped.Task;
    }

    /// <summary>
    /// Completes the lifetime task so the host can shut down. Idempotent.
    /// </summary>
    public void Stop() => stopped.TrySetResult();

    #endregion

    #region Log access

    /// <summary>Returns events with sequence greater than <paramref name="since"/>, in order.</summary>
    public IReadOnlyList<ConversationEvent> EventsSince(long since)
    {
        lock (sync)
        {
            return log.Where(e => e.Seq > since).ToList();
        }
    }

    /// <summary>The highest sequence number emitted so far.</summary>
    public long LatestSeq
    {
        get { lock (sync) { return seq; } }
    }

    /// <summary>
    /// Streams events after <paramref name="since"/>, in order, until <paramref name="ct"/> is
    /// cancelled (client disconnect). Subscribes to live events *before* snapshotting the backlog
    /// so nothing appended during replay is missed, then dedups by sequence — so each event is
    /// yielded exactly once whether it landed before or during the subscription.
    /// </summary>
    public async IAsyncEnumerable<ConversationEvent> StreamAsync(
        long since, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<ConversationEvent>(
            new UnboundedChannelOptions { SingleReader = true });

        void OnAppended(ConversationEvent e) => channel.Writer.TryWrite(e);

        EventAppended += OnAppended;
        try
        {
            // Replay the backlog captured after the subscription is live.
            IReadOnlyList<ConversationEvent> backlog;
            lock (sync)
            {
                backlog = log.Where(e => e.Seq > since).ToList();
            }

            var lastSeq = since;
            foreach (var e in backlog)
            {
                yield return e;
                lastSeq = e.Seq;
            }

            // Then live events, skipping any the backlog already covered.
            await foreach (var e in channel.Reader.ReadAllAsync(ct))
            {
                if (e.Seq > lastSeq)
                {
                    yield return e;
                    lastSeq = e.Seq;
                }
            }
        }
        finally
        {
            EventAppended -= OnAppended;
        }
    }

    #endregion

    #region IDisplayProvider — output (logged)

    /// <summary>
    /// Appends the remote peer's reply text to the log as a "reply" event
    /// </summary>
    public void ShowReply(string reply) => Append("reply", reply);

    /// <summary>
    /// Appends an open thought to the log as a "thought" event
    /// </summary>
    public void ShowThought(string thought) => Append("thought", thought);

    /// <summary>
    /// Appends the model's reasoning summary to the log as a "reasoning" event
    /// </summary>
    public void ShowReasoning(string summary) => Append("reasoning", summary);

    /// <summary>
    /// Appends a streamed chunk of the reasoning summary to the log as a "reasoning" event
    /// </summary>
    public void ShowReasoningDelta(string delta) => Append("reasoning", delta);

    /// <summary>
    /// Appends a tool/command invocation to the log as a "tool" event, with the request and result as detail
    /// </summary>
    public void ShowToolUse(string tool, string request, string result) => Append("tool", tool, $"{request} → {result}");

    /// <summary>
    /// Appends an error message to the log as an "error" event
    /// </summary>
    public void ShowError(string message) => Append("error", message);

    /// <summary>
    /// Appends a wake-up event notification to the log as a "wakeup" event
    /// </summary>
    public void ShowWakeUpEvent(ScheduledEventEntity evt) => Append("wakeup", evt.Name);

    /// <summary>
    /// Captures the pending-events snapshot (for <see cref="Snapshot"/>) and emits a "scheduled" event
    /// carrying it, so streaming clients keep their Schedule pane live. Pushed only on change, so it
    /// doesn't spam the log.
    /// </summary>
    public void ShowScheduledEvents(IReadOnlyList<ScheduledEventEntity> events)
    {
        var views = events
            .Select(e => new ScheduledEventView(e.Id, e.Name, e.ScheduledForUtc, e.WakePrompt, e.Status.ToString()))
            .ToList();

        lock (sync)
        {
            scheduledEvents = views;
        }

        Append("scheduled", views.Count.ToString(), JsonSerializer.Serialize(views));
    }

    /// <summary>
    /// Captures the open-proposal count (for <see cref="Snapshot"/>) and emits a "proposals" event so
    /// streaming clients keep their status affordance live.
    /// </summary>
    public void ShowOpenProposalCount(int count)
    {
        lock (sync)
        {
            openProposalCount = count;
        }

        Append("proposals", count.ToString());
    }

    /// <summary>
    /// Appends a system/local message (e.g. a slash-command result) to the log as a "system" event
    /// </summary>
    public void ShowSystemMessage(string message) => Append("system", message);

    /// <summary>
    /// Appends an unrecognised slash-command message to the log as an "error" event
    /// </summary>
    public void ShowUnknownCommand(string command) => Append("error", $"Unknown command: {command}");

    /// <summary>
    /// Appends a notification that a message has been queued for the next iteration as a "queued" event
    /// </summary>
    public void ShowMessageQueued(string input) => Append("queued", input);

    /// <summary>
    /// Emits a transient "thinking" status event (e.g. "waking", "processing queued messages"). Clients
    /// treat it as an ephemeral indicator — the next real event supersedes it.
    /// </summary>
    public void ShowThinking(string? label = null) => Append("thinking", label ?? "thinking");

    /// <summary>
    /// Appends debug info (e.g. the assembled request and raw model output) to the log as a
    /// "debug" event. Only called when DebugMode is enabled, so it stays silent in normal runs.
    /// </summary>
    public void ShowDebugInfo(string info) => Append("debug", info);

    /// <summary>
    /// No-op on the API surface: chat history isn't maintained here, it's queried fresh when a client
    /// connects (<see cref="IConversationHistoryProvider"/>). Clients render it via this same method.
    /// </summary>
    public void ShowChatHistory(IReadOnlyList<(string Role, string Content, DateTimeOffset Timestamp)> messages) { }

    /// <summary>
    /// The current standing state for a newly-connected client to draw before subscribing to the stream
    /// at <see cref="ConversationSnapshot.LatestSeq"/> (so no event is missed or replayed). Chat history
    /// is supplied by the caller, queried fresh from the store.
    /// </summary>
    public ConversationSnapshot Snapshot(IReadOnlyList<ChatHistoryItem> chatHistory)
    {
        lock (sync)
        {
            return new ConversationSnapshot(
                seq, openProposalCount, scheduledEvents, chatHistory,
                config.Provider, config.Model, sessionContext.SessionId);
        }
    }

    /// <summary>
    /// Emits a "budget" event carrying the context-window usage, so a streaming client keeps its budget
    /// gauge live. Encoded as used/budget/percent for the client to parse.
    /// </summary>
    public void UpdateBudget(int usedTokens, int budgetTokens, int percentFull) =>
        Append("budget", $"{usedTokens}/{budgetTokens}/{percentFull}");

    #endregion

    #region Private

    private void Append(string kind, string text, string? detail = null)
    {
        ConversationEvent evt;

        lock (sync)
        {
            evt = new ConversationEvent(++seq, kind, text, detail);
            log.Add(evt);
        }

        // Notify outside the lock so a slow/streaming subscriber can't stall event production.
        EventAppended?.Invoke(evt);
    }

    #endregion
}
