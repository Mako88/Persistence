using Persistence.Console;
using Persistence.Data.Entities;

namespace Persistence.Tests;

/// <summary>
/// The multi-peer hub's per-peer bookkeeping (ADR-0007 Phase 2b), tested against a fake render target so
/// no Terminal.Gui loop is needed. Covers: the active peer, that background peers accumulate silently, and
/// that switching repaints from the switched-to peer's own buffers.
/// </summary>
public class MultiPeerHubTests
{
    private sealed class FakeTarget : IMultiPeerRenderTarget
    {
        public string Thoughts = "", Actions = "", Schedule = "", Debug = "";
        public string Provider = "", Model = "", Session = "", State = "";
        public int Proposals;
        public (int Used, int Budget, int Percent)? Budget;
        public int SidePaneRepaints;
        public int StatusRepaints;

        public void SetSidePaneContent(string thoughts, string actions, string schedule, string debug)
        {
            Thoughts = thoughts;
            Actions = actions;
            Schedule = schedule;
            Debug = debug;
            SidePaneRepaints++;
        }

        public void SetPeerStatus(string provider, string model, string session, int proposals, (int Used, int Budget, int Percent)? budget, string state)
        {
            Provider = provider;
            Model = model;
            Session = session;
            Proposals = proposals;
            Budget = budget;
            State = state;
            StatusRepaints++;
        }
    }

    private static MultiPeerHub HubWith(FakeTarget target, params string[] peers)
    {
        var hub = new MultiPeerHub(target);
        foreach (var p in peers)
        {
            hub.RegisterPeer(p, provider: $"{p}-prov", model: $"{p}-model", session: $"{p}-sess");
        }
        return hub;
    }

    [Fact]
    public void FirstRegisteredPeerBecomesActive()
    {
        var hub = HubWith(new FakeTarget(), "Arden", "Ember");

        Assert.Equal("Arden", hub.ActivePeer);
        Assert.Equal(new[] { "Arden", "Ember" }, hub.PeerNames);
    }

    [Fact]
    public void RecordingForTheActivePeerRepaintsWithItsContent()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");

        hub.RecordThought("Arden", "arden is pondering");

        Assert.Contains("arden is pondering", target.Thoughts);
        Assert.True(target.SidePaneRepaints > 0);
    }

    [Fact]
    public void RecordingForABackgroundPeerDoesNotAlterTheVisiblePane()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");   // Arden active
        var repaintsBefore = target.SidePaneRepaints;

        hub.RecordThought("Ember", "ember is pondering");

        Assert.DoesNotContain("ember is pondering", target.Thoughts);
        Assert.Equal(repaintsBefore, target.SidePaneRepaints);   // no repaint for the off-screen peer
    }

    [Fact]
    public void SwitchingPeerShowsThatPeersOwnBufferedContent()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");
        hub.RecordThought("Arden", "arden thought");
        hub.RecordThought("Ember", "ember thought");   // buffered off-screen

        hub.SetActive("Ember");

        Assert.Contains("ember thought", target.Thoughts);
        Assert.DoesNotContain("arden thought", target.Thoughts);   // Arden's lane isn't shown now
        Assert.Equal("Ember-model", target.Model);                 // status reflects the switched-to peer
        Assert.Equal("Ember", hub.ActivePeer);
    }

    [Fact]
    public void SwitchingBackRestoresTheFirstPeersAccumulatedThoughts()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");
        hub.RecordThought("Arden", "first arden thought");
        hub.SetActive("Ember");
        hub.RecordThought("Arden", "second arden thought");   // accumulates while Ember is on screen

        hub.SetActive("Arden");

        Assert.Contains("first arden thought", target.Thoughts);
        Assert.Contains("second arden thought", target.Thoughts);
    }

    [Fact]
    public void PerPeerScheduleAndBudgetTrackTheActivePeer()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");

        hub.RecordBudget("Ember", used: 100, budget: 1000, percent: 42);
        hub.RecordSchedule("Ember", [new ScheduledEventEntity
        {
            Name = "Ember wake",
            WorkingContextId = 0,
            ScheduledForUtc = DateTimeOffset.UtcNow,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            Status = ScheduledEventStatus.Pending,
        }]);

        // Still on Arden — Ember's schedule/budget must not be visible yet.
        Assert.DoesNotContain("Ember wake", target.Schedule);
        Assert.Null(target.Budget);

        hub.SetActive("Ember");

        Assert.Contains("Ember wake", target.Schedule);
        Assert.Equal((100, 1000, 42), target.Budget);
    }

    [Fact]
    public void RecordingForAnUnknownPeerIsIgnored()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden");

        hub.RecordThought("Nobody", "into the void");   // no such lane
        hub.SetActive("Nobody");                        // no such peer

        Assert.Equal("Arden", hub.ActivePeer);
        Assert.DoesNotContain("into the void", target.Thoughts);
    }
}
