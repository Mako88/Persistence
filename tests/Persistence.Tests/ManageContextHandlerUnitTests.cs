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
/// Unit tests for the in-memory <see cref="ManageContextHandler"/> commands — those that operate on
/// the working context's fragments without querying the database (remove, set_summary,
/// toggle_summary_display, summarize_fragments, update). Repositories are mocked; the only DB call
/// these make is <c>RemoveFragmentAsync</c> (detach from the junction), which is verified.
/// </summary>
public class ManageContextHandlerUnitTests
{
    private readonly Mock<IWorkingContextRepository> workingContextRepo = new();
    private readonly SessionContext session = new() { RemotePeerSourceId = 3 };
    private readonly List<ToolInvoked> published = [];
    private readonly ManageContextHandler handler;

    public ManageContextHandlerUnitTests()
    {
        workingContextRepo
            .Setup(r => r.RemoveFragmentAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<IDbTransaction?>()))
            .Returns(Task.CompletedTask);

        var bus = new EventBus();
        bus.Subscribe<ToolInvoked>((_, e) => { published.Add(e); return Task.CompletedTask; });

        handler = new ManageContextHandler(
            workingContextRepo.Object,
            new Mock<IContextFragmentRepository>().Object,
            new Mock<ITagRepository>().Object,
            new Mock<IEntityTagRepository>().Object,
            new Mock<IScheduledEventRepository>().Object,
            new Mock<ISourceRepository>().Object,
            session,
            new Mock<IProposalService>().Object,
            new Mock<IProposalRepository>().Object,
            new AppConfig(),
            bus);
    }

    private static WorkingContextEntity Context() =>
        new() { Id = 1, Name = "c", Summary = "s", CreatedUtc = DateTimeOffset.UtcNow, LastModifiedUtc = DateTimeOffset.UtcNow };

    private static WeightedContextFragment Fragment(long id, string content, bool isProtected = false, string? summary = null) =>
        new()
        {
            Id = id,
            FragmentType = ContextFragmentType.Personal,
            Status = ContextFragmentStatus.Active,
            Content = content,
            Summary = summary,
            Importance = 0.5f,
            Confidence = 0.5f,
            Relevance = 1.0f,
            IsProtected = isProtected,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };

    private async Task<string> RunAsync(WorkingContextEntity context, string json)
    {
        published.Clear();
        await handler.HandleAsync(context, JsonNode.Parse(json));
        return published.Single().Result;
    }

    [Fact]
    public async Task RemoveTakesFragmentOutOfContext()
    {
        var context = Context();
        context.AddFragment(Fragment(10, "note"));

        var result = await RunAsync(context, """{ "remove": { "id": 10 } }""");

        Assert.Contains("Took fragment #10 out of context", result);
        Assert.DoesNotContain(context.ContextFragments.Values, f => f.Id == 10);
        workingContextRepo.Verify(r => r.RemoveFragmentAsync(1, 10, It.IsAny<IDbTransaction?>()), Times.Once);
    }

    [Fact]
    public async Task RemoveRefusesProtectedAndPointsAtPropose()
    {
        var context = Context();
        context.AddFragment(Fragment(10, "note", isProtected: true));

        var result = await RunAsync(context, """{ "remove": { "id": 10 } }""");

        Assert.Contains("is protected", result);
        Assert.Contains("propose kind=remove", result);
        Assert.Contains(context.ContextFragments.Values, f => f.Id == 10); // untouched
    }

    [Fact]
    public async Task RemoveReportsNotInContext()
    {
        var result = await RunAsync(Context(), """{ "remove": { "id": 99 } }""");
        Assert.Contains("not found in current context", result);
    }

    [Fact]
    public async Task UpdateProtectedFragmentPointsAtPropose()
    {
        var context = Context();
        context.AddFragment(Fragment(10, "x", isProtected: true));

        var result = await RunAsync(context, """{ "update": { "id": 10, "content": "y" } }""");

        Assert.Contains("is protected", result);
        Assert.Contains("propose kind=modify", result);
        Assert.Equal("x", context.ContextFragments.Values.Single(f => f.Id == 10).Content); // unchanged
    }

    [Fact]
    public async Task UpdateFlagsUnrecognisedStatusButStillUpdates()
    {
        var context = Context();
        context.AddFragment(Fragment(10, "x"));

        var result = await RunAsync(context, """{ "update": { "id": 10, "content": "y", "status": "bogus" } }""");

        Assert.Contains("Updated fragment #10", result);
        Assert.Contains("not recognised", result);
        Assert.Equal("y", context.ContextFragments.Values.Single(f => f.Id == 10).Content);
    }

    [Fact]
    public async Task SetSummaryAppliesToFragmentsAndSkipsProtected()
    {
        var context = Context();
        context.AddFragment(Fragment(10, "a"));
        context.AddFragment(Fragment(11, "b", isProtected: true));

        var result = await RunAsync(context, """{ "set_summary": { "ids": [10, 11, 99], "summary": "short" } }""");

        Assert.Contains("Set summary on 1 fragment(s): #10", result);
        Assert.Contains("#11 (protected)", result);
        Assert.Contains("#99 (not in context)", result);
        Assert.Equal("short", context.ContextFragments.Values.Single(f => f.Id == 10).Summary);
    }

    [Fact]
    public async Task ToggleSummaryDisplaySkipsFragmentsWithoutASummary()
    {
        var context = Context();
        context.AddFragment(Fragment(10, "a", summary: "sum"));
        context.AddFragment(Fragment(11, "b")); // no summary

        var result = await RunAsync(context, """{ "toggle_summary_display": { "ids": [10, 11], "collapsed": true } }""");

        Assert.Contains("Collapsed 1 fragment(s): #10", result);
        Assert.Contains("#11 (no summary", result);
        Assert.True(context.ContextFragments.Values.Single(f => f.Id == 10).Collapsed);
    }

    [Fact]
    public async Task SummarizeFoldsFragmentsIntoASummaryAndArchivesOriginals()
    {
        var context = Context();
        context.AddFragment(Fragment(10, "first"));
        context.AddFragment(Fragment(11, "second"));

        var result = await RunAsync(context, """{ "summarize_fragments": { "ids": [10, 11], "summary": "both points" } }""");

        Assert.Contains("Folded 2 fragment(s)", result);
        // Originals detached, a new Summary fragment added in their place.
        Assert.DoesNotContain(context.ContextFragments.Values, f => f.Id is 10 or 11);
        Assert.Contains(context.ContextFragments.Values, f => f.FragmentType == ContextFragmentType.Summary && f.Content == "both points");
        workingContextRepo.Verify(r => r.RemoveFragmentAsync(1, 10, It.IsAny<IDbTransaction?>()), Times.Once);
        workingContextRepo.Verify(r => r.RemoveFragmentAsync(1, 11, It.IsAny<IDbTransaction?>()), Times.Once);
    }

    [Fact]
    public async Task SummarizeFailsWhenNothingIsEligible()
    {
        var context = Context();
        context.AddFragment(Fragment(10, "p", isProtected: true));

        var result = await RunAsync(context, """{ "summarize_fragments": { "ids": [10], "summary": "x" } }""");

        Assert.Contains("none of the given fragments could be summarised", result);
        Assert.Contains("#10 (protected)", result);
    }
}
