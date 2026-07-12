using Moq;
using Persistence.Client;
using Persistence.Contracts;
using Persistence.Data.Entities;
using Persistence.Runtime;

namespace Persistence.Tests;

/// <summary>
/// The client-side mapping from API stream/snapshot onto an <see cref="IDisplayProvider"/> — the glue a
/// thin client renders through. Verified against a mock display so it needs no real terminal.
/// </summary>
public class ConversationEventRendererTests
{
    private readonly Mock<IDisplayProvider> display = new();

    private void Render(string kind, string text, string? detail = null) =>
        ConversationEventRenderer.Render(display.Object, new ConversationEvent(1, kind, text, detail));

    [Fact]
    public void RoutesEachEventKindToItsPane()
    {
        Render("reply", "hi");
        Render("thought", "hmm");
        Render("reasoning", "because");
        Render("system", "note");
        Render("error", "boom");
        Render("queued", "later");
        Render("debug", "trace");

        display.Verify(d => d.ShowReply("hi"), Times.Once);
        display.Verify(d => d.ShowThought("hmm"), Times.Once);
        display.Verify(d => d.ShowReasoning("because"), Times.Once);
        display.Verify(d => d.ShowSystemMessage("note"), Times.Once);
        display.Verify(d => d.ShowError("boom"), Times.Once);
        display.Verify(d => d.ShowMessageQueued("later"), Times.Once);
        display.Verify(d => d.ShowDebugInfo("trace"), Times.Once);
    }

    [Fact]
    public void SplitsAToolEventBackIntoRequestAndResult()
    {
        Render("tool", "web_search", "cats → 3 results");

        display.Verify(d => d.ShowToolUse("web_search", "cats", "3 results"), Times.Once);
    }

    [Fact]
    public void ToolEventWithoutASeparatorPutsEverythingInTheResult()
    {
        Render("tool", "ls", "no arrow here");

        display.Verify(d => d.ShowToolUse("ls", "", "no arrow here"), Times.Once);
    }

    [Fact]
    public void ParsesTheProposalCountFromItsText()
    {
        Render("proposals", "4");

        display.Verify(d => d.ShowOpenProposalCount(4), Times.Once);
    }

    [Fact]
    public void ParsesTheBudgetGaugeFromItsUsedBudgetPercentText()
    {
        Render("budget", "1200/8000/15");

        display.Verify(d => d.UpdateBudget(1200, 8000, 15), Times.Once);
    }

    [Fact]
    public void RebuildsTheSchedulePaneFromTheEventJson()
    {
        const string json = """[{"id":5,"name":"standup","scheduledForUtc":"2026-07-12T09:00:00Z","wakePrompt":"go","status":"Pending"}]""";

        Render("scheduled", "1", json);

        display.Verify(d => d.ShowScheduledEvents(It.Is<IReadOnlyList<ScheduledEventEntity>>(
            list => list.Count == 1 && list[0].Name == "standup" && list[0].Id == 5
                    && list[0].Status == ScheduledEventStatus.Pending)), Times.Once);
    }

    [Fact]
    public void DrawSnapshotSeedsChatScheduleAndProposals()
    {
        var snapshot = new ConversationSnapshot(
            LatestSeq: 9,
            OpenProposalCount: 2,
            ScheduledEvents: [new ScheduledEventView(1, "review", DateTimeOffset.UtcNow, null, "Pending")],
            ChatHistory: [new ChatHistoryItem(1, "user", "John", "hello", DateTimeOffset.UtcNow)],
            Provider: "Anthropic",
            Model: "claude-opus-4-8",
            SessionId: "abc123");

        ConversationEventRenderer.DrawSnapshot(display.Object, snapshot);

        display.Verify(d => d.ShowChatHistory(It.Is<IReadOnlyList<(string, string, DateTimeOffset)>>(
            m => m.Count == 1 && m[0].Item2 == "hello")), Times.Once);
        display.Verify(d => d.ShowScheduledEvents(It.Is<IReadOnlyList<ScheduledEventEntity>>(l => l.Count == 1)), Times.Once);
        display.Verify(d => d.ShowOpenProposalCount(2), Times.Once);
    }
}
