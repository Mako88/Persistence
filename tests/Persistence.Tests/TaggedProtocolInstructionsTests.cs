using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// Smoke test for <see cref="TaggedProtocolInstructions"/> — the instructions should describe the
/// tagged wire format (its tags and command syntax) so the peer knows how to respond.
/// </summary>
public class TaggedProtocolInstructionsTests
{
    [Fact]
    public void InstructionsDescribeTheTaggedFormat()
    {
        var instructions = new TaggedProtocolInstructions().GetInstructions();

        Assert.False(string.IsNullOrWhiteSpace(instructions));
        Assert.Contains("<think>", instructions);
        Assert.Contains("<respond>", instructions);
        Assert.Contains("<context>", instructions);
        Assert.Contains("<actions>", instructions);
        Assert.Contains("<continue>", instructions);
    }
}
