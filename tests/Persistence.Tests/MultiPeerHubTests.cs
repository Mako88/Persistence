using Persistence.Console;
using Persistence.Contracts;
using Persistence.Data.Entities;

namespace Persistence.Tests;

/// <summary>
/// The multi-peer hub's scope + per-peer bookkeeping (ADR-0007 Phase 2b), tested against a fake render
/// target so no Terminal.Gui loop is needed. Covers the two scopes — one peer, or the merged "all"
/// overview — and what each puts on screen: whose conversation, whose side panes, whose status.
/// </summary>
public class MultiPeerHubTests
{
    private sealed class FakeTarget : IMultiPeerRenderTarget
    {
        public string Chat = "";
        public string Thoughts = "", Actions = "", Schedule = "", Debug = "";
        public string Provider = "", Model = "", Session = "", State = "";
        public int Proposals;
        public (int Used, int Budget, int Percent)? Budget;
        public int SidePaneRepaints;
        public int StatusRepaints;
        public bool? LastPeerSwitched;

        public void SetConversation(string chat, bool scopeChanged) => Chat = chat;

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

    private static MultiPeerHub HubWith(FakeTarget target, params string[] peers)
    {
        var hub = new MultiPeerHub(target);
        foreach (var p in peers)
        {
            hub.RegisterPeer(p, provider: $"{p}-prov", model: $"{p}-model", session: $"{p}-sess");
        }
        return hub;
    }

    private static ChatHistoryItem Msg(long id, string author, string text, DateTimeOffset at, bool human = false) =>
        new(id, human ? "user" : "assistant", author, text, at);

    private static readonly DateTimeOffset T0 = new(2026, 7, 14, 9, 0, 0, TimeSpan.Zero);

    // --- Scope ---

    [Fact]
    public void ScopeStartsAtAllSoAFreshStartOpensOnTheMergedConversation()
    {
        var hub = HubWith(new FakeTarget(), "Arden", "Ember");

        Assert.Null(hub.ActivePeer);                                            // null == the "all" scope
        Assert.Equal(new[] { "Arden", "Ember" }, hub.PeerNames);                // "all" is not a peer…
        Assert.Equal(new[] { "All", "Arden", "Ember" }, hub.SelectorEntries);   // …but it is a selector entry
    }

    [Fact]
    public void SelectingAPeerNarrowsTheScopeAndSelectingAllWidensItBack()
    {
        var hub = HubWith(new FakeTarget(), "Arden", "Ember");

        hub.SetActive("Ember");
        Assert.Equal("Ember", hub.ActivePeer);

        hub.SetActive(MultiPeerHub.AllScope);
        Assert.Null(hub.ActivePeer);
    }

    // --- Conversation: per-peer vs. merged ---

    [Fact]
    public void AllScopeMergesEveryPeersHistoryIntoOneChronology()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");

        // Each peer's history arrives as its own block, out of order relative to the other's — exactly as
        // the two connections deliver them. Interleaving by the store's timestamps is the whole point.
        hub.ScopeFor("Arden").ShowChatHistory([Msg(1, "Arden", "arden-first", T0.AddMinutes(1)), Msg(2, "Arden", "arden-third", T0.AddMinutes(3))]);
        hub.ScopeFor("Ember").ShowChatHistory([Msg(3, "Ember", "ember-second", T0.AddMinutes(2)), Msg(4, "Ember", "ember-fourth", T0.AddMinutes(4))]);

        var order = new[] { "arden-first", "ember-second", "arden-third", "ember-fourth" }
            .Select(m => target.Chat.IndexOf(m, StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(-1, order);                  // all four present
        Assert.Equal(order.OrderBy(i => i), order);        // and in time order, not connection order
    }

    [Fact]
    public void APeerScopeShowsOnlyThatPeersConversation()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");
        hub.ScopeFor("Arden").ShowChatHistory([Msg(1, "Arden", "arden-said-this", T0.AddMinutes(1))]);
        hub.ScopeFor("Ember").ShowChatHistory([Msg(2, "Ember", "ember-said-this", T0.AddMinutes(2))]);

        hub.SetActive("Arden");

        Assert.Contains("arden-said-this", target.Chat);
        Assert.DoesNotContain("ember-said-this", target.Chat);
    }

    [Fact]
    public void AReplyLandsInItsOwnPeersConversationNotTheOthers()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");

        hub.ScopeFor("Ember").ShowReply("ember-reply");
        hub.SetActive("Arden");

        Assert.DoesNotContain("ember-reply", target.Chat);

        hub.SetActive("Ember");
        Assert.Contains("ember-reply", target.Chat);
    }

    [Fact]
    public void AllScopeCollapsesTheSameBroadcastStoredOnceInEachPeer()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");

        // One thing John broadcast is persisted in *both* peers' stores, so the merge sees it twice.
        var at = T0.AddMinutes(1);
        hub.ScopeFor("Arden").ShowChatHistory([Msg(1, "John", "hello both", at, human: true)]);
        hub.ScopeFor("Ember").ShowChatHistory([Msg(9, "John", "hello both", at, human: true)]);

