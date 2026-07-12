using Persistence.Data.Entities;

namespace Persistence.Services;

/// <summary>
/// Associative recall: given the current conversation, surfaces the peer's own authored memories that
/// are relevant to it but not already loaded — so relevant notes appear on their own, the way a mind
/// surfaces a related thought, instead of the peer having to search for them.
/// </summary>
public interface IMemorySurfacer
{
    /// <summary>
    /// Returns up to <paramref name="count"/> authored fragments (Identity/Relational/Personal/Summary)
    /// relevant to <paramref name="queryText"/> (full-text/BM25) but not in <paramref name="excludeIds"/>,
    /// ranked by a blend of relevance × importance × confidence. Empty when disabled (count ≤ 0) or when
    /// the text yields no searchable terms.
    /// </summary>
    Task<IReadOnlyList<ContextFragmentEntity>> SurfaceAsync(
        IReadOnlyCollection<long> excludeIds, string? queryText, int count, CancellationToken ct = default);
}
