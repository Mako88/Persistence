using Moq;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// Unit tests for semantic associative recall: the vector maths, the rank fusion that blends the
/// semantic and lexical rankings, and — most importantly — that every failure path still returns the
/// lexical result rather than failing the turn.
/// </summary>
public class EmbeddingMemorySurfacerTests
{
    // -- Vector maths --

    [Fact]
    public void Cosine_IsOneForIdenticalAndZeroForOrthogonal()
    {
        Assert.Equal(1.0, VectorMath.Cosine([1f, 2f, 3f], [1f, 2f, 3f]), 6);
        Assert.Equal(0.0, VectorMath.Cosine([1f, 0f], [0f, 1f]), 6);
        Assert.Equal(-1.0, VectorMath.Cosine([1f, 0f], [-1f, 0f]), 6);
    }

    [Fact]
    public void Cosine_ReturnsZeroForUncomparableVectorsRatherThanThrowing()
    {
        // A vector we cannot compare is "unrelated", so one bad row drops out of the ranking
        // instead of taking the whole recall down.
        Assert.Equal(0.0, VectorMath.Cosine([1f, 2f], [1f, 2f, 3f]));
        Assert.Equal(0.0, VectorMath.Cosine([0f, 0f], [1f, 2f]));
        Assert.Equal(0.0, VectorMath.Cosine([], []));
    }

    [Fact]
    public void Blob_RoundTripsAndRejectsTruncatedData()
    {
        float[] vector = [0.5f, -1.25f, 3.0f, 0f];

        Assert.Equal(vector, VectorMath.FromBlob(VectorMath.ToBlob(vector)));

        var truncated = VectorMath.ToBlob(vector)[..7]; // not a whole number of float32s
        Assert.Empty(VectorMath.FromBlob(truncated));
        Assert.Empty(VectorMath.FromBlob(null));
    }

    [Fact]
    public void ContentHash_IsStableForSameTextAndDiffersForChangedText()
    {
        Assert.Equal(VectorMath.ContentHash("a memory"), VectorMath.ContentHash("a memory"));
        Assert.NotEqual(VectorMath.ContentHash("a memory"), VectorMath.ContentHash("a memory."));
    }

    // -- Rank fusion --

    [Fact]
    public void Fuse_PrefersAgreementOverASingleRankingsTopHit()
    {
        // The whole point of fusing: B is only mid-ranked in each list, but both lists like it, and
        // that beats A and D which each top exactly one list.
        var a = Frag(1);
        var b = Frag(2);
        var c = Frag(3);
        var d = Frag(4);

        var fused = EmbeddingMemorySurfacer.Fuse([a, b, c], [d, b, c], k: 60, count: 3);

        Assert.Equal(2, fused[0].Id);
    }

    [Fact]
    public void Fuse_KeepsItemsFoundByOnlyOneRanking()
    {
        // Semantic-only hits must survive fusion — reaching memories BM25 cannot see is the reason
        // this exists at all.
        var fused = EmbeddingMemorySurfacer.Fuse([Frag(1)], [Frag(2)], k: 60, count: 5);

        Assert.Equal([1L, 2L], fused.Select(f => f.Id).OrderBy(x => x));
    }

    // -- Embedding response parsing --

    [Fact]
    public void ParseEmbeddings_OrdersByResponseIndexNotArrayOrder()
    {
        // The API may return results out of order; trusting array order would pair a vector with
        // the wrong fragment, quietly attaching one memory's meaning to another.
        const string json = """
            {"data":[{"index":1,"embedding":[9.0,9.0]},{"index":0,"embedding":[1.0,1.0]}]}
            """;

        var vectors = OpenAiEmbeddingClient.ParseEmbeddings(json);

        Assert.Equal(2, vectors.Count);
        Assert.Equal([1f, 1f], vectors[0]);
        Assert.Equal([9f, 9f], vectors[1]);
    }

    [Fact]
    public void ParseEmbeddings_MeanPoolsAnUnpooledPerTokenResponse()
    {
        // A llama.cpp server run without --pooling mean returns one vector per token. Mean-pooling it
        // here is what the pooled server would have done, so a misconfigured server gives slightly
        // different vectors rather than silently no semantic recall at all.
        const string json = """
            {"data":[{"index":0,"embedding":[[1.0,3.0],[3.0,5.0]]}]}
            """;

        var vectors = OpenAiEmbeddingClient.ParseEmbeddings(json);

        Assert.Single(vectors);
        Assert.Equal([2f, 4f], vectors[0]);
    }

    [Fact]
    public void ParseEmbeddings_ReturnsEmptyForUnusableResponses()
    {
        Assert.Empty(OpenAiEmbeddingClient.ParseEmbeddings(null));
        Assert.Empty(OpenAiEmbeddingClient.ParseEmbeddings("{}"));
        Assert.Empty(OpenAiEmbeddingClient.ParseEmbeddings("""{"data":"nope"}"""));
    }

