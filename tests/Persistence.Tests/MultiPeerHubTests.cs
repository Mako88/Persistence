using Persistence.Console;
using Persistence.Contracts;
using Persistence.Data.Entities;
using Persistence.Runtime;

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
        public bool? LastPeerSwitched;

        public void SetSidePaneContent(string thoughts, string actions, string schedule, string debug, bool peerSwitched)
        {
            Thoughts = thoughts;
            Actions = actions;
            Schedule = schedule;
            Debug = debug;
            LastPeerSwitched = peerSwitched;
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

    /// <summary>
    /// Stands in for the shared conversation pane a <c>PeerScopedDisplay</c> writes chat through. The
    /// state assertions below are about the hub's lanes, not the chat text, so this just swallows it.
    /// </summary>
    private sealed class NullChat : IDisplayProvider
    {
        public Task Start(CancellationToken ct) => Task.CompletedTask;
        public void Stop() { }
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
        public void ShowError(string message) { }
        public void ShowDebugInfo(string info) { }
        public void ShowChatHistory(IReadOnlyList<ChatHistoryItem> messages) { }
        public void ShowSystemMessage(string message) { }
        public void ShowUnknownCommand(string command) { }
        public void ShowMessageQueued(string input) { }
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
    public void SwitchingPeerIsFlaggedSoThePanesJumpToTheNewestLine()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");

        hub.SetActive("Ember");

        // Different content is now on screen, so where the reader had scrolled to means nothing.
        Assert.True(target.LastPeerSwitched);
    }

    [Fact]
    public void LiveUpdatesToTheShownPeerAreNotFlaggedAsASwitch()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");

        hub.RecordThought("Arden", "still going");

        // An append to the peer already on screen must respect where the reader is (no forced scroll).
        Assert.False(target.LastPeerSwitched);
    }

    [Fact]
    public void ABackgroundPeersReplyDoesNotSettleTheWatchedPeersState()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");   // Arden active
        var chat = new NullChat();

        hub.ScopeFor("Arden", chat).ShowThinking();      // the peer being watched starts a turn
        hub.ScopeFor("Ember", chat).ShowReply("done");   // a background peer finishes one

        // Chat aggregates, so Ember's reply reaches the shared pane — but the status bar shows Arden,
        // who is still working. Reporting "idle" here was the reason the chip lied about the peer.
        Assert.Contains("thinking", target.State);
    }

    [Fact]
    public void APeersOwnReplySettlesItsOwnState()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden");
        var chat = new NullChat();
        var arden = hub.ScopeFor("Arden", chat);

        arden.ShowThinking();
        Assert.Contains("thinking", target.State);

        arden.ShowReply("here you go");

        Assert.Equal("idle", target.State);
    }

    [Fact]
    public void AThinkingStateCarriesTheEllipsisTheStatusChipColoursOn()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden");

        hub.ScopeFor("Arden", new NullChat()).ShowThinking("recalling");

        // The status bar tells "working" from "settled" by the trailing ellipsis, so a lane storing the
        // bare label would leave the chip gray however hard the peer was working.
        Assert.Equal("recalling…", target.State);
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
