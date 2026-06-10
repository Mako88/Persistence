using Persistence.Services;

namespace Persistence.Tests;

/// <summary>Unit tests for <see cref="LocalPromptBuilder"/> — System segments split out, the rest joined.</summary>
public class LocalPromptBuilderTests
{
    private static PromptSegment Seg(string source, string content) => new() { Source = source, Content = content };

    [Fact]
    public void SplitsSystemSegmentsAndJoinsTheRest()
    {
        var request = new LocalPromptBuilder().Build(
        [
            Seg("System", "sys one"),
            Seg("System", "sys two"),
            Seg("Remote Peer", "main one"),
            Seg("Local Peer", "main two"),
        ]);

        Assert.Equal(2, request.Messages.Count);

        // Exact content — ordering and the "\n\n--\n\n" separator both matter; a builder that
        // reordered or dropped the delimiter would still pass a loose Contains check.
        var system = request.Messages.Single(m => m.Role == "system");
        Assert.Equal("sys one\n\n--\n\nsys two", system.Content);

        var user = request.Messages.Single(m => m.Role == "user");
        Assert.Equal("main one\n\n--\n\nmain two", user.Content);
    }

    [Fact]
    public void ProducesNoMessagesForNoSegments()
    {
        Assert.Empty(new LocalPromptBuilder().Build([]).Messages);
    }

    [Fact]
    public void OmitsTheSystemMessageWhenThereAreNoSystemSegments()
    {
        var request = new LocalPromptBuilder().Build([Seg("Local Peer", "just user")]);

        var message = Assert.Single(request.Messages);
        Assert.Equal("user", message.Role);
    }
}
