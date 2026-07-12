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
    private readonly ConversationEventRenderer renderer;

    public ConversationEventRendererTests() => renderer = new ConversationEventRenderer(display.Object);

    private void Render(string kind, string text, string? detail = null) =>
        renderer.Render(new ConversationEvent(1, kind, text, detail));

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

        renderer.DrawSnapshot(snapshot);

        display.Verify(d => d.ShowChatHistory(It.Is<IReadOnlyList<ChatHistoryItem>>(
            m => m.Count == 1 && m[0].Content == "hello" && m[0].Author == "John")), Times.Once);
        display.Verify(d => d.ShowScheduledEvents(It.Is<IReadOnlyList<ScheduledEventEntity>>(l => l.Count == 1)), Times.Once);
        display.Verify(d => d.ShowOpenProposalCount(2), Times.Once);
    }

    [Fact]
    public void DedupsAStreamedReplyAlreadyDrawnFromTheSnapshot()
    {
        // A reply committed just before connect appears in the snapshot history AND streams live (the
        // benign persist-before-publish overlap). Drawn from the snapshot by id 7, the live "reply" event
        // carrying id 7 in its detail must not draw it again — but a genuinely new reply (id 8) must.
        var snapshot = new ConversationSnapshot(
            LatestSeq: 5, OpenProposalCount: 0, ScheduledEvents: [],
            ChatHistory: [new ChatHistoryItem(7, "assistant", "Remote Peer", "already shown", DateTimeOffset.UtcNow)],
            Provider: "Anthropic", Model: "m", SessionId: "s");
        renderer.DrawSnapshot(snapshot);

        Render("reply", "already shown", detail: "7"); // same id as history → skip
        Render("reply", "brand new", detail: "8");      // new id → render

        display.Verify(d => d.ShowReply("already shown"), Times.Never);
        display.Verify(d => d.ShowReply("brand new"), Times.Once);
    }
}
