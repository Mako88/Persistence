using Moq;
using SimpleHttpClient;
using SimpleHttpClient.Models;
using System.Net;
using Persistence.Config;
using Persistence.Runtime;
using Persistence.Services;

namespace Persistence.Tests;

/// <summary>
/// Chunked query embedding (docs/design/chunked-query-embedding.md, 63b2f54): a query longer than the embedder reads
/// is cut into ordered, overlapping chunks covering all of it, each embedded and kept; what could not be embedded is
/// reported by the characters it leaves uncovered, never dropped silently. Off unless QueryChunkChars is set.
/// Each test names the design point it pins.
/// </summary>
public sealed class QueryChunkingTests
{
    // -- The split: order, no gaps, progress, bounded overlap --

    [Fact]
    public void Split_covers_the_whole_message_in_order_with_bounded_overlap_and_always_progresses()
    {
        // Acceptance 1 (Ember): each next chunk starts before the prior one's end when overlap is possible, ends
        // strictly after it, and there is never a gap. Random texts with every kind of boundary, and none.
        var random = new Random(63);
        string[] pieces = ["word", "a", "longerword", ". ", "! ", "? ", " ", " ", " ", "\n", "\n\n", "x"];
        for (var trial = 0; trial < 400; trial++)
        {
            var text = string.Concat(Enumerable.Range(0, random.Next(1, 900)).Select(_ => pieces[random.Next(pieces.Length)]));
            var budget = random.Next(300, 1200);
            var overlap = random.Next(0, budget / 4 + 1);

            var spans = QueryChunker.Split(text, budget, overlap);

            Assert.Equal(0, spans[0].Start);
            Assert.Equal(text.Length, spans[^1].End);
            for (var i = 0; i < spans.Count; i++)
            {
                Assert.True(spans[i].End - spans[i].Start <= budget, $"chunk {i} is over the budget");
                Assert.True(spans[i].End > spans[i].Start);
                if (i == 0)
                {
                    continue;
                }

                Assert.True(spans[i].Start <= spans[i - 1].End, "a gap");
                Assert.True(spans[i].Start > spans[i - 1].Start, "no progress at the start");
                Assert.True(spans[i].End > spans[i - 1].End, "no progress at the end");
                Assert.True(spans[i - 1].End - spans[i].Start <= overlap, "overlap past its setting");
                if (overlap > 0)
                {
                    Assert.True(spans[i].Start < spans[i - 1].End, "overlap was possible and none was kept");
                }
            }
        }
    }

    [Fact]
    public void Split_cuts_at_a_paragraph_then_a_line_then_a_sentence_then_a_space_and_hard_only_when_none()
    {
        var paragraph = new string('a', 200) + " b. c\nd\n\n" + new string('e', 400);
        Assert.Equal(paragraph.IndexOf("\n\n") + 2, QueryChunker.Split(paragraph, 300, 0)[0].End);

        var line = new string('a', 200) + " b. c\nd " + new string('e', 400);
        Assert.Equal(line.IndexOf('\n') + 1, QueryChunker.Split(line, 300, 0)[0].End);

        var sentence = new string('a', 200) + " b. c d " + new string('e', 400);
        Assert.Equal(sentence.IndexOf(". ") + 2, QueryChunker.Split(sentence, 300, 0)[0].End);

        var space = new string('a', 200) + " b c" + new string('e', 400);
        Assert.Equal(space.LastIndexOf(' ') + 1, QueryChunker.Split(space, 300, 0)[0].End);

        var none = new string('a', 1000);
        Assert.Equal([(0, 300), (300, 600), (600, 900), (900, 1000)], QueryChunker.Split(none, 300, 0));
    }

    [Fact]
    public void Split_of_a_query_within_the_budget_is_one_chunk()
    {
        Assert.Equal([(0, 12)], QueryChunker.Split("short query.", 1000, 150));
    }

    // -- Off by default: today's behaviour, exactly --

    [Fact]
    public async Task Chunking_is_off_by_default_and_off_sends_the_whole_query_once_as_today()
    {
        Assert.Equal(0, new EmbeddingSettings().QueryChunkChars);
        var client = new ChunkEmbedder { RefuseOverChars = 512 };
        var query = Embedder(client, new EmbeddingSettings { Enabled = true });
        var text = Prose(2400);

        Assert.Null(await query.EmbedQueryAsync(text));

        Assert.Equal(text, Assert.Single(Assert.Single(client.Calls)));
        Assert.Contains("sent whole", query.Fallback);
        Assert.Null(query.Query); // no chunk record, no diagnostics line: nothing new is reported when off
    }

