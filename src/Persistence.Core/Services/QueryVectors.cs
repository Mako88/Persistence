namespace Persistence.Services;

/// <summary>
/// Why a piece of a chunked query has no vector (docs/design/chunked-query-embedding.md, "Diagnostics"). Categories
/// only: never the embedder's message, which can quote the query.
/// </summary>
public static class ChunkFailure
{
    /// <summary>The embedder refused the input as too long.</summary>
    public const string TooLarge = "too-large";

    /// <summary>Too large, and splitting it again would make a half under the floor (acceptance 3).</summary>
    public const string Floor = "too-large (floor)";

    /// <summary>The embedder failed for another reason (down, a bad response); not answered by splitting.</summary>
    public const string EmbedderError = "embedder-error";

    /// <summary>The embedder gave no vector and its client cannot say why; answered as too large.</summary>
    public const string Refused = "refused";

    /// <summary>In the middle of a message with more chunks than the cap.</summary>
    public const string OverCap = "over-cap";

    /// <summary>Not embedded when the recall's elapsed budget ran out.</summary>
    public const string OverTime = "over-time";

    /// <summary>Not embedded when the recall itself was cancelled.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// One embedded (or not) piece of a chunked query: the chunk it belongs to (1-based, in message order), its span
/// <c>[Start, End)</c> in the original message, and its vector or why it has none. A chunk the embedder refused is
/// split into pieces, each with its own span.
/// </summary>
public sealed record QueryPiece(int Chunk, int Start, int End, float[]? Vector, string? Failure);

/// <summary>
/// A recall's query as chunk vectors, and how it scores a stored vector: <see cref="Max"/>, the best cosine over the
/// pieces, ties to the lowest chunk; <see cref="Mean"/>, the cosine to one normalised mean vector. Also everything the
/// recall reports about it: the uncovered characters (<see cref="Gaps"/>), the path's reason (<see cref="Degraded"/>)
/// and the diagnostics line (<see cref="Line"/>), all content-free.
/// </summary>
public sealed class QueryVectors
{
    public const string Max = "max";
    public const string Mean = "mean";

    private readonly float[]? mean;

    public QueryVectors(string strategy, int length, int chunks, int overlap, IReadOnlyList<QueryPiece> pieces)
    {
        Strategy = strategy;
        Length = length;
        Chunks = chunks;
        Overlap = overlap;
        Pieces = pieces;
        if (strategy == Mean && Embedded.Count > 0)
        {
            mean = new float[Embedded[0].Vector!.Length];
            foreach (var piece in Embedded.Where(p => p.Vector!.Length == mean.Length))
            {
                var norm = Math.Sqrt(piece.Vector!.Sum(x => (double)x * x));
                for (var i = 0; norm > 0 && i < mean.Length; i++)
                {
                    mean[i] += (float)(piece.Vector[i] / norm);
                }
            }
        }
    }

    /// <summary>A query that was not chunked (chunking off): one vector, scored by cosine as before.</summary>
    public static QueryVectors Single(float[] vector, int length) =>
        new(Max, length, 1, 0, [new QueryPiece(1, 0, length, vector, null)]) { Chunked = false };

    /// <summary>Whether the query went through chunking (only then is there anything new to report).</summary>
    public bool Chunked { get; private init; } = true;

    public string Strategy { get; }

    /// <summary>The message's length in characters.</summary>
    public int Length { get; }

    /// <summary>Original chunks, before any refused one was split (the cap counts these).</summary>
    public int Chunks { get; }

    public int Overlap { get; }

    /// <summary>Every piece, embedded or not, in message order.</summary>
    public IReadOnlyList<QueryPiece> Pieces { get; }

    public IReadOnlyList<QueryPiece> Embedded => Pieces.Where(p => p.Vector is not null).ToList();

    /// <summary>
    /// A stored vector's similarity to the query and the chunk that gave it (0 for <see cref="Mean"/>, which has no
    /// single contributor). For <see cref="Max"/> this is the best semantic chunk, not necessarily the chunk that put
    /// the item in the shown set: words, fusion and quality weighting also do (acceptance 4).
    /// </summary>
    public (double Score, int Chunk) Score(float[] stored)
    {
        if (mean is not null)
        {
            return (VectorMath.Cosine(mean, stored), 0);
        }

        var best = (Score: double.NegativeInfinity, Chunk: 0);
        foreach (var piece in Pieces)
        {
            if (piece.Vector is null)
            {
                continue;
            }

            // Pieces are in chunk order, so a strictly better score is needed to move on: ties stay with the lowest chunk.
            var score = VectorMath.Cosine(piece.Vector, stored);
            if (score > best.Score)
            {
                best = (score, piece.Chunk);
            }
        }

        return best.Chunk == 0 ? (0, 0) : best;
    }

