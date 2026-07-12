using Moq;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using Persistence.Runtime.ActionHandlers;
using Persistence.Services;
using System.Text.Json.Nodes;

namespace Persistence.Tests;

/// <summary>
/// <c>prune_candidates</c> surfaces the least-valuable authorable fragments in context (low
/// importance/confidence, long idle) as review candidates — read-only, and never protected anchors or
/// system-managed fragments.
/// </summary>
public class PruneCandidatesTests
{
    private readonly List<ToolInvoked> published = [];
    private readonly ManageContextHandler handler;

    public PruneCandidatesTests()
    {
        var bus = new EventBus();
        bus.Subscribe<ToolInvoked>((_, e) => { published.Add(e); return Task.CompletedTask; });

        handler = new ManageContextHandler(
            new Mock<IWorkingContextRepository>().Object,
            new Mock<IContextFragmentRepository>().Object,
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

    private static WeightedContextFragment Frag(
        long id, ContextFragmentType type, float importance, float confidence,
        double idleDays, string content, bool isProtected = false) =>
        new()
        {
            Id = id,
            FragmentType = type,
            Status = ContextFragmentStatus.Active,
            Importance = importance,
            Confidence = confidence,
            Relevance = 1.0f,
            IsProtected = isProtected,
            Content = content,
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-idleDays),
            LastModifiedUtc = DateTimeOffset.UtcNow.AddDays(-idleDays),
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
    public async Task RanksLowValueStaleFragmentsAheadOfValuableFreshOnes()
    {
        var context = ContextWith(
            Frag(1, ContextFragmentType.Personal, importance: 0.1f, confidence: 0.1f, idleDays: 60, content: "trivial aside"),
            Frag(2, ContextFragmentType.Personal, importance: 0.9f, confidence: 0.9f, idleDays: 0, content: "core belief"));

        var result = await RunAsync(context, """{ "prune_candidates": { "limit": 5 } }""");

        var lowValueIdx = result.IndexOf("#1", StringComparison.Ordinal);
        var highValueIdx = result.IndexOf("#2", StringComparison.Ordinal);
        Assert.True(lowValueIdx >= 0 && highValueIdx >= 0);
        Assert.True(lowValueIdx < highValueIdx, "the low-value, stale fragment should rank first");
        Assert.Contains("trivial aside", result);
    }

    [Fact]
    public async Task NeverSuggestsProtectedAnchorsOrSystemManagedFragments()
    {
        var context = ContextWith(
            Frag(1, ContextFragmentType.Identity, importance: 0.1f, confidence: 0.1f, idleDays: 90, content: "protected identity", isProtected: true),
            Frag(2, ContextFragmentType.ChatMessage, importance: 0.1f, confidence: 0.1f, idleDays: 90, content: "old chat line"),
            Frag(3, ContextFragmentType.Personal, importance: 0.2f, confidence: 0.2f, idleDays: 40, content: "prunable note"));

        var result = await RunAsync(context, """{ "prune_candidates": {} }""");

        Assert.Contains("prunable note", result);          // authorable, unprotected → a candidate
        Assert.DoesNotContain("protected identity", result); // protected anchor excluded
        Assert.DoesNotContain("old chat line", result);      // system-managed (decays on its own) excluded
    }

    [Fact]
    public async Task ReportsNothingToPruneWhenOnlyProtectedOrSystemFragmentsArePresent()
    {
        var context = ContextWith(
            Frag(1, ContextFragmentType.Identity, importance: 0.1f, confidence: 0.1f, idleDays: 90, content: "anchor", isProtected: true),
            Frag(2, ContextFragmentType.Thought, importance: 0.1f, confidence: 0.1f, idleDays: 90, content: "a thought"));

        var result = await RunAsync(context, """{ "prune_candidates": {} }""");

        Assert.Contains("Nothing stands out to prune", result);
    }
}
