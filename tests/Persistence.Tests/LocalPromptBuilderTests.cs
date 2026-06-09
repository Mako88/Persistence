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

        var system = request.Messages.Single(m => m.Role == "system");
        Assert.Contains("sys one", system.Content);
        Assert.Contains("sys two", system.Content);

        var user = request.Messages.Single(m => m.Role == "user");
        Assert.Contains("main one", user.Content);
        Assert.Contains("main two", user.Content);
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
