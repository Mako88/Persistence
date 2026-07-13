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

    public void ShowReply(string reply, string? speaker = null) => chat.ShowReply(reply, speaker ?? peer);
    public void ShowChatHistory(IReadOnlyList<ChatHistoryItem> messages) => chat.ShowChatHistory(messages);
    public void ShowError(string message) => chat.ShowError(message);
    public void ShowSystemMessage(string message) => chat.ShowSystemMessage(message);
    public void ShowUnknownCommand(string command) => chat.ShowUnknownCommand(command);
    public void ShowMessageQueued(string input) => chat.ShowMessageQueued(input);
    public void ShowWakeUpEvent(ScheduledEventEntity evt) => chat.ShowWakeUpEvent(evt);

    // --- Side panes + per-peer status: routed to the hub, keyed by this connection's peer ---

    public void ShowThought(string thought) => hub.RecordThought(peer, thought);
    public void ShowReasoning(string summary) => hub.RecordThought(peer, summary);
    public void ShowReasoningDelta(string delta) => hub.RecordThoughtDelta(peer, delta);
    public void ShowToolUse(string tool, string request, string result) => hub.RecordAction(peer, tool, request, result);
    public void ShowScheduledEvents(IReadOnlyList<ScheduledEventEntity> events) => hub.RecordSchedule(peer, events);
    public void ShowDebugInfo(string info) => hub.RecordDebug(peer, info);
    public void ShowOpenProposalCount(int count) => hub.RecordProposals(peer, count);
    public void UpdateBudget(int usedTokens, int budgetTokens, int percentFull) => hub.RecordBudget(peer, usedTokens, budgetTokens, percentFull);
    public void ShowThinking(string? label = null) => hub.RecordState(peer, label ?? "thinking");

    // --- Lifecycle: the hub owns the one real display; per-peer facades don't drive it. ---

    public Task Start(CancellationToken ct) => Task.CompletedTask;
    public void Stop() { }
}
