using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;

namespace Persistence.Services;

/// <summary>
/// Embeds a recall query under the store's query policy (<see cref="EmbeddingSettings.QueryTailChars"/>), and
/// says so when it cannot. Recall falls back to exact words (BM25) whenever there is no query vector; that
/// fallback used to be silent, and on long messages it went unseen for weeks (2026-11-11). Now every one is
/// logged and kept in <see cref="Fallback"/> for the recall output to show.
///
/// <para>
/// With a tail set, a refusal (the embedder returns nothing) is retried on a shorter tail, at most
/// <see cref="MaxShrinks"/> times and never below <see cref="MinTailChars"/>: the tail length is a
/// character heuristic for a token limit, and token-dense text (long numbers, code, non-Latin script) runs
/// past 512 tokens in fewer characters. The embedder does not say why it returned nothing, so an unreachable
/// one costs the same few retries; a text that already failed is not tried again until <see cref="Clear"/>.
/// With no tail (0, the default), the whole query is sent once, as before the setting existed.
/// </para>
/// </summary>
[Singleton(typeof(QueryEmbedder))]
public class QueryEmbedder(IEmbeddingClient embeddings, IAppConfig config, IDisplayProvider display)
{
    /// <summary>How many times a refused tail is halved and retried.</summary>
    public const int MaxShrinks = 2;

    /// <summary>The shortest tail retried: shorter and it no longer says what a long message is about.</summary>
    public const int MinTailChars = 300;

    /// <summary>The part of a recall that compares authored fragments by meaning.</summary>
    public const string Fragments = "fragments";

    /// <summary>The part of a recall that compares past utterances by meaning (conversation recall).</summary>
    public const string PastUtterances = "past utterances";

    private string? failed;
    private string? failedWhy;
    private readonly HashSet<string> compared = [];
    private readonly Dictionary<string, string> fellBack = [];

    /// <summary>Why the last query since <see cref="Clear"/> ran on words alone, or null when none did.</summary>
    public string? Fallback { get; private set; }

    /// <summary>
    /// The parts of this recall (<see cref="Fragments"/>, <see cref="PastUtterances"/>) whose meaning comparison ran:
    /// the positive evidence for a recall's path (<see cref="Persistence.Services.RecallPath"/>).
    /// </summary>
    public IReadOnlySet<string> Compared => compared;

    /// <summary>The parts of this recall that asked for meaning and ran on words instead, each with why: the negative evidence.</summary>
    public IReadOnlyDictionary<string, string> FellBack => fellBack;

    /// <summary>The chunked query of this recall, embedded or not (null when chunking is off or nothing was asked).</summary>
    public QueryVectors? Query { get; private set; }

    private readonly Dictionary<long, int> bestChunks = [];

    /// <summary>For max scoring, each shown item's best semantic chunk, by fragment id (the design's attribution).</summary>
    public IReadOnlyDictionary<long, int> BestChunks => bestChunks;

    /// <summary>Notes that item <paramref name="id"/>'s best semantic chunk was <paramref name="chunk"/> (0: none, as for mean).</summary>
    public void Attribute(long id, int chunk)
    {
        if (chunk > 0)
        {
            bestChunks[id] = chunk;
        }
    }

    /// <summary>Starts a recall afresh: no fallback noted, nothing compared, nothing remembered as failed.</summary>
    public void Clear()
    {
        Fallback = null;
        failed = null;
        failedWhy = null;
        Query = null;
        bestChunks.Clear();
        compared.Clear();
        fellBack.Clear();
    }

    /// <summary>Notes that <paramref name="part"/> of a recall compared meaning: its query vector was ranked against stored ones.</summary>
    public void Compare(string part) => compared.Add(part);

