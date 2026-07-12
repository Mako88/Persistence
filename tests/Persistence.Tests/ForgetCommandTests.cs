using Moq;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Runtime.ActionHandlers;
using Persistence.Services;
using System.Data;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

/// <summary>
/// Recoverable <c>forget</c>: soft-deletes a fragment (flips <c>IsDeleted</c>, detaches from view) so it
/// stops surfacing everywhere but is never erased; <c>unforget</c> restores it; <c>list_forgotten</c> is
/// the recovery surface. Distinct from <c>remove</c>, which only detaches an active fragment from context.
/// </summary>
public class ForgetCommandTests
{
    private readonly Mock<IContextFragmentRepository> fragmentRepo = new();
    private readonly Mock<IWorkingContextRepository> workingContextRepo = new();
    private readonly List<ToolInvoked> published = [];
    private readonly ManageContextHandler handler;

    public ForgetCommandTests()
    {
        fragmentRepo.Setup(r => r.SetDeletedAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        workingContextRepo.Setup(r => r.RemoveFragmentAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<IDbTransaction?>()))
            .Returns(Task.CompletedTask);

        var bus = new EventBus();
        bus.Subscribe<ToolInvoked>((_, e) => { published.Add(e); return Task.CompletedTask; });

        handler = new ManageContextHandler(
            workingContextRepo.Object,
            fragmentRepo.Object,
            new Mock<ITagRepository>().Object,
            new Mock<IEntityTagRepository>().Object,
            new Mock<IScheduledEventRepository>().Object,
            new Mock<ISourceRepository>().Object,
            new SessionContext { RemotePeerSourceId = 3 },
            new Mock<IProposalService>().Object,
            new Mock<IProposalRepository>().Object,
            new AppConfig(),
            bus);
    }

    private static WeightedContextFragment Frag(long id, bool isProtected = false, bool isDeleted = false, string content = "x") =>
        new()
        {
            Id = id,
            FragmentType = ContextFragmentType.Personal,
            Status = ContextFragmentStatus.Active,
            Importance = 0.5f,
            Confidence = 0.5f,
            Relevance = 1.0f,
            IsProtected = isProtected,
            IsDeleted = isDeleted,
            Content = content,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    private static WorkingContextEntity ContextWith(params WeightedContextFragment[] fragments)
    {
        var context = new WorkingContextEntity
        {
            Id = 1, Name = "c", Summary = "s",
            CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow,
        };
        var order = 0;
        foreach (var f in fragments)
        {
            context.ContextFragments[order++] = f;
        }
        return context;
    }

    private async Task<string> RunAsync(WorkingContextEntity context, string json)
    {
        published.Clear();
        await handler.HandleAsync(context, JsonNode.Parse(json));
        return published.Single().Result;
    }

    [Fact]
    public async Task ForgetSoftDeletesAndDetachesFromContext()
    {
        var context = ContextWith(Frag(5));

        var result = await RunAsync(context, """{ "forget": { "id": 5 } }""");

        fragmentRepo.Verify(r => r.SetDeletedAsync(5, true, It.IsAny<CancellationToken>()), Times.Once);
        workingContextRepo.Verify(r => r.RemoveFragmentAsync(1, 5, It.IsAny<IDbTransaction?>()), Times.Once);
        Assert.DoesNotContain(context.ContextFragments.Values, f => f.Id == 5); // left view this turn
        Assert.Contains("unforget(5)", result);
    }

    [Fact]
    public async Task ForgetWorksOnAFragmentNotCurrentlyLoaded()
    {
        // Not in context — the handler fetches it so you can forget something out of view.
        fragmentRepo.Setup(r => r.GetByIdAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync(Frag(9));
        var context = ContextWith();

        var result = await RunAsync(context, """{ "forget": { "id": 9 } }""");

        fragmentRepo.Verify(r => r.SetDeletedAsync(9, true, It.IsAny<CancellationToken>()), Times.Once);
        workingContextRepo.Verify(r => r.RemoveFragmentAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<IDbTransaction?>()), Times.Never);
        Assert.Contains("Forgot fragment #9", result);
    }

    [Fact]
    public async Task ForgetRefusesProtectedFragments()
    {
        var context = ContextWith(Frag(5, isProtected: true));

        var result = await RunAsync(context, """{ "forget": { "id": 5 } }""");

        Assert.Contains("protected", result);
        fragmentRepo.Verify(r => r.SetDeletedAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(context.ContextFragments.Values, f => f.Id == 5); // still there, untouched
    }

    [Fact]
    public async Task ForgetIsIdempotentOnAnAlreadyForgottenFragment()
    {
        fragmentRepo.Setup(r => r.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(Frag(7, isDeleted: true));

        var result = await RunAsync(ContextWith(), """{ "forget": { "id": 7 } }""");

        Assert.Contains("already forgotten", result);
        fragmentRepo.Verify(r => r.SetDeletedAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnforgetRestoresAForgottenFragmentAndLoadsItBack()
    {
        fragmentRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(Frag(5, isDeleted: true));
        var context = ContextWith();

        var result = await RunAsync(context, """{ "unforget": { "id": 5 } }""");

        fragmentRepo.Verify(r => r.SetDeletedAsync(5, false, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(context.ContextFragments.Values, f => f.Id == 5); // loaded back into context
        Assert.Contains("Restored fragment #5", result);
    }

    [Fact]
    public async Task UnforgetIsANoOpWhenTheFragmentWasNotForgotten()
    {
        fragmentRepo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(Frag(5, isDeleted: false));

        var result = await RunAsync(ContextWith(), """{ "unforget": { "id": 5 } }""");

        Assert.Contains("isn't forgotten", result);
        fragmentRepo.Verify(r => r.SetDeletedAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListForgottenShowsTheRecoverySurface()
    {
        fragmentRepo.Setup(r => r.GetDeletedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Frag(8, isDeleted: true, content: "an old wrong belief")]);

        var result = await RunAsync(ContextWith(), """{ "list_forgotten": {} }""");

        Assert.Contains("#8", result);
        Assert.Contains("an old wrong belief", result);
        Assert.Contains("unforget", result);
    }

    [Fact]
    public async Task ListForgottenReportsWhenNothingIsForgotten()
    {
        fragmentRepo.Setup(r => r.GetDeletedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await RunAsync(ContextWith(), """{ "list_forgotten": {} }""");

        Assert.Contains("Nothing forgotten", result);
    }
}
