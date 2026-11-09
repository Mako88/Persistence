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
}