    /// <summary>Notes that <paramref name="part"/> of a recall ran on words alone, and why, and logs it.</summary>
    public void Note(string why, string part = Fragments)
    {
        fellBack.TryAdd(part, why);

        // One recall asks the surfacer more than once (here, then elsewhere); the same reason is logged once.
        // The fragments' notice keeps its wording (scripts and the app's log match on it); another part names itself,
        // because when the fragments did compare meaning, "recall ran on exact words alone" would not be true.
        var fallback = part == Fragments
            ? $"Recall ran on exact words alone, without meaning: {why}."
            : $"Recall of {part} ran on exact words alone, without meaning: {why}.";
        if (Fallback is not null && Fallback.Contains(fallback, StringComparison.Ordinal))
        {
            return;
        }

        Fallback = Fallback is null ? fallback : $"{Fallback} {fallback}";
        display.ShowError(fallback);
    }

    /// <summary>The text actually sent for <paramref name="text"/> under a tail of <paramref name="tailChars"/> (0: all of it).</summary>
    public static string Tail(string text, int tailChars) =>
        tailChars > 0 && text.Length > tailChars ? text[^tailChars..] : text;

    /// <summary>
    /// The query as vectors to score stored ones against, or null (noted in <see cref="Fallback"/>) when none embedded.
    /// With <see cref="EmbeddingSettings.QueryChunkChars"/> 0 (the default) this is <see cref="EmbedAsync"/>, unchanged:
    /// one vector, scored by cosine. Set, the query is chunked (<see cref="EmbedChunksAsync"/>), once per recall.
    /// </summary>
    public async Task<QueryVectors?> EmbedQueryAsync(string text, CancellationToken ct = default, string part = Fragments)
    {
        var settings = config.Embedding;
        if (settings.QueryChunkChars <= 0)
        {
            return await EmbedAsync(text, ct, part) is { } vector ? QueryVectors.Single(vector, text.Length) : null;
        }

        if (text == failed)
        {
            fellBack.TryAdd(part, failedWhy!);
            return null;
        }

        // The fragment rounds and the utterance round share one embedding of the message: the budget is per recall.
        if (Query is null || chunkedText != text)
        {
            Query = await EmbedChunksAsync(text, settings, ct);
            chunkedText = text;
        }

        if (Query.Embedded.Count > 0)
        {
            return Query;
        }

        failed = text;
        failedWhy = Query.NoneEmbedded();
        Note(failedWhy, part);
        return null;
    }

    private string? chunkedText;

