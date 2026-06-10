using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;

namespace Persistence.Runtime;

/// <summary>
/// A no-op <see cref="IDisplayProvider"/> for headless runs (e.g. the scheduled wake-runner, which
/// fires due events and exits with no UI). Nothing is rendered — the turn's effects persist to the
/// store, which is all a headless wake needs. <see cref="Start"/> completes immediately; the
/// wake-cycle path never awaits it.
/// </summary>
[Singleton(typeof(IDisplayProvider), UiMode.Headless)]
public class HeadlessDisplayProvider : IDisplayProvider
{
    /// <inheritdoc />
    public Task Start(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public void Stop() { }

    public void ShowThinking(string? label = null) { }
    public void ShowReply(string reply) { }
    public void ShowReasoning(string summary) { }
    public void ShowReasoningDelta(string delta) { }
    public void ShowThought(string thought) { }
    public void ShowToolUse(string tool, string request, string result) { }
    public void ShowWakeUpEvent(ScheduledEventEntity evt) { }
    public void ShowScheduledEvents(IReadOnlyList<ScheduledEventEntity> events) { }
    public void ShowOpenProposalCount(int count) { }
    public void ShowError(string message) { }
    public void ShowDebugInfo(string info) { }
    public void ShowChatHistory(IReadOnlyList<(string Role, string Content, DateTimeOffset Timestamp)> messages) { }
    public void ShowSystemMessage(string message) { }
    public void ShowUnknownCommand(string command) { }
    public void ShowMessageQueued(string input) { }
}