    [Fact]
    public async Task Chunking_wins_over_the_tail_when_both_are_set()
    {
        var client = new ChunkEmbedder();
        var query = Embedder(client, Chunked(1000, tail: 1200));

        var vectors = await query.EmbedQueryAsync(Prose(2400));

        Assert.NotNull(vectors);
        Assert.Equal(0, vectors.Pieces.Min(p => p.Start)); // the head was embedded: no tail cut
        Assert.Equal(2400, vectors.Pieces.Max(p => p.End));
    }

    // -- The case this is for: a long query the embedder refuses whole now has vectors --

    [Fact]
    public async Task A_long_query_refused_whole_today_embeds_every_chunk_with_chunking_on()
    {
        // Every room wake on 2026-11-12 (1,962 to 6,159 characters) fell back to words with query tail 0 (Ember msg
        // 1570558471026249749). The fake refuses as bge-small at -c 512 does, past ~1,800 characters of prose.
        var text = Prose(6159);
        var off = Embedder(new ChunkEmbedder { RefuseOverChars = 1800 }, new EmbeddingSettings { Enabled = true });
        var on = Embedder(new ChunkEmbedder { RefuseOverChars = 1800 }, Chunked(1000));

        Assert.Null(await off.EmbedQueryAsync(text));
        var vectors = await on.EmbedQueryAsync(text);

        Assert.NotNull(vectors);
        Assert.All(vectors.Pieces, p => Assert.NotNull(p.Vector));
        Assert.Empty(vectors.Gaps());
        Assert.Null(on.Fallback);
    }

    // -- A refusal: kept successes, halved never tailed, the floor --

    [Fact]
    public async Task A_success_is_kept_after_another_chunks_failure_and_the_gap_is_only_the_uncovered_characters()
    {
        // Ember's first concern, and acceptance 6 (Arden): the batch fails whole, so its chunks go one by one; chunk 2
        // fails, 1 and 3 are kept. The gap is what no embedded piece covers, not chunk 2's whole span.
        var text = Prose(2600);
        var client = new ChunkEmbedder { ErrorWhenContains = Sentence(40) };
        var query = Embedder(client, Chunked(1000));

        var vectors = await query.EmbedQueryAsync(text);

        Assert.NotNull(vectors);
        Assert.Equal(3, vectors.Chunks);
        var failed = Assert.Single(vectors.Pieces, p => p.Vector is null);
        Assert.Equal((2, ChunkFailure.EmbedderError), (failed.Chunk, failed.Failure));
        var gap = Assert.Single(vectors.Gaps());
        var first = vectors.Pieces.Single(p => p.Chunk == 1);
        var third = vectors.Pieces.Single(p => p.Chunk == 3);
        Assert.Equal((first.End, third.Start), (gap.Start, gap.End));
        Assert.True(gap.End - gap.Start < failed.End - failed.Start); // overlap with neighbours is not a gap
        Assert.Contains("chunk 2 of 3", vectors.Degraded());
        Assert.Contains("embedder-error", vectors.Degraded());
    }

    [Fact]
    public async Task A_refused_chunk_is_halved_and_both_halves_embed_so_its_head_is_never_cut()
    {
        // Arden: halving to the tail dropped the head silently. Two halves, each with its own span, both embedded.
        var text = Prose(1960);
        var client = new ChunkEmbedder { RefuseOverChars = 700 };
        var query = Embedder(client, Chunked(1000, overlap: 0));

        var vectors = await query.EmbedQueryAsync(text);

        Assert.NotNull(vectors);
        Assert.Empty(vectors.Gaps());
        Assert.Equal(2, vectors.Chunks);
        Assert.Equal(4, vectors.Pieces.Count);
        Assert.Equal(0, vectors.Pieces[0].Start); // the head of chunk 1 is embedded
        Assert.All(vectors.Pieces, p => Assert.True(p.End - p.Start <= 700));
        Assert.Equal([1, 1, 2, 2], vectors.Pieces.Select(p => p.Chunk));
    }

    [Fact]
    public async Task The_floor_stops_a_second_split_and_says_floor_and_none_embedded_is_words_fallback()
    {
        // Acceptance 3: 1,000 halves to 500, and 500 to 250 is under the 300 floor, so in practice two pieces a chunk.
        var text = Prose(1960);
        var client = new ChunkEmbedder { RefuseOverChars = 400 };
        var query = Embedder(client, Chunked(1000, overlap: 0));

        Assert.Null(await query.EmbedQueryAsync(text));

        var record = query.Query!;
        Assert.Equal(4, record.Pieces.Count);
        Assert.All(record.Pieces, p => Assert.Equal(ChunkFailure.Floor, p.Failure));
        Assert.Contains("floor", query.FellBack[QueryEmbedder.Fragments]);
        Assert.Equal(RecallPath.WordsByFallback, RecallPath.Of(true, query).Path);
    }