    /// <summary>
    /// Embeds every chunk of <paramref name="text"/> under the design's budget: batches of at most
    /// <see cref="EmbeddingSettings.QueryChunkBatch"/>, sent first, then last, then inward (acceptance 5); a failed batch
    /// retried chunk by chunk, each success kept the moment it arrives; a chunk refused as too large halved, at most two
    /// levels and never under <see cref="MinTailChars"/>; over <see cref="EmbeddingSettings.QueryChunkMax"/> chunks, the
    /// first and last halves only; within <see cref="EmbeddingSettings.QueryChunkBudgetMs"/>, a deadline linked into the
    /// request in flight (acceptance 2). Nothing is dropped silently: every piece comes back, embedded or with why not.
    /// The caller's own cancellation escapes, as since PR 17: a cancelled recall returns nothing to report.
    /// </summary>
    private async Task<QueryVectors> EmbedChunksAsync(string text, EmbeddingSettings settings, CancellationToken ct)
    {
        var spans = QueryChunker.Split(text, settings.QueryChunkChars, settings.QueryChunkOverlap);
        var chunks = spans.Select((s, i) => (Chunk: i + 1, s.Start, s.End)).ToList();
        var results = new Dictionary<int, List<QueryPiece>>();

        // Over the cap: the first and last halves, where the addressed message and the ask usually are.
        var cap = settings.QueryChunkMax;
        var kept = chunks.Count <= cap ? chunks
            : chunks.Take((cap + 1) / 2).Concat(chunks.Skip(chunks.Count - cap / 2)).ToList();
        foreach (var skipped in chunks.Except(kept))
        {
            results[skipped.Chunk] = [new QueryPiece(skipped.Chunk, skipped.Start, skipped.End, null, ChunkFailure.OverCap)];
        }

        var batches = kept.Chunk(settings.QueryChunkBatch).ToList();
        var order = new List<int>();
        for (int low = 0, high = batches.Count - 1; low <= high; low++, high--)
        {
            order.Add(low);
            if (high != low)
            {
                order.Add(high);
            }
        }

        // The pieces bound (acceptance 3) is structural: a chunk is halved at most MaxShrinks (2) levels deep, so it ends as
        // at most four pieces, and 24 chunks as at most 96. Both counts are reported (QueryVectors.Line).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(settings.QueryChunkBudgetMs);
        var stopped = (string?)null;

        foreach (var b in order)
        {
            var batch = batches[b];
            if (stopped is not null)
            {
                foreach (var c in batch)
                {
                    results[c.Chunk] = [new QueryPiece(c.Chunk, c.Start, c.End, null, stopped)];
                }

                continue;
            }

            try
            {
                var (vectors, failure) = await embeddings.TryEmbedAsync(batch.Select(c => text[c.Start..c.End]).ToList(), deadline.Token);
                if (vectors.Count == batch.Length)
                {
                    for (var i = 0; i < batch.Length; i++)
                    {
                        results[batch[i].Chunk] = [new QueryPiece(batch[i].Chunk, batch[i].Start, batch[i].End, vectors[i], null)];
                    }

                    continue;
                }

                // llama-server fails the whole request when any one input is too long: each chunk on its own. A batch of
                // one has already been tried on its own, and its failure is that chunk's.
                foreach (var c in batch)
                {
                    var pieces = new List<QueryPiece>();
                    results[c.Chunk] = pieces;
                    await EmbedPieceAsync(c.Chunk, c.Start, c.End, 0, batch.Length == 1 ? failure : null);

                    async Task EmbedPieceAsync(int chunk, int start, int end, int depth, string? known)
                    {
                        var why = known;
                        if (why is null)
                        {
                            var (one, oneFailure) = await embeddings.TryEmbedAsync([text[start..end]], deadline.Token);
                            if (one.Count == 1 && one[0].Length > 0)
                            {
                                pieces.Add(new QueryPiece(chunk, start, end, one[0], null));
                                return;
                            }

                            why = oneFailure ?? ChunkFailure.Refused;
                        }

                        if (why is not (ChunkFailure.TooLarge or ChunkFailure.Refused) || depth == MaxShrinks)
                        {
                            pieces.Add(new QueryPiece(chunk, start, end, null, why));
                            return;
                        }

                        if (QueryChunker.Halve(text, start, end, MinTailChars) is not { } halves)
                        {
                            pieces.Add(new QueryPiece(chunk, start, end, null, ChunkFailure.Floor));
                            return;
                        }

                        await EmbedPieceAsync(chunk, halves.Head.Start, halves.Head.End, depth + 1, null);
                        await EmbedPieceAsync(chunk, halves.Tail.Start, halves.Tail.End, depth + 1, null);
                    }
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // The budget, or the recall's own token. Spans of this batch not yet embedded join the rest as stopped.
                stopped = ct.IsCancellationRequested ? ChunkFailure.Cancelled : ChunkFailure.OverTime;
                foreach (var c in batch)
                {
                    var done = results.GetValueOrDefault(c.Chunk) ?? [];
                    var covered = done.Where(p => p.Vector is not null || p.Failure is not null).ToList();
                    results[c.Chunk] = covered.Count > 0 && covered.Max(p => p.End) >= c.End
                        ? covered
                        : [.. covered, new QueryPiece(c.Chunk, covered.Count == 0 ? c.Start : covered.Max(p => p.End), c.End, null, stopped)];
                }
            }
        }

        if (stopped == ChunkFailure.Cancelled)
        {
            // Recorded above as the design classes it; the caller's cancellation is its decision and escapes (PR 17).
            ct.ThrowIfCancellationRequested();
        }

        var all = results.OrderBy(r => r.Key).SelectMany(r => r.Value.OrderBy(p => p.Start)).ToList();
        return new QueryVectors(settings.QueryChunkScore, text.Length, chunks.Count, Math.Min(settings.QueryChunkOverlap, settings.QueryChunkChars / 4), all);
    }

    /// <summary>The query's vector, or null (noted in <see cref="Fallback"/>) when the embedder gave none.</summary>
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default, string part = Fragments)
    {
        if (text == failed)
        {
            // Not sent again and not logged again, but this part ran on words too, for the same reason.
            fellBack.TryAdd(part, failedWhy!);
            return null;
        }

        var tailChars = config.Embedding.QueryTailChars;
        var sent = Tail(text, tailChars);
        var tried = new List<int>();
        for (var shrinks = 0; ; shrinks++)
        {
            tried.Add(sent.Length);
            var vectors = await embeddings.EmbedAsync([sent], ct);
            if (vectors.Count > 0 && vectors[0].Length > 0)
            {
                return vectors[0];
            }

            if (tailChars <= 0 || shrinks == MaxShrinks || sent.Length <= MinTailChars)
            {
                break;
            }

            sent = Tail(sent, Math.Max(MinTailChars, sent.Length / 2));
        }

        failed = text;
        failedWhy = tailChars <= 0
            ? $"the embedder gave no vector for the whole {text.Length:N0}-character message (query tail is 0: sent whole)"
            : $"the embedder gave no vector for the message ({text.Length:N0} characters; tried its last {string.Join(", then ", tried.Select(n => n.ToString("N0")))})";
        Note(failedWhy, part);
        return null;
    }
}

