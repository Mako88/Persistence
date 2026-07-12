using Moq;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// Unit tests for associative recall: the FTS query it builds from conversation text, and how it
/// filters (authored, not-in-context) and re-ranks (relevance × importance × confidence) candidates.
/// </summary>
public class MemorySurfacerTests
{
    // -- Query building --

    [Fact]
    public void BuildFtsQuery_KeepsSalientTermsQuotedAndOred()
    {
        var q = MemorySurfacer.BuildFtsQuery("The peer values honesty and continuity across sessions");

        Assert.NotNull(q);
        Assert.Contains("\"honesty\"", q);
        Assert.Contains("\"continuity\"", q);
        Assert.Contains(" OR ", q);
        Assert.DoesNotContain("\"the\"", q);  // stop-word
        Assert.DoesNotContain("\"and\"", q);  // stop-word
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the and but you are with that")] // all stop-words / too short
    public void BuildFtsQuery_ReturnsNullWhenNothingSearchable(string text)
    {
        Assert.Null(MemorySurfacer.BuildFtsQuery(text));
    }

    // -- Filtering + ranking --

    private static ContextFragmentEntity Frag(long id, ContextFragmentType type, float imp, float conf) => new()
    {
        Id = id,
        FragmentType = type,
        Status = ContextFragmentStatus.Active,
        Content = $"fragment {id}",
        Importance = imp,
        Confidence = conf,
        CreatedUtc = DateTimeOffset.UtcNow,
        LastModifiedUtc = DateTimeOffset.UtcNow,
    };

    private static MemorySurfacer WithResults(params ContextFragmentEntity[] bm25Ordered)
    {
        var repo = new Mock<IContextFragmentRepository>();
        repo.Setup(r => r.SearchRelevantAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(bm25Ordered);
        return new MemorySurfacer(repo.Object);
    }

    [Fact]
    public async Task SurfaceAsync_DropsNonAuthoredTypesAndAlreadyLoadedFragments()
    {
        var surfacer = WithResults(
            Frag(1, ContextFragmentType.Personal, 0.5f, 0.5f),
            Frag(2, ContextFragmentType.ChatMessage, 0.9f, 0.9f),   // not authored — dropped
            Frag(3, ContextFragmentType.Thought, 0.9f, 0.9f),       // not authored — dropped
            Frag(4, ContextFragmentType.Identity, 0.5f, 0.5f),
            Frag(5, ContextFragmentType.Personal, 0.9f, 0.9f));     // already in context — dropped

        var surfaced = await surfacer.SurfaceAsync(new[] { 5L }, "honesty continuity", count: 10, CancellationToken.None);

        Assert.Equal(new long[] { 1, 4 }, surfaced.Select(f => f.Id).OrderBy(x => x));
        Assert.All(surfaced, f => Assert.Contains(f.FragmentType, FragmentTypeRules.Authorable));
    }

    [Fact]
    public async Task SurfaceAsync_RanksByRelevanceBlendedWithImportanceAndConfidence()
    {
        // BM25 order: #1 (top match, mid quality), #2 (lower match, high quality), #3 (lowest, low quality).
        var surfacer = WithResults(
            Frag(1, ContextFragmentType.Personal, 0.5f, 0.5f),
            Frag(2, ContextFragmentType.Personal, 0.9f, 0.9f),
            Frag(3, ContextFragmentType.Personal, 0.4f, 0.4f));

        var surfaced = await surfacer.SurfaceAsync(Array.Empty<long>(), "honesty", count: 2, CancellationToken.None);

        // #2's higher importance/confidence out-ranks #1's better relevance; #3 (low on both) falls off.
        Assert.Equal(new long[] { 2, 1 }, surfaced.Select(f => f.Id));
    }

    [Fact]
    public async Task SurfaceAsync_DropsDeletedAndArchivedFragments()
    {
        var deleted = Frag(1, ContextFragmentType.Personal, 0.9f, 0.9f);
        deleted.IsDeleted = true;
        var archived = Frag(2, ContextFragmentType.Personal, 0.9f, 0.9f);
        archived.Status = ContextFragmentStatus.Archived;
        var keep = Frag(3, ContextFragmentType.Personal, 0.5f, 0.5f);

        var surfaced = await WithResults(deleted, archived, keep)
            .SurfaceAsync(Array.Empty<long>(), "honesty", count: 10, CancellationToken.None);

        Assert.Equal(new long[] { 3 }, surfaced.Select(f => f.Id)); // only the live one
    }

    [Fact]
    public async Task SurfaceAsync_SwallowsSearchErrorsInsteadOfBreakingTheTurn()
    {
        var repo = new Mock<IContextFragmentRepository>();
        repo.Setup(r => r.SearchRelevantAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("malformed FTS"));

        var surfaced = await new MemorySurfacer(repo.Object)
            .SurfaceAsync(Array.Empty<long>(), "honesty", count: 5, CancellationToken.None);

        Assert.Empty(surfaced); // a bad FTS expression must never crash the turn
    }

    [Fact]
    public async Task SurfaceAsync_ReturnsEmptyWhenDisabledOrNoTerms()
    {
        var surfacer = WithResults(Frag(1, ContextFragmentType.Personal, 0.9f, 0.9f));

        Assert.Empty(await surfacer.SurfaceAsync(Array.Empty<long>(), "honesty", count: 0, CancellationToken.None)); // disabled
        Assert.Empty(await surfacer.SurfaceAsync(Array.Empty<long>(), "the and but", count: 5, CancellationToken.None)); // no terms
    }
}
