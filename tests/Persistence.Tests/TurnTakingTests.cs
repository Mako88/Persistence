using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// The turn-taking rule (ADR-0008 §1). Deliberately a rule rather than a classifier — a peer decides
/// whether to speak by something it can read and correct, not by an opaque model. These tests are the
/// rule's readable form: each asserts the verdict *and* that the reason says why.
/// </summary>
public class TurnTakingTests
{
    private static TurnTakingVerdict Eval(string content, string? addressedTo = null, bool fromHuman = true,
        string self = "Arden", params string[] aliases) =>
        TurnTaking.Evaluate(content, self, addressedTo, fromHuman, aliases);

    // --- Structurally addressed: the strongest signal ---

    [Fact]
    public void AddressedToYouMeansRespond()
    {
        var v = Eval("thoughts?", addressedTo: "Arden");

        Assert.True(v.ShouldRespond);
        Assert.Contains("addressed to you", v.Reason);
    }

    [Fact]
    public void AddressedToSomeoneElseMeansHold()
    {
        // The whole reason addressed_to exists: "overheard" is knowable without reading the wording.
        var v = Eval("thoughts?", addressedTo: "Ember");

        Assert.False(v.ShouldRespond);
        Assert.Contains("not you", v.Reason);
    }

    // --- Named in the text ---

    [Fact]
    public void BeingNamedMeansRespond()
    {
        var v = Eval("Arden, what do you make of this?", fromHuman: false);

        Assert.True(v.ShouldRespond);
        Assert.Contains("named", v.Reason);
    }

    [Theory]
    [InlineData("arden, thoughts?")]
    [InlineData("ARDEN — thoughts?")]
    [InlineData("@arden thoughts?")]
    public void NameMatchingIsCaseInsensitiveAndAcceptsAnAtPrefix(string content) =>
        // @-syntax is an *additional* signal, never required: humans won't use it consistently, and
        // requiring it would make a peer miss being addressed — the worse failure.
        Assert.True(Eval(content, fromHuman: false).ShouldRespond);

    [Theory]
    [InlineData("the gardener will know")]
    [InlineData("hardened steel")]
    public void ANameBuriedInsideAnotherWordDoesNotCount(string content) =>
        // "Arden" inside "gardener" must not fire — a false positive makes a peer speak uninvited.
        Assert.False(Eval(content, fromHuman: false).ShouldRespond);

    [Fact]
    public void AnAliasCounts()
    {
        var v = Eval("Claude, thoughts?", fromHuman: false, self: "Arden", aliases: "Claude");

        Assert.True(v.ShouldRespond);
        Assert.Contains("Claude", v.Reason);
    }

    // --- Opening the floor ---

    [Theory]
    [InlineData("what do you both think?")]
    [InlineData("everyone, weigh in")]
    [InlineData("open question for the room")]
    public void AHumanOpeningTheFloorMeansRespond(string content)
    {
        var v = Eval(content);

        Assert.True(v.ShouldRespond);
        Assert.Contains("floor", v.Reason);
    }

    [Fact]
    public void APeerCannotOpenTheFloor()
    {
        // Only a human opens the floor. A peer saying "what do you both think?" shouldn't conscript the
        // room — that's how a two-peer loop starts.
        var v = Eval("what do you both think?", fromHuman: false);

        Assert.False(v.ShouldRespond);
        Assert.Contains("overheard", v.Reason);
    }

    // --- The default ---

    [Fact]
    public void OverhearingBetweenPeersMeansHold()
    {
        // ADR-0008: speaking costs money and attention. Hold unless you have something to add.
        var v = Eval("I'll take the import path then", fromHuman: false);

        Assert.False(v.ShouldRespond);
        Assert.Contains("hold", v.Reason);
    }

    [Fact]
    public void AHumanTalkingToTheRoomStillReachesYou()
    {
        // A person addressing the room with one peer present is addressing that peer; staying silent
        // there would read as being ignored.
        var v = Eval("morning, how's it going?");

        Assert.True(v.ShouldRespond);
    }

    [Fact]
    public void EveryVerdictCarriesItsReason()
    {
        // The rule is meant to be inspectable and correctable, which means it always has to say why.
        foreach (var v in new[]
        {
            Eval("hi", addressedTo: "Arden"),
            Eval("hi", addressedTo: "Ember"),
            Eval("Arden?", fromHuman: false),
            Eval("what do you both think?"),
            Eval("chatter", fromHuman: false),
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(v.Reason));
        }
    }
}