    // -- End-to-end surfacing --

    [Fact]
    public async Task SurfaceAsync_UsesLexicalResultWhenEmbeddingIsDisabled()
    {
        var surfacer = Build(
            lexicalHits: [Frag(1), Frag(2)],
            embeddings: new FakeEmbeddingClient { IsEnabled = false },
            stored: []);

        var surfaced = await surfacer.SurfaceAsync([], "intent and outcome", count: 2, CancellationToken.None);

        Assert.Equal([1L, 2L], surfaced.Select(f => f.Id));
    }

    [Fact]
    public async Task SurfaceAsync_FallsBackToLexicalWhenTheEmbeddingServiceIsUnreachable()
    {
        // A wedged embedding server must cost the peer its semantic reach, not its turn.
        var surfacer = Build(
            lexicalHits: [Frag(1), Frag(2)],
            embeddings: new FakeEmbeddingClient { Throws = true },
            stored: [(3L, [1f, 0f])]);

        var surfaced = await surfacer.SurfaceAsync([], "intent and outcome", count: 2, CancellationToken.None);

        Assert.Equal([1L, 2L], surfaced.Select(f => f.Id));
    }

    [Fact]
    public async Task SurfaceAsync_SurfacesAMemoryTheLexicalPathCannotReach()
    {
        // Fragment 7 shares no words with the query, so BM25 never returns it; its vector does.
        var reachedOnlyByMeaning = Frag(7);

        var surfacer = Build(
            lexicalHits: [Frag(1)],
            embeddings: new FakeEmbeddingClient { QueryVector = [1f, 0f] },
            stored: [(7L, [1f, 0f])],
            knownFragments: [reachedOnlyByMeaning]);

        var surfaced = await surfacer.SurfaceAsync([], "believing something happened", count: 5, CancellationToken.None);

        Assert.Contains(surfaced, f => f.Id == 7);
    }

    [Fact]
    public async Task SurfaceAsync_DoesNotSurfaceFragmentsAlreadyInContext()
    {
        var surfacer = Build(
            lexicalHits: [],
            embeddings: new FakeEmbeddingClient { QueryVector = [1f, 0f] },
            stored: [(7L, [1f, 0f])],
            knownFragments: [Frag(7)]);

        var surfaced = await surfacer.SurfaceAsync([7L], "anything", count: 5, CancellationToken.None);

        Assert.Empty(surfaced);
    }

    [Fact]
    public async Task Backfill_SkipsFragmentsWhoseTextIsUnchanged()
    {
        // The stale query is a timestamp prefilter, so a re-tagged fragment lands here with identical
        // text. The content hash must keep that free rather than paying to re-embed it.
        var fragment = Frag(7);
        var embeddingRepo = new FakeEmbeddingRepository
        {
            Stale = [7L],
            Stored = { [7L] = new StoredEmbedding(7L, [1f, 0f], VectorMath.ContentHash(fragment.Content)) },
        };
        var client = new FakeEmbeddingClient { QueryVector = [1f, 0f] };

        var surfacer = Build(
            lexicalHits: [],
            embeddings: client,
            embeddingRepo: embeddingRepo,
            knownFragments: [fragment]);

        await surfacer.SurfaceAsync([], "anything", count: 5, CancellationToken.None);

        Assert.Empty(embeddingRepo.Upserted);              // nothing re-embedded
        Assert.Equal(1, client.EmbedCallCount);            // only the query itself
    }

    [Fact]
    public async Task Backfill_EmbedsAndStoresFragmentsThatHaveNoVector()
    {
        var fragment = Frag(7);
        var embeddingRepo = new FakeEmbeddingRepository { Stale = [7L] };

        var surfacer = Build(
            lexicalHits: [],
            embeddings: new FakeEmbeddingClient { QueryVector = [1f, 0f] },
            embeddingRepo: embeddingRepo,
            knownFragments: [fragment]);

        await surfacer.SurfaceAsync([], "anything", count: 5, CancellationToken.None);

        Assert.Equal([7L], embeddingRepo.Upserted);
    }

    [Fact]
    public async Task SemanticMatchOnASupersededFragment_ReturnsTheHeadOfItsChain_Once()
    {
        var old = Frag(1);
        old.Status = ContextFragmentStatus.Archived;
        old.SupersededById = 2;
        var middle = Frag(2);
        middle.Status = ContextFragmentStatus.Archived;
        middle.SupersededById = 3;
        var head = Frag(3);

        var surfacer = Build(
            lexicalHits: [],
            embeddings: new FakeEmbeddingClient { QueryVector = [1f, 0f] },
            stored: [(1L, [1f, 0f]), (2L, [0.9f, 0.1f]), (3L, [0f, 1f])],
            knownFragments: [old, middle, head],
            heads: new Dictionary<long, (long, int)> { [1] = (3, 2), [2] = (3, 1) });

        var surfaced = await surfacer.SurfaceAsync([], "anything", count: 5, CancellationToken.None);

        Assert.Equal([3L], surfaced.Select(f => f.Id));
    }

