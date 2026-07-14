using Persistence.Contracts;
using Persistence.Data.Entities;
using Persistence.Runtime;

namespace Persistence.Console;

/// <summary>
/// An <see cref="IDisplayProvider"/> facade bound to one peer connection, used by that connection's
/// <see cref="Persistence.Client.ConversationEventRenderer"/> in the multi-peer hub. It splits the
/// render surface in two:
///
/// <list type="bullet">
/// <item><b>Chat</b> (replies, history, errors, system/queued messages, wake-ups) goes straight to the
/// shared conversation pane — every peer's messages aggregate into one attributed scrollback.</item>
/// <item><b>Side panes</b> (thoughts, actions, schedule, debug) and the per-peer status (proposals,
/// budget, thinking state) are routed to the <see cref="MultiPeerHub"/>, which buffers them per peer so
/// the selector can switch which peer the side column shows.</item>
/// </list>
///
/// This is deliberately transport- and framework-agnostic — it holds no Terminal.Gui types — so the
/// same split survives a future renderer swap (e.g. Terminal.Gui v2 or a web UI).
/// </summary>
internal sealed class PeerScopedDisplay(MultiPeerHub hub, string peer, IDisplayProvider chat) : IDisplayProvider
{
    // --- Chat: aggregated, straight through to the shared conversation pane ---
    //
    // The three that end a turn (reply / error / wake-up) also settle *this* peer's lane back to idle.
    // The shared chat surface can't do it: it hears every peer, but the status bar shows only the
    // selected one, so a reply from a background peer would report the watched peer as idle mid-thought.

    public void ShowReply(string reply, string? speaker = null)
    {
        chat.ShowReply(reply, speaker ?? peer);
        hub.RecordState(peer, IdleState);
    }

    public void ShowError(string message)
    {
        chat.ShowError(message);
        hub.RecordState(peer, IdleState);
    }

    public void ShowWakeUpEvent(ScheduledEventEntity evt)
    {
        chat.ShowWakeUpEvent(evt);
        hub.RecordState(peer, IdleState);
    }

    public void ShowChatHistory(IReadOnlyList<ChatHistoryItem> messages) => chat.ShowChatHistory(messages);
    public void ShowSystemMessage(string message) => chat.ShowSystemMessage(message);
    public void ShowUnknownCommand(string command) => chat.ShowUnknownCommand(command);
    public void ShowMessageQueued(string input) => chat.ShowMessageQueued(input);

    // --- Side panes + per-peer status: routed to the hub, keyed by this connection's peer ---

    public void ShowThought(string thought) => hub.RecordThought(peer, thought);
    public void ShowReasoning(string summary) => hub.RecordThought(peer, summary);
    public void ShowReasoningDelta(string delta) => hub.RecordThoughtDelta(peer, delta);
    public void ShowToolUse(string tool, string request, string result) => hub.RecordAction(peer, tool, request, result);
    public void ShowScheduledEvents(IReadOnlyList<ScheduledEventEntity> events) => hub.RecordSchedule(peer, events);
    public void ShowDebugInfo(string info) => hub.RecordDebug(peer, info);
    public void ShowOpenProposalCount(int count) => hub.RecordProposals(peer, count);
    public void UpdateBudget(int usedTokens, int budgetTokens, int percentFull) => hub.RecordBudget(peer, usedTokens, budgetTokens, percentFull);

    /// <summary>
    /// Records this peer as busy. The trailing ellipsis is load-bearing, not decoration: it's how the
    /// status bar tells a working state from a settled one (and so colours the chip green vs. gray). The
    /// single-peer path appends it at the point of display, so a lane that stored the bare label would
    /// leave the chip permanently gray no matter what the peer was doing.
    /// </summary>
    public void ShowThinking(string? label = null) => hub.RecordState(peer, $"{label ?? "thinking"}…");

    /// <summary>The settled state — matches the single-peer path's wording, and carries no ellipsis.</summary>
    private const string IdleState = "idle";

    // --- Lifecycle: the hub owns the one real display; per-peer facades don't drive it. ---

    public Task Start(CancellationToken ct) => Task.CompletedTask;
    public void Stop() { }
}
