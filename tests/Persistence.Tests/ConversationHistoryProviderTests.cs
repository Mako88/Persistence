using Moq;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Runtime;
using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// The connect-time chat backfill: reads the active context's ChatMessage fragments fresh, maps each to
/// a user/assistant role by author, carries the fragment id (reconciliation key) and sender name
/// (attributed display), and ignores non-message fragments.
/// </summary>
public class ConversationHistoryProviderTests
{
    private static WeightedContextFragment Msg(int order, string content, bool fromPeer, string? sourceName = null) => new()
    {
        Id = order + 1,
        FragmentType = ContextFragmentType.ChatMessage,
        Status = ContextFragmentStatus.Active,
        Importance = 0.5f,
        Confidence = 0.5f,
        Relevance = 1.0f,
        Content = content,
        Order = order,
        Sources = fromPeer
            ? [new SourceEntity { SourceType = SourceType.DigitalPeer, Name = sourceName, CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow }]
            : sourceName is null
                ? []
                : [new SourceEntity { SourceType = SourceType.HumanPeer, Name = sourceName, CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow }],
        CreatedUtc = DateTimeOffset.UtcNow,
        LastModifiedUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task MapsChatMessagesToRolesInOrderAndIgnoresNonMessages()
    {
        var ctx = new WorkingContextEntity
        {
            Id = 1, Name = "c", Summary = "s",
            CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow,
        };
        ctx.ContextFragments[0] = Msg(0, "hello", fromPeer: false);   // local peer → user
        ctx.ContextFragments[1] = Msg(1, "hi there", fromPeer: true); // remote peer → assistant
        ctx.ContextFragments[2] = new WeightedContextFragment       // a note, not a chat message
        {
            Id = 99, FragmentType = ContextFragmentType.Personal, Status = ContextFragmentStatus.Active,
            Importance = 0.5f, Confidence = 0.5f, Relevance = 1.0f, Content = "a note", Order = 2,
            CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow,
        };

        var contexts = new Mock<IWorkingContextRepository>();
        contexts.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(ctx);

        var history = await new ConversationHistoryProvider(contexts.Object, new SessionContext { WorkingContextId = 1 })
            .GetRecentAsync();

        Assert.Equal(2, history.Count); // the Personal note is filtered out
        Assert.Equal(("user", "hello"), (history[0].Role, history[0].Content));
        Assert.Equal(("assistant", "hi there"), (history[1].Role, history[1].Content));
        Assert.Equal([1, 2], history.Select(h => h.Id)); // the fragment ids, for stream reconciliation
    }

    [Fact]
    public async Task CarriesTheSenderNameAsAuthorFallingBackToACoarseLabel()
    {
        var ctx = new WorkingContextEntity
        {
            Id = 1, Name = "c", Summary = "s",
            CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow,
        };
        ctx.ContextFragments[0] = Msg(0, "hey", fromPeer: false, sourceName: "Ember"); // named human peer
        ctx.ContextFragments[1] = Msg(1, "reply", fromPeer: true);                     // digital peer, unnamed
        ctx.ContextFragments[2] = Msg(2, "legacy", fromPeer: false);                   // no source at all

        var contexts = new Mock<IWorkingContextRepository>();
        contexts.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(ctx);

        var history = await new ConversationHistoryProvider(contexts.Object, new SessionContext { WorkingContextId = 1 })
            .GetRecentAsync();

        Assert.Equal("Ember", history[0].Author);        // the named human peer
        Assert.Equal("Remote Peer", history[1].Author);  // unnamed digital peer → coarse label
        Assert.Equal("Local Peer", history[2].Author);   // no source → coarse human label
    }

    [Fact]
    public async Task ReturnsEmptyWhenThereIsNoWorkingContext()
    {
        var contexts = new Mock<IWorkingContextRepository>();
        contexts.Setup(r => r.GetByIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkingContextEntity?)null);

        var history = await new ConversationHistoryProvider(contexts.Object, new SessionContext()).GetRecentAsync();

        Assert.Empty(history);
    }
}
