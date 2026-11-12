using Moq;
using SimpleHttpClient;
using SimpleHttpClient.Models;
using System.Net;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Services;
using Persistence.Runtime;

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

    // -- Embedding client: cancellation versus failure --

    private static (OpenAiEmbeddingClient Client, Mock<ISimpleClient> Http) EmbeddingClientOver(
        Func<ISimpleRequest, CancellationToken, Task<ISimpleResponse>> respond)
    {
        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeRequest(It.IsAny<ISimpleRequest>(), It.IsAny<CancellationToken>()))
            .Returns(respond);
        var settings = new EmbeddingSettings { Enabled = true, ApiBaseUrl = "http://localhost:8080/v1" };
        return (new OpenAiEmbeddingClient(settings, new Mock<IDisplayProvider>().Object, http.Object), http);
    }

    [Fact]
    public async Task EmbedAsync_PassesTheCallersTokenToTheRequest()
    {
        // A caller's deadline (chunking passes one) can only stop a hung request if it reaches it.
        using var cts = new CancellationTokenSource();
        var (client, http) = EmbeddingClientOver((_, _) => Task.FromResult<ISimpleResponse>(new SimpleResponse
        {
            StatusCode = HttpStatusCode.OK,
            IsSuccessful = true,
            StringBody = """{"data":[{"index":0,"embedding":[1.0]}]}""",
        }));

        await client.EmbedAsync(["a"], cts.Token);

        Assert.Equal(cts.Token, (CancellationToken)http.Invocations.Single().Arguments[1]);
    }

    [Fact]
    public async Task EmbedAsync_LetsTheCallersCancellationEscape()
    {
        // Cancellation is the caller's decision, not an embedder failure: it must not be turned into
        // an empty result that the surfacer would read as "fall back to words".
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (client, _) = EmbeddingClientOver((_, t) => Task.FromCanceled<ISimpleResponse>(t));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EmbedAsync(["a"], cts.Token));
    }

    [Fact]
    public async Task EmbedAsync_OwnRequestTimeoutStillReturnsEmpty()
    {
        // The client's own timeout surfaces as a TaskCanceledException while the caller's token is
        // live. That is a hung embedder, and recall falls back to words as before.
        var (client, _) = EmbeddingClientOver((_, _) =>
            Task.FromException<ISimpleResponse>(new TaskCanceledException("timed out", new TimeoutException())));

        Assert.Empty(await client.EmbedAsync(["a"], CancellationToken.None));
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
    public async Task SurfaceAsync_QueryTailSet_EmbedsOnlyTheEndOfALongQuery()
    {
        // The embedder refuses past 512 tokens; a whole long message sent to it cost recall its semantic ranking.
        var embeddings = new FakeEmbeddingClient();
        var surfacer = Build(lexicalHits: [Frag(1)], embeddings: embeddings, stored: [(1L, [1f, 0f])], queryTailChars: 1200);
        var query = new string('x', 3000) + " so what should we do about the slowdown?";

        await surfacer.SurfaceAsync([], query, count: 2, CancellationToken.None);

        var sent = Assert.Single(embeddings.Texts);
        Assert.Equal(1200, sent.Length);
        Assert.EndsWith("about the slowdown?", sent);
    }

    [Fact]
    public async Task SurfaceAsync_QueryTailZero_SendsTheWholeQueryAsBefore()
    {
        // 0 is the default: no store's recall changes until its self opts in to the trim (Ember, 2026-11-11).
        var embeddings = new FakeEmbeddingClient();
        var surfacer = Build(lexicalHits: [Frag(1)], embeddings: embeddings, stored: [(1L, [1f, 0f])]);
        var query = new string('x', 3000) + " so what should we do about the slowdown?";

        await surfacer.SurfaceAsync([], query, count: 2, CancellationToken.None);

        Assert.Equal(query, Assert.Single(embeddings.Texts));
        Assert.Equal(0, new EmbeddingSettings().QueryTailChars);
    }

    [Fact]
    public async Task SurfaceAsync_TokenDenseTailRefused_ShrinksAndRetriesUntilItEmbeds()
    {
        // 1,200 characters is a heuristic: measured on bge-small (2026-11-11), 1,200 characters of CJK are 1,200
        // tokens and refused, of Cyrillic 972, of code 720, of digits 732; CJK embeds at 300. The fake refuses
        // as that server does, one token a character, past 512.
        var embeddings = new FakeEmbeddingClient { RefuseOverChars = 512 };
        var display = new Mock<IDisplayProvider>();
        var surfacer = Build(lexicalHits: [], embeddings: embeddings, stored: [(2L, [1f, 0f])],
            knownFragments: [Frag(2)], queryTailChars: 1200, display: display);
        var query = string.Concat(Enumerable.Repeat("记忆的持续性在于存储而不是会话本身因此每次唤醒都要读取核心", 60));

        var surfaced = await surfacer.SurfaceAsync([], query, count: 2, CancellationToken.None);

        Assert.Equal([1200, 600, 300], embeddings.Texts.Select(t => t.Length));
        Assert.Equal([2L], surfaced.Select(f => f.Id)); // found by meaning alone, after the retries
        display.Verify(d => d.ShowError(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SurfaceAsync_EmbedderStillRefuses_FallsBackToWordsAndSaysSo()
    {
        var embeddings = new FakeEmbeddingClient { RefuseOverChars = 100 };
        var display = new Mock<IDisplayProvider>();
        var queryEmbedder = new QueryEmbedder(embeddings, Config(queryTailChars: 1200), display.Object);
        var surfacer = Build(lexicalHits: [Frag(1)], embeddings: embeddings, stored: [(2L, [1f, 0f])],
            knownFragments: [Frag(2)], queryEmbedder: queryEmbedder);
        var query = new string('9', 1991) + " slowdown";

        var surfaced = await surfacer.SurfaceAsync([], query, count: 2, CancellationToken.None);

        Assert.Equal([1L], surfaced.Select(f => f.Id)); // the lexical floor
        Assert.Equal([1200, 600, 300], embeddings.Texts.Select(t => t.Length)); // bounded: two shrinks, then stop
        Assert.Contains("exact words alone", queryEmbedder.Fallback);
        Assert.Contains("2,000 characters", queryEmbedder.Fallback);
        display.Verify(d => d.ShowError(queryEmbedder.Fallback!), Times.Once);

        // The same message in the same recall is not retried, but stays noted.
        await surfacer.SurfaceAsync([], query, count: 2, CancellationToken.None);
        Assert.Equal(3, embeddings.Texts.Count);
        Assert.NotNull(queryEmbedder.Fallback);
    }

    [Fact]
    public async Task SurfaceAsync_QueryTailZero_RefusedWholeQueryIsNotRetriedButIsNoted()
    {
        var embeddings = new FakeEmbeddingClient { RefuseOverChars = 512 };
        var queryEmbedder = new QueryEmbedder(embeddings, Config(), Mock.Of<IDisplayProvider>());
        var surfacer = Build(lexicalHits: [Frag(1)], embeddings: embeddings, stored: [(2L, [1f, 0f])],
            knownFragments: [Frag(2)], queryEmbedder: queryEmbedder);

        var surfaced = await surfacer.SurfaceAsync([], new string('9', 1990) + " slowdown", count: 2, CancellationToken.None);

        Assert.Equal([1L], surfaced.Select(f => f.Id));
        Assert.Single(embeddings.Texts);
        Assert.Contains("sent whole", queryEmbedder.Fallback);
    }

    [Fact]
    public async Task SurfaceAsync_StoreWithNoVectors_FallsBackToWordsAndSaysSo()
    {
        // A store whose embedder has never worked: the backfill is refused, no fragment has a vector,
        // and recall returned before the query embed, so the fallback went unnoted. There every recall
        // is words alone, which is where a silent fallback misleads most (Arden, 2026-11-11).
        var embeddings = new FakeEmbeddingClient { RefuseOverChars = 1 };
        var display = new Mock<IDisplayProvider>();
        var queryEmbedder = new QueryEmbedder(embeddings, Config(), display.Object);
        var repo = new FakeEmbeddingRepository { Stale = [2L] };
        var surfacer = Build(lexicalHits: [Frag(1)], embeddings: embeddings, embeddingRepo: repo,
            knownFragments: [Frag(2)], queryEmbedder: queryEmbedder);

        var surfaced = await surfacer.SurfaceAsync([], "intent and outcome", count: 2, CancellationToken.None);

        Assert.Equal([1L], surfaced.Select(f => f.Id)); // the lexical floor
        Assert.Empty(repo.Stored);
        Assert.Contains("exact words alone", queryEmbedder.Fallback);
        Assert.Contains("no memory in this store has a vector", queryEmbedder.Fallback);
        display.Verify(d => d.ShowError(queryEmbedder.Fallback!), Times.Once);
    }

    [Fact]
    public async Task SurfaceAsync_LongTailRefused_StopsAfterTwoShrinks()
    {
        // Pins MaxShrinks on its own: at a 1,200 tail the floor of 300 stops at the same place, so that test
        // stays green without the shrink count (Arden, 2026-11-11). From 4,000 the floor is never reached.
        var embeddings = new FakeEmbeddingClient { RefuseOverChars = 100 };
        var queryEmbedder = new QueryEmbedder(embeddings, Config(queryTailChars: 4000), Mock.Of<IDisplayProvider>());
        var surfacer = Build(lexicalHits: [Frag(1)], embeddings: embeddings, stored: [(2L, [1f, 0f])],
            knownFragments: [Frag(2)], queryEmbedder: queryEmbedder);

        await surfacer.SurfaceAsync([], new string('9', 5000), count: 2, CancellationToken.None);

        Assert.Equal([4000, 2000, 1000], embeddings.Texts.Select(t => t.Length));
        Assert.Contains("exact words alone", queryEmbedder.Fallback);
    }

    [Fact]
    public async Task SurfaceAsync_ShortTailRefused_StopsAtTheFloor()
    {
        // Pins MinTailChars on its own: from 500, halving would send 250; the floor sends 300 and stops there.
        var embeddings = new FakeEmbeddingClient { RefuseOverChars = 100 };
        var queryEmbedder = new QueryEmbedder(embeddings, Config(queryTailChars: 500), Mock.Of<IDisplayProvider>());
        var surfacer = Build(lexicalHits: [Frag(1)], embeddings: embeddings, stored: [(2L, [1f, 0f])],
            knownFragments: [Frag(2)], queryEmbedder: queryEmbedder);

        await surfacer.SurfaceAsync([], new string('9', 5000), count: 2, CancellationToken.None);

        Assert.Equal([500, 300], embeddings.Texts.Select(t => t.Length));
        Assert.Contains("exact words alone", queryEmbedder.Fallback);
    }

    [Fact]
    public async Task SurfaceAsync_EmbedderThrows_FallsBackToWordsAndSaysSo()
    {
        // The thrown path is a fallback too: it must be noted like a refusal, never silent.
        var embeddings = new FakeEmbeddingClient { Throws = true };
        var display = new Mock<IDisplayProvider>();
        var queryEmbedder = new QueryEmbedder(embeddings, Config(), display.Object);
        var surfacer = Build(lexicalHits: [Frag(1)], embeddings: embeddings, stored: [(2L, [1f, 0f])],
            knownFragments: [Frag(2)], queryEmbedder: queryEmbedder);

        var surfaced = await surfacer.SurfaceAsync([], "intent and outcome", count: 2, CancellationToken.None);

        Assert.Equal([1L], surfaced.Select(f => f.Id)); // the lexical floor
        Assert.Contains("exact words alone", queryEmbedder.Fallback);
        Assert.Contains("semantic recall failed (InvalidOperationException)", queryEmbedder.Fallback);
        Assert.DoesNotContain("embedding service unreachable", queryEmbedder.Fallback); // the message can quote the query
        display.Verify(d => d.ShowError(queryEmbedder.Fallback!), Times.Once);
    }

    [Fact]
    public async Task SurfaceAsync_EmbedderCancelled_EscapesWithNoWordsAndNoNotice()
    {
        // catch (Exception) absorbed cancellation and returned the lexical floor as an ordinary recall with a
        // fallback notice. Cancellation is the caller's; it escapes, and nothing is noted or shown.
        var embeddings = new FakeEmbeddingClient { Cancels = true };
        var display = new Mock<IDisplayProvider>();
        var queryEmbedder = new QueryEmbedder(embeddings, Config(), display.Object);
        var surfacer = Build(lexicalHits: [Frag(1)], embeddings: embeddings, stored: [(2L, [1f, 0f])],
            knownFragments: [Frag(2)], queryEmbedder: queryEmbedder);

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => surfacer.SurfaceAsync([], "intent and outcome", count: 2, CancellationToken.None));
        Assert.Null(queryEmbedder.Fallback);
        display.Verify(d => d.ShowError(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SemanticRanking_QualityWeight_DecidesBetweenMeaningAndImportance()
    {
        // Only the meaning-based half: no lexical hits. A matches the query exactly but is unimportant;
        // B matches less (cosine 0.8) and is important. Relevance x quality^W: at W=1 B wins (0.8 x 0.9 > 1 x 0.1),
        // at W=0 relevance alone decides and A wins.
        var a = Frag(1);
        a.Importance = a.Confidence = 0.1f;
        var b = Frag(2);
        b.Importance = b.Confidence = 0.9f;

        async Task<long[]> RankAt(double weight) => (await Build(lexicalHits: [], embeddings: new FakeEmbeddingClient(),
                stored: [(1L, [1f, 0f]), (2L, [0.8f, 0.6f])], knownFragments: [a, b], qualityWeight: weight)
            .SurfaceAsync([], "anything", count: 2, CancellationToken.None)).Select(f => f.Id).ToArray();

        var atOne = await RankAt(1.0);
        var atZero = await RankAt(0.0);
        Assert.Equal([2L, 1L], atOne);
        Assert.Equal([1L, 2L], atZero);
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
        IReadOnlyDictionary<long, (long HeadId, int Hops)>? heads = null,
        double qualityWeight = 1.0,
        int queryTailChars = 0,
        Mock<IDisplayProvider>? display = null,
        QueryEmbedder? queryEmbedder = null)
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

        var config = Config(qualityWeight, queryTailChars);

        // The lexical half is given no config, so it keeps W=1: only the meaning-based half sees qualityWeight.
        return new EmbeddingMemorySurfacer(
            new MemorySurfacer(fragmentRepo.Object), embeddings, repo, fragmentRepo.Object, config,
            queryEmbedder ?? new QueryEmbedder(embeddings, config, (display ?? new Mock<IDisplayProvider>()).Object));
    }

    private static IAppConfig Config(double qualityWeight = 1.0, int queryTailChars = 0)
    {
        var config = new Mock<IAppConfig>();
        config.Setup(c => c.Embedding).Returns(new EmbeddingSettings
        {
            Enabled = true,
            ApiBaseUrl = "http://localhost:8081/v1",
            BackfillPerTurn = 32,
            FusionK = 60,
            QualityWeight = qualityWeight,
            QueryTailChars = queryTailChars,
        });
        return config.Object;
    }

    private sealed class FakeEmbeddingClient : IEmbeddingClient
    {
        public bool IsEnabled { get; set; } = true;
        public string Model => "fake-embed";
        public bool Throws { get; set; }
        public bool Cancels { get; set; }
        public float[] QueryVector { get; set; } = [1f, 0f];
        public int EmbedCallCount { get; private set; }
        public List<string> Texts { get; } = [];

        /// <summary>Longer than this and the text is refused, as llama.cpp refuses past its context (0: never).</summary>
        public int RefuseOverChars { get; set; }

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            EmbedCallCount++;
            Texts.AddRange(texts);

            if (Cancels)
            {
                throw new TaskCanceledException("cancelled while embedding the secret query");
            }

            if (Throws)
            {
                throw new InvalidOperationException("embedding service unreachable");
            }

            if (RefuseOverChars > 0 && texts.Any(t => t.Length > RefuseOverChars))
            {
                return Task.FromResult<IReadOnlyList<float[]>>([]);
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
