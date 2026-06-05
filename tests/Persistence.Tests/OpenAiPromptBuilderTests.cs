using Persistence.Services;

namespace Persistence.Tests;

public class OpenAiPromptBuilderTests
{
    private static List<PromptMessage> Build(params PromptSegment[] segments) =>
        new OpenAiPromptBuilder().Build([.. segments]).Messages;

    private static PromptSegment Seg(string source, string content) =>
        new() { Source = source, Content = content };

    [Theory]
    [InlineData("System", "developer")]
    [InlineData("Remote Peer", "assistant")]
    [InlineData("Local Peer", "user")]
    [InlineData("anything else", "user")]
    public void MapsSourceToExpectedRole(string source, string expectedRole)
    {
        var messages = Build(Seg(source, "hello"));

        var message = Assert.Single(messages);
        Assert.Equal(expectedRole, message.Role);
        Assert.Equal("hello", message.Content);
    }

    [Fact]
    public void CollapsesAdjacentSameRoleSegments()
    {
        var messages = Build(
            Seg("Local Peer", "first"),
            Seg("Local Peer", "second"));

        var message = Assert.Single(messages);
        Assert.Equal("user", message.Role);
        Assert.Equal("first\n\n--\n\nsecond", message.Content);
    }

    [Fact]
    public void KeepsNonAdjacentSameRoleSegmentsSeparate()
    {
        var messages = Build(
            Seg("Local Peer", "user-1"),
            Seg("Remote Peer", "assistant"),
            Seg("Local Peer", "user-2"));

        Assert.Equal(3, messages.Count);
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m.Role));
        Assert.Equal("user-2", messages[2].Content);
    }

    [Fact]
    public void ProducesNoMessagesForEmptyInput()
    {
        Assert.Empty(Build());
    }
}