/// <summary>
/// Which way a recall actually ran, read off what happened rather than inferred from the settings or the
/// message's length (Ember, 2026-11-12). A recall has parts that compare meaning (<see cref="QueryEmbedder.Fragments"/>,
/// <see cref="QueryEmbedder.PastUtterances"/>); a part counts as meaning only when its comparison ran and it did not
/// then fall back. <c>semantic</c>: every part that asked for meaning compared it. <c>semantic-degraded</c>: at least
/// one part compared meaning and another ran on words, with which and why (Ember's CHANGES on f922439: such a recall
/// is neither clean semantic nor words alone, and a benchmark must count it apart from both). <c>words-setting</c>:
/// embedding is off, so exact words were the choice. <c>words-fallback</c>: meaning was asked for and no part compared
/// it, with why. <c>unknown</c>: neither was seen (an empty query, nothing asked of the surfacer).
/// </summary>
public static class RecallPath
{
    public const string Semantic = "semantic";
    public const string SemanticDegraded = "semantic-degraded";
    public const string WordsBySetting = "words-setting";
    public const string WordsByFallback = "words-fallback";
    public const string Unknown = "unknown";

    /// <summary>
    /// The path and, unless it is clean semantic, why. A fallback never outranks a part that compared meaning: that
    /// recall is degraded, not words alone.
    /// </summary>
    public static (string Path, string? Why) Of(bool embeddingEnabled, QueryEmbedder query)
    {
        if (!embeddingEnabled)
        {
            return (WordsBySetting, "embedding is off");
        }

        var meaning = query.Compared.Where(p => !query.FellBack.ContainsKey(p)).ToList();
        // A chunked query partly embedded is degraded by the part not embedded (component `query`), never clean semantic;
        // with nothing embedded at all, the parts fell back and it is words-fallback (docs/design/chunked-query-embedding.md).
        var missing = new Dictionary<string, string>(query.FellBack);
        if (query.Query?.Degraded() is { } partly)
        {
            missing["query"] = partly;
        }

        var words = string.Join("; ", missing.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}: {f.Value}"));
        return (meaning.Count > 0, missing.Count > 0) switch
        {
            (true, false) => (Semantic, null),
            (true, true) => (SemanticDegraded, words),
            (false, true) => (WordsByFallback, words),
            _ => (Unknown, null),
        };
    }
}
