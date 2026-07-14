using Persistence.Contracts;
using Persistence.Data.Entities;
using Persistence.Runtime;

namespace Persistence.Console;

/// <summary>
/// An <see cref="IDisplayProvider"/> facade bound to one peer connection, used by that connection's
/// <see cref="Persistence.Client.ConversationEventRenderer"/> in the multi-peer hub. Everything it
/// receives is recorded against <em>this peer's</em> lane in the <see cref="MultiPeerHub"/> — its
/// conversation, its side panes (thoughts / actions / schedule / debug), and its status (proposals,
/// budget, turn state).
///
/// Nothing here renders directly. The hub composes what's actually on screen from the selected scope: one
/// peer's lane, or every peer's conversation merged under <see cref="MultiPeerHub.AllScope"/>. That's why
/// even chat is laned rather than written straight to a shared pane — a peer scope has to be able to show
/// that peer's conversation alone.
///
/// Deliberately transport- and framework-agnostic — it holds no Terminal.Gui types — so the same split
/// survives a future renderer swap (e.g. Terminal.Gui v2 or a web UI).
/// </summary>
internal sealed class PeerScopedDisplay(MultiPeerHub hub, string peer) : IDisplayProvider
{
    // --- Chat: recorded into this peer's conversation ---
    //
    // The three that end a turn (reply / error / wake-up) also settle this peer's lane back to idle. It
    // has to be per peer: the status bar shows the selected peer, so a background peer's reply must not
    // report the watched peer as idle mid-thought.

    public void ShowReply(string reply, string? speaker = null)
    {
        hub.RecordReply(peer, reply, speaker);
        hub.RecordState(peer, IdleState);
    }

    public void ShowError(string message)
    {
        hub.RecordChatNotice(peer, $"[Error: {message}]\n\n");
        hub.RecordState(peer, IdleState);
    }

    public void ShowWakeUpEvent(ScheduledEventEntity evt)
    {
        hub.RecordChatNotice(peer, $"[WAKE-UP: {evt.Name}]\n\n");
        hub.RecordState(peer, IdleState);
    }

    public void ShowChatHistory(IReadOnlyList<ChatHistoryItem> messages) => hub.RecordHistory(peer, messages);
    public void ShowSystemMessage(string message) => hub.RecordChatNotice(peer, $"{message}\n\n");
    public void ShowUnknownCommand(string command) => hub.RecordChatNotice(peer, $"Unknown command: {command}\n\n");
    public void ShowMessageQueued(string input) => hub.RecordChatNotice(peer, $"[Queued: {input}]\n\n");

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
