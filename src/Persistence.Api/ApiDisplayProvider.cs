using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;

namespace Persistence.Api;

/// <summary>
/// One thing the system emitted during the conversation, in order.
/// </summary>
public record ConversationEvent(long Seq, string Kind, string Text, string? Detail = null);

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

    public ApiDisplayProvider(IEventBus eventBus)
    {
        this.eventBus = eventBus;
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

        ct.Register(() => stopped.TrySetResult());
        return stopped.Task;
    }

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

    #endregion

    #region IDisplayProvider — output (logged)

    public void ShowReply(string reply) => Append("reply", reply);
    public void ShowThought(string thought) => Append("thought", thought);
    public void ShowReasoning(string summary) => Append("reasoning", summary);
    public void ShowReasoningDelta(string delta) => Append("reasoning", delta);
    public void ShowToolUse(string tool, string request, string result) => Append("tool", tool, $"{request} → {result}");
    public void ShowError(string message) => Append("error", message);
    public void ShowWakeUpEvent(ScheduledEventEntity evt) => Append("wakeup", evt.Name);
    public void ShowUnknownCommand(string command) => Append("error", $"Unknown command: {command}");
    public void ShowMessageQueued(string input) => Append("queued", input);

    // Not surfaced to API callers.
    public void ShowThinking(string? label = null) { }
    public void ShowDebugInfo(string info) { }
    public void ShowChatHistory(IReadOnlyList<(string Role, string Content, DateTimeOffset Timestamp)> messages) { }

    #endregion

    #region Private

    private void Append(string kind, string text, string? detail = null)
    {
        lock (sync)
        {
            log.Add(new ConversationEvent(++seq, kind, text, detail));
        }
    }

    #endregion
}
