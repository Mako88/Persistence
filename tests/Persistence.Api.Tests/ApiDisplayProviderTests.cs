using Persistence.Data.Entities;
using Persistence.Events;

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
        var display = new ApiDisplayProvider(new EventBus());

        display.ShowScheduledEvents([Event(1, "standup"), Event(2, "review")]);

        var snap = display.Snapshot();
        Assert.Equal(2, snap.ScheduledEvents.Count);
        Assert.Contains(snap.ScheduledEvents, e => e.Name == "standup");
        // ...and a live "scheduled" event so a subscribed client's Schedule pane updates without re-fetching.
        Assert.Contains(display.EventsSince(0), e => e.Kind == "scheduled");
    }

    [Fact]
    public void OpenProposalCountPopulatesTheSnapshotAndEmitsALiveEvent()
    {
        var display = new ApiDisplayProvider(new EventBus());

        display.ShowOpenProposalCount(3);

        Assert.Equal(3, display.Snapshot().OpenProposalCount);
        Assert.Contains(display.EventsSince(0), e => e.Kind == "proposals" && e.Text == "3");
    }

    [Fact]
    public void ChatHistoryIsSnapshotOnlyNotAddedToTheLiveLog()
    {
        var display = new ApiDisplayProvider(new EventBus());

        display.ShowChatHistory([("user", "hi", DateTimeOffset.UtcNow), ("assistant", "hello", DateTimeOffset.UtcNow)]);

        Assert.Equal(2, display.Snapshot().ChatHistory.Count);
        Assert.Empty(display.EventsSince(0)); // a one-time backfill, not incremental output
    }

    [Fact]
    public void SnapshotLatestSeqLetsAClientResumeTheStreamWithoutAGap()
    {
        var display = new ApiDisplayProvider(new EventBus());
        display.ShowReply("first");
        display.ShowReply("second");

        var snap = display.Snapshot();

        Assert.Equal(2, snap.LatestSeq);
        Assert.Empty(display.EventsSince(snap.LatestSeq)); // subscribing at LatestSeq misses nothing and repeats nothing
    }
}
