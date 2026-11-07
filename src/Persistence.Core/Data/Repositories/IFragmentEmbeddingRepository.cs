namespace Persistence.Data.Repositories;

/// <summary>A stored embedding: the fragment it describes, its vector, and the content it was made from.</summary>
/// <param name="ContextFragmentId">The fragment this vector represents.</param>
/// <param name="Vector">The embedding, already decoded from its stored blob.</param>
/// <param name="ContentHash">Fingerprint of the text that was embedded, for staleness detection.</param>
public record StoredEmbedding(long ContextFragmentId, float[] Vector, string ContentHash);

/// <summary>
/// Persistence for fragment embeddings — derived data supporting semantic recall, keyed by fragment
/// *and* embedding model so vectors from different models never mix.
/// </summary>
public interface IFragmentEmbeddingRepository
{
    /// <summary>Loads every stored vector for <paramref name="model"/>. Peer stores hold hundreds to low
    /// thousands of authored fragments, so recall sweeps them all and scores in memory.</summary>
    Task<IReadOnlyList<StoredEmbedding>> GetAllAsync(string model, CancellationToken ct = default);

    /// <summary>
    /// Returns ids of active, authored, non-deleted fragments whose vector for <paramref name="model"/>
    /// is missing or possibly stale (embedded before the fragment was last modified). This is a cheap
    /// SQL prefilter on timestamps; the caller confirms with the content hash before paying for an
    /// embed, so a change that did not touch the text (a re-tag, an importance tweak) costs nothing.
    /// Ordered newest-first so the memories most likely to matter warm up first.
    /// </summary>
    Task<IReadOnlyList<long>> GetStaleFragmentIdsAsync(
        string model, int limit, CancellationToken ct = default);

    /// <summary>Inserts or replaces the vector for a fragment under <paramref name="model"/>.</summary>
    Task UpsertAsync(
        string model, long fragmentId, float[] vector, string contentHash, CancellationToken ct = default);
}