    [Fact]
    public async Task SemanticMatchOnASupersededFragment_DropsItWhenTheHeadIsExcluded()
    {
        var old = Frag(1);
        old.Status = ContextFragmentStatus.Archived;
        old.SupersededById = 3;

        var surfacer = Build(
            lexicalHits: [],
            embeddings: new FakeEmbeddingClient { QueryVector = [1f, 0f] },
            stored: [(1L, [1f, 0f])],
            knownFragments: [old, Frag(3)],
            heads: new Dictionary<long, (long, int)> { [1] = (3, 1) });

        Assert.Empty(await surfacer.SurfaceAsync([3L], "anything", count: 5, CancellationToken.None));
    }

    // -- Helpers --

    private static ContextFragmentEntity Frag(long id, ContextFragmentType type = ContextFragmentType.Personal) => new()
    {
        Id = id,
        FragmentType = type,
        Status = ContextFragmentStatus.Active,
        Content = $"fragment {id}",
        Importance = 0.6f,
        Confidence = 0.6f,
        CreatedUtc = DateTimeOffset.UtcNow,
        LastModifiedUtc = DateTimeOffset.UtcNow,
    };

    private static EmbeddingMemorySurfacer Build(
        IReadOnlyList<ContextFragmentEntity> lexicalHits,
        FakeEmbeddingClient embeddings,
        IReadOnlyList<(long id, float[] vector)>? stored = null,
        FakeEmbeddingRepository? embeddingRepo = null,
        IReadOnlyList<ContextFragmentEntity>? knownFragments = null,
        IReadOnlyDictionary<long, (long HeadId, int Hops)>? heads = null)
    {
        var fragmentRepo = new Mock<IContextFragmentRepository>();

        fragmentRepo
            .Setup(r => r.GetHeadsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<long> ids, CancellationToken _) =>
                (IReadOnlyDictionary<long, (long HeadId, int Hops)>)(heads ?? new Dictionary<long, (long, int)>())
                    .Where(h => ids.Contains(h.Key)).ToDictionary(h => h.Key, h => h.Value));

        fragmentRepo
            .Setup(r => r.SearchRelevantAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lexicalHits);

        var known = (knownFragments ?? []).ToDictionary(f => f.Id);

        fragmentRepo
            .Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<long> ids, CancellationToken _) =>
                ids.Where(known.ContainsKey).Select(id => known[id]).ToList());

        var repo = embeddingRepo ?? new FakeEmbeddingRepository();

        foreach (var (id, vector) in stored ?? [])
        {
            repo.Stored[id] = new StoredEmbedding(id, vector, "hash");
        }

        var config = new Mock<IAppConfig>();
        config.Setup(c => c.Embedding).Returns(new EmbeddingSettings
        {
            Enabled = true,
            ApiBaseUrl = "http://localhost:8081/v1",
            BackfillPerTurn = 32,
            FusionK = 60,
        });

        return new EmbeddingMemorySurfacer(
            new MemorySurfacer(fragmentRepo.Object), embeddings, repo, fragmentRepo.Object, config.Object);
    }

    private sealed class FakeEmbeddingClient : IEmbeddingClient
    {
        public bool IsEnabled { get; set; } = true;
        public string Model => "fake-embed";
        public bool Throws { get; set; }
        public float[] QueryVector { get; set; } = [1f, 0f];
        public int EmbedCallCount { get; private set; }

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            EmbedCallCount++;

            if (Throws)
            {
                throw new InvalidOperationException("embedding service unreachable");
            }

            return Task.FromResult<IReadOnlyList<float[]>>(
                texts.Select(_ => QueryVector).ToList());
        }
    }

    private sealed class FakeEmbeddingRepository : IFragmentEmbeddingRepository
    {
        public Dictionary<long, StoredEmbedding> Stored { get; } = [];
        public List<long> Stale { get; set; } = [];
        public List<long> Upserted { get; } = [];

        public Task<IReadOnlyList<StoredEmbedding>> GetAllAsync(string model, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredEmbedding>>(Stored.Values.ToList());

        public Task<IReadOnlyList<long>> GetStaleFragmentIdsAsync(
            string model, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<long>>(Stale.Take(limit).ToList());

        public Task<IReadOnlyList<long>> GetUnembeddedIdsAsync(
            string model, ContextFragmentType type, int minLength, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<long>>(Stale.Take(limit).ToList());

        public Task UpsertAsync(
            string model, long fragmentId, float[] vector, string contentHash, CancellationToken ct = default)
        {
            Upserted.Add(fragmentId);
            Stored[fragmentId] = new StoredEmbedding(fragmentId, vector, contentHash);
            return Task.CompletedTask;
        }
    }
}