    /// <summary>
    /// The characters no embedded piece covers (acceptance 6: a refused chunk's overlap with an embedded neighbour is
    /// not a gap), each with the chunks and categories of the pieces that left it uncovered.
    /// </summary>
    public IReadOnlyList<(int Start, int End, string Why)> Gaps()
    {
        var gaps = new List<(int Start, int End, string Why)>();
        var covered = Merge(Embedded);
        var at = 0;
        foreach (var (start, end) in covered.Append((Length, Length)))
        {
            if (start > at)
            {
                var missing = Pieces.Where(p => p.Vector is null && p.Start < start && p.End > at).ToList();
                var chunks = missing.Select(p => p.Chunk).Distinct().ToList();
                var why = string.Join(", ", missing.Select(p => p.Failure).Distinct());
                var which = chunks.Count == 1 ? $"chunk {chunks[0]}" : $"chunks {chunks.Min()}-{chunks.Max()}";
                gaps.Add((at, start, $"{which} of {Chunks}: {why}"));
            }

            at = Math.Max(at, end);
        }

        return gaps;
    }

    /// <summary>The path's reason when some of the query was not embedded (<c>semantic-degraded (query: ...)</c>), else null.</summary>
    public string? Degraded()
    {
        var gaps = Gaps();
        return gaps.Count == 0 || Embedded.Count == 0 ? null
            : string.Join("; ", gaps.Select(g => $"chars {Chars(g.Start, g.End)} not embedded ({g.Why})"));
    }

    /// <summary>Why no piece embedded, for the words-fallback reason.</summary>
    public string NoneEmbedded() =>
        $"no chunk of the {Length:N0}-character message embedded ({Chunks} chunks, {Pieces.Count} pieces: "
        + string.Join(", ", Pieces.GroupBy(p => p.Failure).Select(g => $"{g.Key} {g.Count()}")) + ")";

    /// <summary>
    /// The one diagnostics line for a chunked recall: strategy, both counts, what failed by category with chunk and span,
    /// overlap, each chunk's span, and for max how many shown items each chunk was best for and each shown item's best
    /// chunk (<paramref name="bestChunks"/>, by fragment id). Spans, counts and ids only: never text, never vector values.
    /// </summary>
    public string Line(IReadOnlyDictionary<long, int> bestChunks)
    {
        var failed = Pieces.Where(p => p.Vector is null).GroupBy(p => p.Failure)
            .Select(g => $"{g.Key} {g.Count()}: " + string.Join(", ", g.Select(p => $"chunk {p.Chunk} chars {Chars(p.Start, p.End)}")));
        var counts = string.Join("; ", new[] { $"embedded {Embedded.Count}" }.Concat(failed));
        var spans = string.Join(" ", Pieces.GroupBy(p => p.Chunk).Select(g => $"{g.Key}:{Chars(g.Min(p => p.Start), g.Max(p => p.End))}"));
        var line = $"recall-chunks: {Strategy}; {Chunks} chunks, {Pieces.Count} pieces ({counts}); overlap {Overlap}; spans {spans}";
        if (Strategy == Mean)
        {
            var missing = Gaps();
            return $"{line}; embedded chars {string.Join(", ", Merge(Embedded).Select(s => Chars(s.Start, s.End)))}"
                   + (missing.Count == 0 ? "" : $"; missing chars {string.Join(", ", missing.Select(g => Chars(g.Start, g.End)))}");
        }

        var shown = string.Join(" ", Enumerable.Range(1, Chunks).Select(c => $"{c}:{bestChunks.Values.Count(b => b == c)}"));
        var items = string.Join(" ", bestChunks.OrderBy(b => b.Key).Select(b => $"#{b.Key}:{b.Value}"));
        return $"{line}; shown by chunk: {shown}" + (items.Length == 0 ? "" : $"; best chunk: {items}");
    }

    /// <summary>A span as a reader counts characters: 1-based and inclusive, with thousands separators.</summary>
    private static string Chars(int start, int end) => $"{start + 1:N0}-{end:N0}";

    private static List<(int Start, int End)> Merge(IEnumerable<QueryPiece> pieces)
    {
        var merged = new List<(int Start, int End)>();
        foreach (var piece in pieces.OrderBy(p => p.Start))
        {
            if (merged.Count > 0 && piece.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, piece.End));
            }
            else
            {
                merged.Add((piece.Start, piece.End));
            }
        }

        return merged;
    }
}
