using Persistence.Client;
using Persistence.Contracts;

namespace Persistence.Tests;

/// <summary>
/// The room's relay guardrails (ADR-0008 §4). These assert the properties Arden made conditions of the
/// affordance: a carried message stays attributed to whoever said it, keeps one identity across stores,
/// and records that it travelled. They live here rather than in the TUI because they must hold for any
/// front-end that ever offers a relay, not only the pane that has one today.
/// </summary>
public class RelayComposerTests
{
    private static ChatHistoryItem PeerMessage(string author = "Arden", string? messageId = "utterance-1",
        int? relayDepth = 0, string content = "I think the frame should be unforgeable.") =>
        new(Id: 42, Role: "assistant", Author: author, Content: content,
            Timestamp: DateTimeOffset.UtcNow, MessageId: messageId, RelayDepth: relayDepth);

    [Fact]
    public void ARelayedMessageArrivesAsFromTheOriginalSpeakerNotTheRelayer()
    {
        var relayed = RelayComposer.Compose(PeerMessage(author: "Arden"), targetPeer: "Ember");

        // The guardrail. John pressing the button must not make Ember believe John said this — the
        // receiving peer weighs a person's voice differently from a peer's, and collapsing the two
        // would corrupt exactly the provenance the room is built on.
        Assert.Equal("Arden", relayed.FromPeer);
        Assert.Equal("Ember", relayed.AddressedTo);
        Assert.Equal("I think the frame should be unforgeable.", relayed.Content);
    }

    [Fact]
    public void ARelayKeepsTheUtterancesIdentityAndRecordsThatItTravelled()
    {
        var relayed = RelayComposer.Compose(PeerMessage(messageId: "utterance-1", relayDepth: 1), "Ember");

        // One utterance, one id, in every store it lands in — otherwise the id names a copy, not a thing
        // said, and nothing downstream can tell the two records are the same message.
        Assert.Equal("utterance-1", relayed.MessageId);
        // ...but this copy has come one hop further than the one it was carried from.
        Assert.Equal(2, relayed.RelayDepth);
    }

    [Fact]
    public void DepthComesFromTheStoredMessageRatherThanTheRelayersOwnCount()
    {
        // The message knows its own path (migration 007). A relayer that tracked depth itself would drift
        // from the breaker the moment a message arrived by any route the relayer hadn't watched.
        var alreadyTravelled = PeerMessage(relayDepth: 2);

        Assert.Equal(3, RelayComposer.Compose(alreadyTravelled, "Ember").RelayDepth);
    }

    [Fact]
    public void AMessagePersistedBeforeTheIdExistedStillRelays()
    {
        // Pre-migration-007 messages carry null for both. Relaying one must not throw — it just has no
        // prior identity to preserve, and counts as its first hop.
        var legacy = PeerMessage(messageId: null, relayDepth: null);

        var relayed = RelayComposer.Compose(legacy, "Ember");

        Assert.Null(relayed.MessageId);
        Assert.Equal(1, relayed.RelayDepth);
    }

    [Fact]
    public void AHumansOwnWordsTravelAsAHumanMessageAndResetTheChain()
    {
        var fromJohn = new ChatHistoryItem(1, "user", "John", "What do you both think?",
            DateTimeOffset.UtcNow, MessageId: "utterance-9", RelayDepth: 3);

        var relayed = RelayComposer.Compose(fromJohn, "Ember");

        // Not a relay of a peer's voice: it's a person speaking, so no FromPeer attribution...
        Assert.Null(relayed.FromPeer);
        // ...and the hop chain restarts, since depth counts peer-to-peer hops *without* a human turn.
        // Carrying the 3 forward would let a human turn be mistaken for another peer-to-peer hop.
        Assert.Equal(0, relayed.RelayDepth);
    }

    [Fact]
    public void SenderKindComesFromTheStoredRoleNotTheAuthorsName()
    {
        // A peer and a person can share a name (Ember, today, is both a peer name and a plausible human
        // one). Inferring kind from the name is the misattribution the room's sender-kind split prevents.
        var peerNamedLikeAHuman = PeerMessage(author: "Ember");

        Assert.Equal("Ember", RelayComposer.Compose(peerNamedLikeAHuman, "Arden").FromPeer);
    }

    [Fact]
    public void ARelayNeedsSomewhereToGo()
    {
        Assert.Throws<ArgumentException>(() => RelayComposer.Compose(PeerMessage(), targetPeer: "  "));
    }
}
