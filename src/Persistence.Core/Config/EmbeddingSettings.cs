namespace Persistence.Config;

/// <summary>
/// Settings for semantic (vector) associative recall. Shared infra, not model-coupled: the embedding
/// model is deliberately separate from the peer's chat model, so a peer on Anthropic and a peer on a
/// local llama.cpp can share one small embedding server and produce comparable vectors.
///
/// Off by default. When disabled — or when the embedding service is unreachable — recall falls back to
/// the lexical BM25 path, which is the behaviour that shipped before this existed. Surfacing a memory
/// is an enrichment, never a precondition for a turn.
/// </summary>
public class EmbeddingSettings
{
    /// <summary>Whether semantic recall is attempted at all. Off = pure lexical (BM25), as before.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Base URL of an OpenAI-compatible embeddings endpoint (<c>{ApiBaseUrl}/embeddings</c>) — e.g.
    /// a second llama.cpp server started with <c>--embedding</c> at
    /// <c>http://127.0.0.1:8081/v1</c>. Required when <see cref="Enabled"/>.
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// Embedding model name. Sent to the endpoint, and stored alongside every vector so that changing
    /// models invalidates the old vectors rather than silently comparing across embedding spaces —
    /// cosine between two different models' vectors is meaningless, not merely noisy.
    /// </summary>
    public string Model { get; set; } = "local";

    /// <summary>API key, when the endpoint needs one. Local servers are usually keyless.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Request timeout. Embedding is fast even locally; a short timeout keeps a wedged
    /// service from delaying the turn it was only meant to enrich.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>How many texts to send per embed request.</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>
    /// How many not-yet-embedded fragments to backfill per turn. Embeddings fill in lazily as recall
    /// runs rather than in a migration or a separate job, so a large existing store warms up over a
    /// few turns and a fresh fragment is reachable the turn after it is written. 0 disables backfill
    /// (recall then only sees what is already embedded).
    /// </summary>
    public int BackfillPerTurn { get; set; } = 32;

    /// <summary>
    /// Reciprocal-rank-fusion constant blending the semantic and lexical rankings. Higher values
    /// flatten the contribution of rank position (a top hit counts for less relative to the rest).
    /// 60 is the value the RRF literature settles on and needs no tuning for a store this size.
    /// </summary>
    public int FusionK { get; set; } = 60;

    /// <summary>
    /// How much a fragment's quality (the mean of importance and confidence) weighs in recall: each ranking
    /// scores relevance × quality^QualityWeight. 1 is the ranking recall has always had; 0 ranks by relevance
    /// alone. The knob exists for the importance ablation (scripts/recall-eval): importance may be doing two
    /// jobs, keeping a fragment prominent and making it look relevant to every prompt.
    /// </summary>
    public double QualityWeight { get; set; } = 1.0;

    /// <summary>
    /// Tail characters used for query embedding: recall embeds only a query's last this-many characters.
    /// 0 (the default) sends the whole query, the behaviour before the setting existed, so no store's recall
    /// changes until its self opts in (Ember, 2026-11-11: retrieval policy touches a self). The embedder
    /// refuses past 512 tokens, and a long message then had no meaning-based ranking at all; 1,200 kept
    /// John's long messages inside it on the recall gold set. That is a heuristic, not a guarantee: token-dense
    /// text (numbers, code, non-Latin script) can still run past 512, so a refused tail is shrunk and retried
    /// (<see cref="Services.QueryEmbedder"/>). Only queries: fragments, and the near-duplicate check against
    /// them, always embed a text's head (<see cref="Services.EmbeddingMemorySurfacer.HeadText"/>).
    /// </summary>
    public int QueryTailChars { get; set; }

    /// <summary>
    /// Chunked query embedding (docs/design/chunked-query-embedding.md): a query longer than this many characters is
    /// cut into ordered, overlapping chunks covering all of it, each embedded and kept (<see cref="Services.QueryChunker"/>,
    /// <see cref="Services.QueryEmbedder"/>). 0 (the default) is off: today's behaviour exactly, so no store's recall
    /// changes until its self opts in. When set, it wins over <see cref="QueryTailChars"/>. At least
    /// <see cref="Services.QueryEmbedder.MinTailChars"/> when set.
    /// </summary>
    public int QueryChunkChars { get; set; }

    /// <summary>How a stored item is scored against the chunks: <c>max</c> (best chunk) or <c>mean</c> (one normalised mean vector).</summary>
    public string QueryChunkScore { get; set; } = "max";

    /// <summary>Characters each chunk after the first reaches back into the previous one; at most a quarter of <see cref="QueryChunkChars"/>.</summary>
    public int QueryChunkOverlap { get; set; } = 150;

    /// <summary>Most chunks embedded per recall; over it, the first and last halves are embedded and the middle is reported.</summary>
    public int QueryChunkMax { get; set; } = 24;

    /// <summary>Most chunks sent in one embedding request.</summary>
    public int QueryChunkBatch { get; set; } = 8;

    /// <summary>Elapsed-time budget for a recall's query embedding, in milliseconds; what is left when it runs out is reported.</summary>
    public int QueryChunkBudgetMs { get; set; } = 8000;

    /// <summary>Settings given that could not be read (an environment value that is not a number); a problem at start.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> Unreadable { get; } = [];

    /// <summary>
    /// Why the chunk settings cannot run, or null when they can. Out-of-range values fail at start (the design's rollout),
    /// rather than quietly running with something nobody chose.
    /// </summary>
    public string? QueryChunkProblem()
    {
        if (Unreadable.Count > 0)
        {
            return $"Not a number: {string.Join(", ", Unreadable)}.";
        }

        if (QueryChunkChars == 0)
        {
            return null;
        }

        var floor = Services.QueryEmbedder.MinTailChars;
        return QueryChunkChars < floor ? $"QueryChunkChars is {QueryChunkChars}: 0 (off) or at least {floor}."
            : QueryChunkOverlap < 0 || QueryChunkOverlap > QueryChunkChars / 4 ? $"QueryChunkOverlap is {QueryChunkOverlap}: 0 to a quarter of QueryChunkChars ({QueryChunkChars / 4})."
            : QueryChunkMax < 1 ? $"QueryChunkMax is {QueryChunkMax}: at least 1."
            : QueryChunkBatch < 1 ? $"QueryChunkBatch is {QueryChunkBatch}: at least 1."
            : QueryChunkBudgetMs < 1 ? $"QueryChunkBudgetMs is {QueryChunkBudgetMs}: at least 1."
            : QueryChunkScore is not ("max" or "mean") ? $"QueryChunkScore is '{QueryChunkScore}': max or mean."
            : null;
    }
}