        Assert.Equal(1, Occurrences(target.Chat, "hello both"));
    }

    [Fact]
    public void AllScopeKeepsTwoPeersSayingTheSameThing()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");

        // The collapse must be narrow: only the human's own broadcast is deduped. Two peers independently
        // agreeing are two real messages, and losing one would be a lie about the conversation.
        var at = T0.AddMinutes(1);
        hub.ScopeFor("Arden").ShowChatHistory([Msg(1, "Arden", "sounds good", at)]);
        hub.ScopeFor("Ember").ShowChatHistory([Msg(2, "Ember", "sounds good", at)]);

        Assert.Equal(2, Occurrences(target.Chat, "sounds good"));
    }

    [Fact]
    public void LocalChatUnderAPeerScopeIsAttributedToThatPeerOnly()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");

        hub.SetActive("Arden");
        hub.RecordLocalChat("You: just for arden");

        Assert.Contains("just for arden", target.Chat);

        hub.SetActive("Ember");
        Assert.DoesNotContain("just for arden", target.Chat);
    }

    [Fact]
    public void LocalChatUnderAllIsABroadcastAndShowsInEveryPeersConversation()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");

        hub.RecordLocalChat("You: for everyone");   // scope starts at "all"

        foreach (var peer in new[] { "Arden", "Ember" })
        {
            hub.SetActive(peer);
            Assert.Contains("for everyone", target.Chat);
        }
    }

    // --- Side panes + status under each scope ---

    [Fact]
    public void AllScopeBlanksTheSideColumnSinceThereIsNoSinglePeerToShow()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");

        hub.ScopeFor("Arden").ShowThought("arden is pondering");

        Assert.DoesNotContain("arden is pondering", target.Thoughts);
        Assert.Contains("No peer selected", target.Thoughts);
    }

    [Fact]
    public void AllScopeStatusSumsProposalsAndReportsAnyPeerWorking()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");

        hub.RecordProposals("Arden", 2);
        hub.RecordProposals("Ember", 3);
        Assert.Equal(5, target.Proposals);
        Assert.Equal("idle", target.State);

        hub.ScopeFor("Ember").ShowThinking();

        Assert.Contains("thinking", target.State);   // someone is working, even though Ember isn't selected
        Assert.Null(target.Budget);                  // spend is per peer; there's no meaningful "all" figure
        Assert.Equal("", target.Model);              // and no single model — the bar collapses its "/"
    }

    [Fact]
    public void RecordingForTheSelectedPeerRepaintsWithItsContent()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");
        hub.SetActive("Arden");

        hub.ScopeFor("Arden").ShowThought("arden is pondering");

        Assert.Contains("arden is pondering", target.Thoughts);
        Assert.True(target.SidePaneRepaints > 0);
    }

    [Fact]
    public void RecordingForABackgroundPeerDoesNotAlterTheVisiblePane()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");
        hub.SetActive("Arden");
        var repaintsBefore = target.SidePaneRepaints;

        hub.ScopeFor("Ember").ShowThought("ember is pondering");

        Assert.DoesNotContain("ember is pondering", target.Thoughts);
        Assert.Equal(repaintsBefore, target.SidePaneRepaints);   // no repaint for the off-screen peer
    }

    [Fact]
    public void SwitchingPeerShowsThatPeersOwnBufferedContent()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");
        hub.ScopeFor("Arden").ShowThought("arden thought");
        hub.ScopeFor("Ember").ShowThought("ember thought");   // buffered off-screen

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
        hub.ScopeFor("Arden").ShowThought("first arden thought");
        hub.SetActive("Ember");
        hub.ScopeFor("Arden").ShowThought("second arden thought");   // accumulates while Ember is on screen

        hub.SetActive("Arden");

        Assert.Contains("first arden thought", target.Thoughts);
        Assert.Contains("second arden thought", target.Thoughts);
    }

    [Fact]
    public void PerPeerScheduleAndBudgetTrackTheSelectedPeer()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");
        hub.SetActive("Arden");

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

    // --- Scroll-behaviour flag ---

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
        hub.SetActive("Arden");

        hub.ScopeFor("Arden").ShowThought("still going");

        // An append to the peer already on screen must respect where the reader is (no forced scroll).
        Assert.False(target.LastPeerSwitched);
    }

    // --- Per-peer turn state ---

    [Fact]
    public void ABackgroundPeersReplyDoesNotSettleTheWatchedPeersState()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden", "Ember");
        hub.SetActive("Arden");

        hub.ScopeFor("Arden").ShowThinking();      // the peer being watched starts a turn
        hub.ScopeFor("Ember").ShowReply("done");   // a background peer finishes one

        // The status bar shows Arden, who is still working. Reporting "idle" here was the reason the chip
        // lied about the peer.
        Assert.Contains("thinking", target.State);
    }

    [Fact]
    public void APeersOwnReplySettlesItsOwnState()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden");
        hub.SetActive("Arden");
        var arden = hub.ScopeFor("Arden");

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
        hub.SetActive("Arden");

        hub.ScopeFor("Arden").ShowThinking("recalling");

        // The status bar tells "working" from "settled" by the trailing ellipsis, so a lane storing the
        // bare label would leave the chip gray however hard the peer was working.
        Assert.Equal("recalling…", target.State);
    }

    [Fact]
    public void RecordingForAnUnknownPeerIsIgnored()
    {
        var target = new FakeTarget();
        var hub = HubWith(target, "Arden");
        hub.SetActive("Arden");

        hub.RecordThought("Nobody", "into the void");   // no such lane
        hub.SetActive("Nobody");                        // no such peer

        Assert.Equal("Arden", hub.ActivePeer);
        Assert.DoesNotContain("into the void", target.Thoughts);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
