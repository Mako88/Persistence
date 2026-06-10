using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// Tests for <see cref="TaggedProtocolInstructions"/> — the instructions are the only thing telling
/// the peer how to respond, so they must actually describe the five tags *and* the command syntax
/// (named arguments, triple-quoting, list discovery), not merely mention the tag names.
/// </summary>
public class TaggedProtocolInstructionsTests
{
    private static readonly string Instructions = new TaggedProtocolInstructions().GetInstructions();

    [Theory]
    [InlineData("<think>")]
    [InlineData("<respond>")]
    [InlineData("<context>")]
    [InlineData("<actions>")]
    [InlineData("<continue>")]
    public void DescribesEachTag(string tag)
    {
        // Every tag must appear at least twice: once in the worked example block and once in the
        // "## Tags" reference list that explains what it does. A tag named only in the example
        // (with no explanation) would be under-documented.
        var occurrences = Instructions.Split(tag).Length - 1;
        Assert.True(occurrences >= 2, $"{tag} should be both shown and explained, found {occurrences} mention(s)");
    }

    [Fact]
    public void ExplainsTheNamedArgumentCommandSyntax()
    {
        // The command form a peer must emit inside <context>/<actions>. Without this it knows the
        // tags but not how to write a command.
        Assert.Contains("field=value", Instructions);
        Assert.Contains("named arguments", Instructions);
    }

    [Fact]
    public void DocumentsTripleQuotingForMultiLineText()
    {
        // The whole point of the format is escaping-free prose; the triple-quote escape hatch for
        // multi-line / quote-containing values must be spelled out.
        Assert.Contains("triple quote", Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"\"\"", Instructions);
    }

    [Fact]
    public void PointsAtListForCommandDiscovery()
    {
        // Commands aren't enumerated here — the peer is told to discover them via list(). If this
        // pointer were lost the peer would have no way to learn the available commands.
        Assert.Contains("list()", Instructions);
    }

    [Fact]
    public void ExplainsTopToBottomExecutionOrder()
    {
        // Ordering is load-bearing (think before respond, create a tag before using it); the
        // instructions must state that tags and commands run in written order.
        Assert.Contains("top to bottom", Instructions);
    }
}
