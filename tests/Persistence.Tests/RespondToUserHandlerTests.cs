using Moq;
using Persistence.Data.Entities;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Runtime.ActionHandlers;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

/// <summary>Unit tests for <see cref="RespondToUserHandler"/>.</summary>
public class RespondToUserHandlerTests
{
    private static WorkingContextEntity Context() =>
        new() { Name = "t", Summary = "s", CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow };

    private static (RespondToUserHandler Handler, List<string> Replies) Create()
    {
        var bus = new EventBus();
        var replies = new List<string>();
        bus.Subscribe<RemotePeerReplied>((_, e) => { replies.Add(e.Reply); return Task.CompletedTask; });

        var session = new Mock<ISessionContext>();
        session.SetupGet(s => s.RemotePeerSourceId).Returns(7);

        return (new RespondToUserHandler(session.Object, bus), replies);
    }

    [Fact]
    public async Task AddsReplyAsChatMessageAndPublishesIt()
    {
        var (handler, replies) = Create();
        var context = Context();

        await handler.HandleAsync(context, JsonValue.Create("hi there"));

        var fragment = Assert.Single(context.ContextFragments.Values);
        Assert.Equal(ContextFragmentType.ChatMessage, fragment.FragmentType);
        Assert.Equal("hi there", fragment.Content);
        Assert.Equal(SourceType.RemotePeer, fragment.Sources.Single().SourceType);
        Assert.Equal(7, fragment.Sources.Single().Id);
        Assert.Equal(["hi there"], replies);
    }

    [Fact]
    public async Task AcceptsAnObjectPayloadWithATextProperty()
    {
        // The model may emit either a bare string or { "text": ... }; both must reply identically.
        var (handler, replies) = Create();
        var context = Context();

        await handler.HandleAsync(context, new JsonObject { ["text"] = "from object" });

        Assert.Equal("from object", Assert.Single(context.ContextFragments.Values).Content);
        Assert.Equal(["from object"], replies);
    }

    [Fact]
    public async Task ThrowsWithoutATextPayload()
    {
        var (handler, _) = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(Context(), null));
    }

    [Fact]
    public async Task ThrowsWhenObjectPayloadLacksAText()
    {
        // An object missing the "text" key is a malformed payload, not an empty reply — it must
        // surface as an error rather than silently posting nothing.
        var (handler, replies) = Create();
        var context = Context();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(context, new JsonObject { ["message"] = "wrong key" }));

        Assert.Empty(context.ContextFragments.Values);
        Assert.Empty(replies);
    }
}
