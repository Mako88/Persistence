using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using System.Text.RegularExpressions;

namespace Persistence.Services;

/// <summary>
/// <see cref="IMemorySurfacer"/> over the fragment store's full-text index. Builds an FTS query from
/// the recent conversation (salient terms OR'd together), asks the repository for BM25-ranked matches,
/// keeps only the peer's authored memories not already in context, and re-ranks them by relevance ×
/// importance × confidence. All per-model knowledge stays out of here — it's pure retrieval + ranking.
/// </summary>
[Singleton(typeof(IMemorySurfacer))]
public partial class MemorySurfacer : IMemorySurfacer
{
    private readonly IContextFragmentRepository fragmentRepo;

    // Relevance decays gently with BM25 rank position, so a slightly-less-relevant but more
    // important/confident memory can still out-rank a top match — both signals matter.
    private const double PositionDecay = 0.15;

    // Cap the query terms so a long turn doesn't build a huge MATCH expression.
    private const int MaxTerms = 24;

    public MemorySurfacer(IContextFragmentRepository fragmentRepo) => this.fragmentRepo = fragmentRepo;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContextFragmentEntity>> SurfaceAsync(
        IReadOnlyCollection<long> excludeIds, string? queryText, int count, CancellationToken ct = default)
    {
        if (count <= 0 || BuildFtsQuery(queryText) is not { } ftsQuery)
        {
            return [];
        }

        IReadOnlyList<ContextFragmentEntity> candidates;
        try
        {
            // Pull a wider pool than needed so re-ranking has room to promote important/confident hits.
            candidates = (await fragmentRepo.SearchRelevantAsync(ftsQuery, Math.Max(count * 5, 25), ct)).ToList();
        }
        catch
        {
            return []; // a malformed FTS expression must never break the turn
        }

        var exclude = excludeIds as ISet<long> ?? new HashSet<long>(excludeIds);
        var authorable = new HashSet<ContextFragmentType>(FragmentTypeRules.Authorable);

        return candidates
            .Select((fragment, position) => (fragment, position)) // position = BM25 rank (best first)
            .Where(x => x.fragment.Id > 0
                        && !x.fragment.IsDeleted
                        && x.fragment.Status == ContextFragmentStatus.Active
                        && authorable.Contains(x.fragment.FragmentType)
                        && !exclude.Contains(x.fragment.Id))
            .Select(x => (x.fragment, score: Score(x.fragment, x.position)))
            .OrderByDescending(x => x.score)
            .Take(count)
            .Select(x => x.fragment)
            .ToList();
    }

    /// <summary>relevance (rank-decayed) × the mean of importance and confidence.</summary>
    private static double Score(ContextFragmentEntity f, int position)
    {
        var relevance = 1.0 / (1.0 + PositionDecay * position);
        var quality = (f.Importance + f.Confidence) / 2.0;
        return relevance * quality;
    }

    /// <summary>
    /// Turns free text into an FTS5 match expression: distinct salient terms (≥3 chars, not stop-words,
    /// most-frequent first), each quoted and OR'd. Null when there's nothing searchable. Internal for tests.
    /// </summary>
    internal static string? BuildFtsQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var terms = TermRegex().Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => !StopWords.Contains(t))
            .GroupBy(t => t)
            .OrderByDescending(g => g.Count()) // frequency = salience
            .Select(g => g.Key)
            .Take(MaxTerms)
            .ToList();

        return terms.Count == 0 ? null : string.Join(" OR ", terms.Select(t => $"\"{t}\""));
    }

    // A word starting with a letter, ≥3 chars total (drops pure numbers, punctuation, and tiny tokens).
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]{2,}")]
    private static partial Regex TermRegex();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "your", "with", "that", "this", "have", "has",
        "had", "was", "were", "will", "would", "should", "could", "can", "from", "they", "them", "there",
        "then", "than", "what", "when", "where", "which", "who", "whom", "how", "why", "about", "into",
        "out", "over", "under", "just", "like", "some", "any", "all", "one", "two", "get", "got", "let",
        "its", "it's", "i'm", "i've", "i'd", "i'll", "don't", "doesn't", "didn't", "isn't", "aren't",
        "wasn't", "weren't", "won't", "can't", "couldn't", "shouldn't", "wouldn't", "here", "been", "being",
        "does", "did", "doing", "yes", "yeah", "okay", "now", "also", "very", "much", "more", "most",
    };
}
