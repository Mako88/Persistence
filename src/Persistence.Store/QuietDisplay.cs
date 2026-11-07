using Persistence.Contracts;
using Persistence.Data.Entities;
using Persistence.Runtime;

namespace Persistence.Store;

/// <summary>
/// The CLI has no conversation to render: its output is the command's own. Services that report
/// through a display (the embedding client's diagnostics) write to stderr only when asked to, so a
/// hook reading stdout gets the command's result and nothing else.
/// </summary>
internal sealed class QuietDisplay(bool debug) : IDisplayProvider
{
    public Task Start(CancellationToken ct) => Task.CompletedTask;
    public void ShowThinking(string? label = null) { }
    public void ShowReply(string reply, string? speaker = null) { }
    public void ShowReasoning(string summary) { }
    public void ShowReasoningDelta(string delta) { }
    public void ShowThought(string thought) { }
    public void ShowToolUse(string tool, string request, string result) { }
    public void ShowWakeUpEvent(ScheduledEventEntity evt) { }
    public void ShowScheduledEvents(IReadOnlyList<ScheduledEventEntity> events) { }
    public void ShowOpenProposalCount(int count) { }
    public void UpdateBudget(int usedTokens, int budgetTokens, int percentFull) { }
    public void ShowError(string message) => Console.Error.WriteLine(message);
    public void ShowDebugInfo(string info) { if (debug) Console.Error.WriteLine(info); }
    public void Stop() { }
    public void ShowChatHistory(IReadOnlyList<ChatHistoryItem> messages) { }
    public void ShowSystemMessage(string message) { }
    public void ShowUnknownCommand(string command) { }
    public void ShowMessageQueued(string input) { }
}
