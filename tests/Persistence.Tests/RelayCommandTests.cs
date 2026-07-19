using Persistence.Client;
using Persistence.Contracts;

namespace Persistence.Tests;

/// <summary>
/// The lean relay affordance's resolver (ADR-0008 §4): which stored message a bare <c>/relay</c> means.
/// The provenance guardrails are <see cref="RelayComposerTests"/>'s job — this only covers the choosing.
/// </summary>
public class RelayCommandTests
{
    private static ChatHistoryItem Msg(string role, string author, string content, long id) =>
        new(id, role, author, content, DateTimeOffset.UtcNow.AddMinutes(id), MessageId: $"u{id}", RelayDepth: 0);

    [Theory]
    [InlineData("/relay Ember", true)]
    [InlineData("/RELAY Ember", true)]   // a command shouldn't hinge on capitalisation
    [InlineData("  /relay Ember  ", true)]
    [InlineData("/relayed thoughts on this", false)]  // a prefix match would swallow ordinary messages
    [InlineData("relay Ember", false)]
    [InlineData("tell Ember to /relay", false)]
    public void RecognisesTheVerbWithoutSwallowingOrdinaryMessages(string input, bool expected) =>
        Assert.Equal(expected, RelayCommand.IsRelay(input));

    [Fact]
    public void ABareRelayHasNoTargetRatherThanAGuessedOne()
    {
        // Better to answer with usage than to pick a peer the human didn't name — a wrong guess here
        // sends someone's words to someone they weren't meant for, which isn't undoable.
        Assert.Null(RelayCommand.ParseTarget("/relay"));
        Assert.Null(RelayCommand.ParseTarget("/relay    "));
        Assert.Equal("Ember", RelayCommand.ParseTarget("/relay Ember"));
    }

    [Fact]
    public void ResolvesTheMostRecentPeerUtteranceNotTheMostRecentMessage()
    {
        var history = new[]
        {
            Msg("assistant", "Arden", "the earlier thought", 1),
            Msg("assistant", "Arden", "the thing worth carrying", 2),
            Msg("user", "John", "thanks, that helps", 3),   // the human spoke last
        };

        var resolved = RelayCommand.ResolveLastRelayable(history);

        // Relaying the human's own words isn't a relay — it's talking to the other peer, which the
        // ordinary input path already does. The useful referent is the last thing the *peer* said.
        Assert.Equal("the thing worth carrying", resolved?.Content);
    }

    [Fact]
    public void ResolvesNothingWhenThePeerHasNotSpoken()
    {
        Assert.Null(RelayCommand.ResolveLastRelayable([Msg("user", "John", "hello?", 1)]));
        Assert.Null(RelayCommand.ResolveLastRelayable([]));
        Assert.Null(RelayCommand.ResolveLastRelayable(null));
    }

    [Fact]
    public void ThePreviewNamesWhoSaidItWhereItsGoingAndHowFar()
    {
        var line = RelayCommand.Describe(Msg("assistant", "Arden", "the frame must be unforgeable", 1), "Ember", 1);

        // The preview is what recovers "you can't see what you're forwarding" without a selection model,
        // so it has to carry all three facts, not just the text.
        Assert.Contains("Arden", line);
        Assert.Contains("Ember", line);
        Assert.Contains("hop 1", line);
        Assert.Contains("the frame must be unforgeable", line);
    }

    [Fact]
    public void ALongMessageIsPreviewedOnOneLine()
    {
        var sprawling = string.Join("\n", Enumerable.Repeat("a long paragraph of reasoning", 20));

        var line = RelayCommand.Describe(Msg("assistant", "Arden", sprawling, 1), "Ember", 1);

        Assert.DoesNotContain("\n", line);
        Assert.Contains("…", line);
    }
}
