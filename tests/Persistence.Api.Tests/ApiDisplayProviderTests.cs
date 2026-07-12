using Persistence.Data.Entities;
using Persistence.Config;
using Persistence.Contracts;
using Persistence.Events;
using Persistence.Runtime;

namespace Persistence.Api.Tests;

/// <summary>
/// The API display provider is the front-end/back-end seam for a thin client: incremental output goes to
/// the sequence-numbered event log (streamed), while standing whole-state (pending scheduled events,
/// open-proposal count, recent chat) is captured for a connect-time snapshot. These lock down that split.
/// </summary>
public class ApiDisplayProviderTests
{
    private static ScheduledEventEntity Event(long id, string name) => new()
    {
        Id = id,
        Name = name,
        WorkingContextId = 1,
        ScheduledForUtc = DateTimeOffset.UtcNow.AddHours(1),
        Status = ScheduledEventStatus.Pending,
        WakePrompt = "wake",
        CreatedUtc = DateTimeOffset.UtcNow,
        LastModifiedUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void ScheduledEventsPopulateTheSnapshotAndEmitALiveEvent()
    {
        var display = new ApiDisplayProvider(new EventBus(), new AppConfig(), new SessionContext());

        display.ShowScheduledEvents([Event(1, "standup"), Event(2, "review")]);

        var snap = display.Snapshot(display.LatestSeq, []);
        Assert.Equal(2, snap.ScheduledEvents.Count);
        Assert.Contains(snap.ScheduledEvents, e => e.Name == "standup");
        // ...and a live "scheduled" event so a subscribed client's Schedule pane updates without re-fetching.
        Assert.Contains(display.EventsSince(0), e => e.Kind == "scheduled");
    }

    [Fact]
    public void OpenProposalCountPopulatesTheSnapshotAndEmitsALiveEvent()
    {
        var display = new ApiDisplayProvider(new EventBus(), new AppConfig(), new SessionContext());

        display.ShowOpenProposalCount(3);

        Assert.Equal(3, display.Snapshot(display.LatestSeq, []).OpenProposalCount);
        Assert.Contains(display.EventsSince(0), e => e.Kind == "proposals" && e.Text == "3");
    }

    [Fact]
    public void SnapshotReturnsTheChatHistoryItIsGivenAndDoesNotCaptureShowChatHistory()
    {
        var display = new ApiDisplayProvider(new EventBus(), new AppConfig(), new SessionContext());

        // ShowChatHistory is a no-op here now — history is queried fresh at snapshot time, not captured.
        display.ShowChatHistory([("user", "ignored", DateTimeOffset.UtcNow)]);
        IReadOnlyList<ChatHistoryItem> chat = [new ChatHistoryItem(1, "user", "John", "hi", DateTimeOffset.UtcNow)];

        var snap = display.Snapshot(display.LatestSeq, chat);

        Assert.Equal("hi", Assert.Single(snap.ChatHistory).Content); // the passed-in history, not the pushed one
        Assert.Empty(display.EventsSince(0)); // and nothing hit the live log
    }

    [Fact]
    public void SnapshotLatestSeqLetsAClientResumeTheStreamWithoutAGap()
    {
        var display = new ApiDisplayProvider(new EventBus(), new AppConfig(), new SessionContext());
        display.ShowReply("first");
        display.ShowReply("second");

        var snap = display.Snapshot(display.LatestSeq, []);

        Assert.Equal(2, snap.LatestSeq);
        Assert.Empty(display.EventsSince(snap.LatestSeq)); // subscribing at LatestSeq misses nothing and repeats nothing
    }

    [Fact]
    public void BudgetUpdatesEmitALiveEventForTheClientGauge()
    {
        var display = new ApiDisplayProvider(new EventBus(), new AppConfig(), new SessionContext());

        display.UpdateBudget(1000, 4000, 25);

        Assert.Contains(display.EventsSince(0), e => e.Kind == "budget" && e.Text == "1000/4000/25");
    }

    [Fact]
    public void SnapshotCarriesTheServerModelProviderAndSession()
    {
        var config = new AppConfig { Provider = "Anthropic", Model = "claude-opus-4-8" };
        var session = new SessionContext();
        var display = new ApiDisplayProvider(new EventBus(), config, session);

        var snap = display.Snapshot(display.LatestSeq, []);

        Assert.Equal("Anthropic", snap.Provider);
        Assert.Equal("claude-opus-4-8", snap.Model);
        Assert.Equal(session.SessionId, snap.SessionId); // so a client shows the server's session, not its own
    }
}