    [Fact]
    public async Task Split_depth_is_bounded_at_four_pieces_a_chunk_and_both_counts_are_reported()
    {
        // Acceptance 3: 24 chunks may become up to 96 pieces; with a 2,000 budget the floor allows both splits.
        var text = Prose(3920);
        var client = new ChunkEmbedder { RefuseOverChars = 400 };
        var query = Embedder(client, Chunked(2000, overlap: 0));

        Assert.Null(await query.EmbedQueryAsync(text));

        var record = query.Query!;
        Assert.Equal(2, record.Chunks);
        Assert.Equal(8, record.Pieces.Count);
        Assert.All(record.Pieces, p => Assert.Equal(ChunkFailure.TooLarge, p.Failure)); // at the depth bound, not the floor
        Assert.Contains("2 chunks, 8 pieces", record.Line(new Dictionary<long, int>()));
    }

    // -- The budget: cap, order, deadline, cancellation --

    [Fact]
    public async Task Over_the_chunk_cap_the_first_and_last_halves_embed_and_the_middle_is_reported()
    {
        var text = Prose(2800);
        var client = new ChunkEmbedder();
        var query = Embedder(client, Chunked(300, overlap: 0, max: 4));

        var vectors = await query.EmbedQueryAsync(text);

        Assert.NotNull(vectors);
        Assert.Equal(10, vectors.Chunks);
        Assert.Equal([1, 2, 9, 10], vectors.Pieces.Where(p => p.Vector is not null).Select(p => p.Chunk));
        Assert.All(vectors.Pieces.Where(p => p.Vector is null), p => Assert.Equal(ChunkFailure.OverCap, p.Failure));
        var gap = Assert.Single(vectors.Gaps());
        Assert.Equal((vectors.Pieces.Single(p => p.Chunk == 2).End, vectors.Pieces.Single(p => p.Chunk == 9).Start), (gap.Start, gap.End));
        Assert.Contains("over-cap", vectors.Degraded());
    }

