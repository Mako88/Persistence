using Moq;
using Persistence.Config;
using Persistence.Runtime;
using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// The path contract on its own (Ember's CHANGES on f922439): a fallback in one part must never outrank a part that
/// compared meaning. Each case here is what the benchmark buckets a turn by.
/// </summary>
public sealed class RecallPathTests
{
    private static QueryEmbedder Query()
    {
        var embeddings = new Mock<IEmbeddingClient>();
        embeddings.SetupGet(e => e.IsEnabled).Returns(true);
        var config = new Mock<IAppConfig>();
        config.SetupGet(c => c.Embedding).Returns(new EmbeddingSettings());
        return new QueryEmbedder(embeddings.Object, config.Object, Mock.Of<IDisplayProvider>());
    }

    [Fact]
    public void A_fallback_never_outranks_a_part_that_compared_meaning()
    {
        foreach (var (compared, fellBack) in new[]
                 {
                     (QueryEmbedder.Fragments, QueryEmbedder.PastUtterances),
                     (QueryEmbedder.PastUtterances, QueryEmbedder.Fragments),
                 })
        {
            var query = Query();
            query.Compare(compared);
            query.Note("embedder down", fellBack);

            Assert.Equal((RecallPath.SemanticDegraded, $"{fellBack}: embedder down"), RecallPath.Of(true, query));
        }
    }

    [Fact]
    public void A_part_that_compared_and_then_fell_back_counts_as_words()
    {
        // Compared, then failed while hydrating: that part's results were words, so with no other part it is words-fallback.
        var query = Query();
        query.Compare(QueryEmbedder.Fragments);
        query.Note("semantic recall failed (disk)");

        Assert.Equal((RecallPath.WordsByFallback, "fragments: semantic recall failed (disk)"), RecallPath.Of(true, query));
    }

    [Fact]
    public void The_other_paths_hold()
    {
        var clean = Query();
        clean.Compare(QueryEmbedder.Fragments);
        clean.Compare(QueryEmbedder.PastUtterances);
        var words = Query();
        words.Note("no vector");

        Assert.Equal((RecallPath.Semantic, null), RecallPath.Of(true, clean));
        Assert.Equal((RecallPath.WordsByFallback, "fragments: no vector"), RecallPath.Of(true, words));
        Assert.Equal((RecallPath.Unknown, null), RecallPath.Of(true, Query()));
        Assert.Equal((RecallPath.WordsBySetting, "embedding is off"), RecallPath.Of(false, clean));

        clean.Clear();
        Assert.Equal((RecallPath.Unknown, null), RecallPath.Of(true, clean)); // a new recall starts clean
    }
}
