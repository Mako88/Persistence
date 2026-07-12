using Moq;
using Persistence.Data.Entities;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Runtime.ActionHandlers;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

public class ThinkHandlerTests
{
    private static WorkingContextEntity NewContext() =>
        new()
        {
            Name = "test",
            Summary = "test",
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    private static (ThinkHandler Handler, EventBus Bus, List<string> Thoughts) Create()
    {
        var bus = new EventBus();
        var thoughts = new List<string>();
        bus.Subscribe<ModelThought>((_, e) => { thoughts.Add(e.Thought); return Task.CompletedTask; });

        var session = new Mock<ISessionContext>();
        session.SetupGet(s => s.RemotePeerSourceId).Returns(42);

        return (new ThinkHandler(session.Object, bus), bus, thoughts);
    }

    [Fact]
    public async Task AddsThoughtAsPersistedThoughtFragment()
    {
        var (handler, _, _) = Create();
        var context = NewContext();

        await handler.HandleAsync(context, JsonValue.Create("I should check the logs first"));

        var fragment = Assert.Single(context.ContextFragments.Values);
        // Thought (not the never-saved ScratchPad) so recent reasoning survives across turns.
        Assert.Equal(ContextFragmentType.Thought, fragment.FragmentType);
        Assert.Equal("I should check the logs first", fragment.Content);
    }

    [Fact]
    public async Task AttributesThoughtToRemotePeer()
    {
        var (handler, _, _) = Create();
        var context = NewContext();

        await handler.HandleAsync(context, JsonValue.Create("hmm"));

        var source = Assert.Single(context.ContextFragments.Values.Single().Sources);
        Assert.Equal(SourceType.DigitalPeer, source.SourceType);
        Assert.Equal(42, source.Id);
    }

    [Fact]
    public async Task PublishesThoughtForDisplay()
    {
        var (handler, _, thoughts) = Create();

        await handler.HandleAsync(NewContext(), JsonValue.Create("thinking out loud"));

        Assert.Equal("thinking out loud", Assert.Single(thoughts));
    }

    [Fact]
    public async Task PrivateThoughtIsPersistedButNotPublishedToTheConsole()
    {
        var (handler, _, thoughts) = Create();
        var context = NewContext();

        await handler.HandleAsync(context, JsonNode.Parse("""{ "text": "a private worry", "private": true }"""));

        // Still saved as a Thought fragment (it's the peer's own reasoning)…
        var fragment = Assert.Single(context.ContextFragments.Values);
        Assert.Equal(ContextFragmentType.Thought, fragment.FragmentType);
        Assert.Equal("a private worry", fragment.Content);
        // …but never published to the console Thoughts tab.
        Assert.Empty(thoughts);
    }

    [Fact]
    public async Task AcceptsObjectWithTextProperty()
    {
        var (handler, _, _) = Create();
        var context = NewContext();

        await handler.HandleAsync(context, JsonNode.Parse("""{ "text": "structured thought" }"""));

        Assert.Equal("structured thought", context.ContextFragments.Values.Single().Content);
    }

    [Fact]
    public async Task ThrowsWhenNoText()
    {
        var (handler, _, _) = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(NewContext(), JsonNode.Parse("{}")));
    }
}