    [Fact]
    public async Task Batches_are_sent_first_then_last_then_inward()
    {
        // Acceptance 5 (Arden): the deadline cuts the middle, never the last chunks, where the ask usually is.
        var text = Prose(1960);
        var client = new ChunkEmbedder();
        var query = Embedder(client, Chunked(300, overlap: 0, batch: 2));

        var vectors = await query.EmbedQueryAsync(text);

        Assert.Equal(7, vectors!.Chunks);
        var firstChunkOfEachCall = client.Calls.Select(c => vectors.Pieces.First(p => text[p.Start..p.End] == c[0]).Chunk);
        Assert.Equal([1, 7, 3, 5], firstChunkOfEachCall);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], vectors.Pieces.Select(p => p.Chunk)); // reported in message order
    }

    [Fact]
    public async Task The_deadline_cancels_the_request_in_flight_and_what_is_left_is_over_time()
    {
        // Acceptance 2 (Ember): the budget is a token passed into the running request, not only checked between batches.
        var text = Prose(1120);
        CancellationToken inFlight = default;
        var client = new ChunkEmbedder
        {
            Before = async (call, ct) =>
            {
                if (call == 2)
                {
                    inFlight = ct;
                    await Task.Delay(Timeout.Infinite, ct);
                }
            },
        };
        var query = Embedder(client, Chunked(300, overlap: 0, batch: 1, budgetMs: 200));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var vectors = await query.EmbedQueryAsync(text);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(inFlight.IsCancellationRequested);
        Assert.NotNull(vectors);
        Assert.Equal([1, 4], vectors.Pieces.Where(p => p.Vector is not null).Select(p => p.Chunk));
        Assert.Equal([ChunkFailure.OverTime, ChunkFailure.OverTime], vectors.Pieces.Where(p => p.Vector is null).Select(p => p.Failure));
        Assert.Contains("over-time", vectors.Degraded());
    }

    [Fact]
    public async Task The_callers_cancellation_still_escapes_with_chunking_on()
    {
        // PR 17: cancellation is the caller's decision, never turned into a words-only recall that reads as a success.
        using var caller = new CancellationTokenSource();
        var client = new ChunkEmbedder
        {
            Before = (call, ct) =>
            {
                if (call == 1)
                {
                    caller.Cancel();
                    ct.ThrowIfCancellationRequested();
                }

                return Task.CompletedTask;
            },
        };
        var query = Embedder(client, Chunked(300, overlap: 0, batch: 1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.EmbedQueryAsync(Prose(1200), caller.Token));
        Assert.Null(query.Fallback); // not noted as a fallback
    }

    // -- Scoring and attribution --

    [Fact]
    public void Max_scores_the_best_chunk_and_ties_go_to_the_lowest_chunk()
    {
        var vectors = new QueryVectors(QueryVectors.Max, 3000, 3, 150,
        [
            new QueryPiece(1, 0, 1000, [0f, 1f], null),
            new QueryPiece(2, 850, 2000, [1f, 0f], null),
            new QueryPiece(3, 1850, 3000, [1f, 0f], null),
        ]);

        var best = vectors.Score([1f, 0f]);

        Assert.Equal(1.0, best.Score, 6);
        Assert.Equal(2, best.Chunk); // 2 and 3 tie: the lowest chunk
        Assert.Equal(0.0, vectors.Score([0f, 0f]).Score, 6);
    }

    [Fact]
    public void Mean_scores_against_one_normalised_vector_and_names_no_single_chunk()
    {
        var vectors = new QueryVectors(QueryVectors.Mean, 2000, 2, 0,
        [
            new QueryPiece(1, 0, 1000, [2f, 0f], null),
            new QueryPiece(2, 1000, 2000, [0f, 1f], null),
        ]);

        var score = vectors.Score([1f, 1f]);

        Assert.Equal(1.0, score.Score, 6); // the mean of the two unit directions points at (1, 1)
        Assert.Equal(0, score.Chunk);
        Assert.Contains("embedded chars 1-2,000", vectors.Line(new Dictionary<long, int>()));
    }

    [Fact]
    public async Task Diagnostics_carry_spans_counts_and_categories_but_never_query_text()
    {
        var text = Prose(2600).Replace("Sentence", "Zebracorn")[..2600]; // the same length, so the same three chunks
        var client = new ChunkEmbedder { ErrorWhenContains = "Zebracorn 0040" };
        var query = Embedder(client, Chunked(1000));

        var vectors = await query.EmbedQueryAsync(text);
        query.Attribute(17, 3);
        var line = vectors!.Line(query.BestChunks);
        var (_, why) = RecallPath.Of(true, query);

        Assert.StartsWith("recall-chunks: max; 3 chunks, 3 pieces (embedded 2; embedder-error 1: chunk 2", line);
        Assert.Contains("#17:3", line);
        foreach (var said in new[] { line, why ?? "" })
        {
            Assert.DoesNotContain("Zebracorn", said);
            Assert.DoesNotContain("0040", said);
        }
    }

    // -- Paths: partial is never semantic, never words-fallback --

    [Fact]
    public async Task A_partly_embedded_query_is_semantic_degraded_naming_the_query_and_a_whole_one_is_semantic()
    {
        var text = Prose(2600);
        var partly = Embedder(new ChunkEmbedder { ErrorWhenContains = Sentence(40) }, Chunked(1000));
        var whole = Embedder(new ChunkEmbedder(), Chunked(1000));

        await partly.EmbedQueryAsync(text);
        partly.Compare(QueryEmbedder.Fragments);
        await whole.EmbedQueryAsync(text);
        whole.Compare(QueryEmbedder.Fragments);

        var (path, why) = RecallPath.Of(true, partly);
        Assert.Equal(RecallPath.SemanticDegraded, path);
        Assert.StartsWith("query: chars ", why);
        Assert.Equal((RecallPath.Semantic, null), RecallPath.Of(true, whole));
    }

    [Fact]
    public async Task A_query_is_embedded_once_per_recall_for_every_part()
    {
        // The fragment rounds and the utterance round share the chunk vectors, so the budget is per recall.
        var client = new ChunkEmbedder();
        var query = Embedder(client, Chunked(1000));
        var text = Prose(2600);

        await query.EmbedQueryAsync(text);
        var calls = client.Calls.Count;
        await query.EmbedQueryAsync(text, part: QueryEmbedder.PastUtterances);

        Assert.Equal(calls, client.Calls.Count);
        query.Clear();
        Assert.Null(query.Query);
    }

    // -- Settings --

    [Theory]
    [InlineData(200, 50, 24, 8, 8000, "max")]     // a budget under the 300 floor
    [InlineData(1000, 300, 24, 8, 8000, "max")]   // overlap past a quarter of the budget
    [InlineData(1000, 150, 0, 8, 8000, "max")]    // no chunks allowed
    [InlineData(1000, 150, 24, 0, 8000, "max")]   // empty requests
    [InlineData(1000, 150, 24, 8, 0, "max")]      // no time at all
    [InlineData(1000, 150, 24, 8, 8000, "median")] // no such strategy
    [InlineData(-1, 150, 24, 8, 8000, "max")]
    public void Out_of_range_chunk_settings_fail_at_start(int chars, int overlap, int max, int batch, int budgetMs, string score)
    {
        var settings = new EmbeddingSettings
        {
            QueryChunkChars = chars, QueryChunkOverlap = overlap, QueryChunkMax = max, QueryChunkBatch = batch,
            QueryChunkBudgetMs = budgetMs, QueryChunkScore = score,
        };

        Assert.NotNull(settings.QueryChunkProblem());
    }

    [Fact]
    public void The_defaults_and_a_sane_setting_pass()
    {
        Assert.Null(new EmbeddingSettings().QueryChunkProblem());
        Assert.Null(Chunked(1000).QueryChunkProblem());
        Assert.Equal((150, 24, 8, 8000, "max"), (new EmbeddingSettings().QueryChunkOverlap, new EmbeddingSettings().QueryChunkMax,
            new EmbeddingSettings().QueryChunkBatch, new EmbeddingSettings().QueryChunkBudgetMs, new EmbeddingSettings().QueryChunkScore));
    }

    // -- The client says why it gave no vector --

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":{"message":"input (525 tokens) is too large to process. increase the physical batch size (current batch size: 512)"}}""", ChunkFailure.TooLarge)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "", ChunkFailure.TooLarge)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "loading model", ChunkFailure.EmbedderError)]
    public async Task The_embedding_client_classes_a_refusal_as_too_large_or_an_embedder_error(HttpStatusCode status, string body, string failure)
    {
        var http = new Mock<ISimpleClient>();
        http.Setup(c => c.MakeRequest(It.IsAny<ISimpleRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SimpleResponse { StatusCode = status, IsSuccessful = false, StringBody = body });
        var client = new OpenAiEmbeddingClient(new EmbeddingSettings { Enabled = true, ApiBaseUrl = "http://localhost:8080/v1" },
            Mock.Of<IDisplayProvider>(), http.Object);

        var outcome = await client.TryEmbedAsync(["a"]);

        Assert.Empty(outcome.Vectors);
        Assert.Equal(failure, outcome.Failure);
    }

    // -- Helpers --

    private static string Sentence(int n) => $"Sentence {n:D4} says a thing.";

    /// <summary>Numbered sentences (each one unique, 28 characters with its space) cut to exactly <paramref name="length"/>.</summary>
    private static string Prose(int length) =>
        string.Join(' ', Enumerable.Range(1, length / 28 + 2).Select(Sentence))[..length];

    private static EmbeddingSettings Chunked(int chars, int overlap = 150, int max = 24, int batch = 8, int budgetMs = 8000, int tail = 0) => new()
    {
        Enabled = true,
        ApiBaseUrl = "http://localhost:8081/v1",
        QueryTailChars = tail,
        QueryChunkChars = chars,
        QueryChunkOverlap = overlap,
        QueryChunkMax = max,
        QueryChunkBatch = batch,
        QueryChunkBudgetMs = budgetMs,
    };

    private static QueryEmbedder Embedder(IEmbeddingClient client, EmbeddingSettings settings)
    {
        var config = new Mock<IAppConfig>();
        config.SetupGet(c => c.Embedding).Returns(settings);
        return new QueryEmbedder(client, config.Object, Mock.Of<IDisplayProvider>());
    }

    /// <summary>An embedder that refuses as llama-server does (the whole request when any input is too long) and records each call.</summary>
    private sealed class ChunkEmbedder : IEmbeddingClient
    {
        public int RefuseOverChars { get; init; }
        public string? ErrorWhenContains { get; init; }
        public Func<int, CancellationToken, Task>? Before { get; init; }
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public bool IsEnabled => true;
        public string Model => "chunks";

        public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            (await TryEmbedAsync(texts, ct)).Vectors;

        public async Task<EmbedOutcome> TryEmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            Calls.Add(texts);
            if (Before is not null)
            {
                await Before(Calls.Count - 1, ct);
            }

            if (RefuseOverChars > 0 && texts.Any(t => t.Length > RefuseOverChars))
            {
                return new EmbedOutcome([], ChunkFailure.TooLarge);
            }

            if (ErrorWhenContains is not null && texts.Any(t => t.Contains(ErrorWhenContains)))
            {
                return new EmbedOutcome([], ChunkFailure.EmbedderError);
            }

            return new EmbedOutcome(texts.Select(t => new[] { t.Length, 1f }).ToList(), null);
        }
    }
}
