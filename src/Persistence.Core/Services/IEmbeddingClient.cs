namespace Persistence.Services;

/// <summary>
/// Turns text into vectors for semantic recall. Deliberately separate from <see cref="IModelClient"/>:
/// the embedding model is not the peer's chat model, has no conversation, no cost tracking, and no
/// stop reason — and every peer should be able to share one small embedding server regardless of which
/// provider it thinks with.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>Whether embedding is configured and enabled. False means callers should not call
    /// <see cref="EmbedAsync"/> and should use their lexical fallback instead.</summary>
    bool IsEnabled { get; }

    /// <summary>The model name vectors are being produced by; stored with each vector so a model
    /// change invalidates rather than silently mixes embedding spaces.</summary>
    string Model { get; }

    /// <summary>
    /// Embeds each text, returning vectors in the same order. Returns an empty list on any failure —
    /// embedding enriches recall and must never be able to fail a turn, so callers treat an empty
    /// result as "no semantic signal available" and fall back.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);

    /// <summary>
    /// <see cref="EmbedAsync"/>, saying why when there are no vectors (a <see cref="ChunkFailure"/> category), so chunked
    /// query embedding splits a chunk the embedder found too large and does not split one it failed for another reason.
    /// A client that cannot tell says <see cref="ChunkFailure.Refused"/>.
    /// </summary>
    async Task<EmbedOutcome> TryEmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var vectors = await EmbedAsync(texts, ct);
        return vectors.Count == texts.Count && vectors.All(v => v.Length > 0)
            ? new EmbedOutcome(vectors, null)
            : new EmbedOutcome([], ChunkFailure.Refused);
    }
}

/// <summary>The vectors of an embedding call, or none and why (<see cref="ChunkFailure"/>).</summary>
public sealed record EmbedOutcome(IReadOnlyList<float[]> Vectors, string? Failure);
