using Persistence.Data.Entities;
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
    [InlineData("Local Peer", "user")]
    [InlineData("anything else", "user")]
    public void FallsBackToSourceStringWhenNoAuthorType(string source, string expectedRole)
    {
        // Framework segments (protocol, sensory) carry no AuthorType: "System" → developer, else → user.
        // Peer role mapping goes through AuthorType (see MapsByAuthorTypeRegardlessOfDisplayName).
        var messages = Build(Seg(source, "hello"));

        var message = Assert.Single(messages);
        Assert.Equal(expectedRole, message.Role);
        Assert.Equal("hello", message.Content);
    }

    [Theory]
    [InlineData(SourceType.DigitalPeer, "assistant")]
    [InlineData(SourceType.HumanPeer, "user")]
    [InlineData(SourceType.System, "developer")]
    [InlineData(SourceType.DerivedFromFragments, "developer")]
    public void MapsByAuthorTypeRegardlessOfDisplayName(SourceType authorType, string expectedRole)
    {
        // A peer named "Ember" (or anything) must still map by its type, not by a string match — this is
        // what lets arbitrarily-named peers coexist. The display name here would otherwise map to "user".
        var messages = Build(new PromptSegment { Source = "Ember", AuthorType = authorType, Content = "hi" });

        Assert.Equal(expectedRole, Assert.Single(messages).Role);
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
            new PromptSegment { Source = "John", AuthorType = SourceType.HumanPeer, Content = "user-1" },
            new PromptSegment { Source = "Ember", AuthorType = SourceType.DigitalPeer, Content = "assistant" },
            new PromptSegment { Source = "John", AuthorType = SourceType.HumanPeer, Content = "user-2" });

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
