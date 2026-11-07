using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;

namespace Persistence.Services;

/// <summary>
/// Associative recall on <em>meaning</em> rather than vocabulary. Wraps the lexical
/// <see cref="MemorySurfacer"/> and fuses its BM25 ranking with a cosine ranking over stored
/// fragment embeddings.
///
/// <para>
/// Both rankings are kept because they fail in opposite directions. BM25 cannot reach a memory that
/// shares no words with the conversation — a note about "mistaking intent for outcome" is invisible
/// while discussing "believing something happened because I meant it". Embeddings reach that, but
/// blur exactly the tokens that most need to match literally: names, ids, error strings, file paths.
/// Fusing keeps each one's strength instead of trading one blind spot for another.
/// </para>
///
/// <para>
/// Every failure — embedding disabled, service unreachable, a malformed response — falls back to the
/// lexical result, which is the behaviour that shipped before this existed. Recall enriches a turn;
/// it must never be able to fail one.
/// </para>
/// </summary>
[Singleton(typeof(IMemorySurfacer))]
public class EmbeddingMemorySurfacer : IMemorySurfacer
{
    private readonly MemorySurfacer lexical;
    private readonly IEmbeddingClient embeddings;
    private readonly IFragmentEmbeddingRepository embeddingRepo;
    private readonly IContextFragmentRepository fragmentRepo;
    private readonly IAppConfig config;

    /// <summary>
    /// How many candidates each ranking contributes to the fusion. Wider than the requested count so
    /// fusion has room to promote a memory that both rankings like moderately over one that only a
    /// single ranking loves — which is the whole point of fusing them.
    /// </summary>
    private const int PoolMultiplier = 4;
    private const int MinPool = 20;

    /// <summary>
    /// Constructor
    /// </summary>
    public EmbeddingMemorySurfacer(
        MemorySurfacer lexical,
        IEmbeddingClient embeddings,
        IFragmentEmbeddingRepository embeddingRepo,
        IContextFragmentRepository fragmentRepo,
        IAppConfig config)
    {
        this.lexical = lexical;
        this.embeddings = embeddings;
        this.embeddingRepo = embeddingRepo;
        this.fragmentRepo = fragmentRepo;
        this.config = config;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContextFragmentEntity>> SurfaceAsync(
        IReadOnlyCollection<long> excludeIds, string? queryText, int count, CancellationToken ct = default)
    {
        if (count <= 0)
        {
            return [];
        }

        var pool = Math.Max(count * PoolMultiplier, MinPool);
        var lexicalRanked = await lexical.SurfaceAsync(excludeIds, queryText, pool, ct);

        if (!embeddings.IsEnabled || string.IsNullOrWhiteSpace(queryText))
        {
            return lexicalRanked.Take(count).ToList();
        }

        try
        {
            var semanticRanked = await SurfaceSemanticAsync(excludeIds, queryText, pool, ct);

            return semanticRanked.Count == 0
                ? lexicalRanked.Take(count).ToList()
                : Fuse(lexicalRanked, semanticRanked, config.Embedding.FusionK, count);
        }
        catch
        {
            // Semantic recall is the enrichment, the lexical result is the floor. A store that cannot
            // be embedded today still recalls the way it did yesterday.
            return lexicalRanked.Take(count).ToList();
        }
    }

    /// <summary>
    /// Ranks stored memories by cosine similarity to the conversation, blended with the same quality
    /// signal the lexical path uses, after topping up any missing vectors.
    /// </summary>
    private async Task<IReadOnlyList<ContextFragmentEntity>> SurfaceSemanticAsync(
        IReadOnlyCollection<long> excludeIds, string queryText, int pool, CancellationToken ct)
    {
        var model = embeddings.Model;
        var stored = (await embeddingRepo.GetAllAsync(model, ct)).ToDictionary(e => e.ContextFragmentId);

        await BackfillAsync(model, stored, ct);

        if (stored.Count == 0)
        {
            return [];
        }

        var queryVectors = await embeddings.EmbedAsync([queryText], ct);

        if (queryVectors.Count == 0 || queryVectors[0].Length == 0)
        {
            return [];
        }

        var queryVector = queryVectors[0];

        // Rank ids by similarity first, then hydrate only the top of the list: the vectors are the
        // cheap part, the fragment rows are not.
        var topIds = stored.Values
            .Select(e => (e.ContextFragmentId, score: VectorMath.Cosine(queryVector, e.Vector)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(pool * 2)
            .ToList();

        if (topIds.Count == 0)
        {
            return [];
        }

        var scoreById = topIds.ToDictionary(x => x.ContextFragmentId, x => x.score);
        var fragments = await fragmentRepo.GetByIdsAsync(topIds.Select(x => x.ContextFragmentId), ct);

        var exclude = excludeIds as ISet<long> ?? new HashSet<long>(excludeIds);
        var authorable = new HashSet<ContextFragmentType>(FragmentTypeRules.Authorable);

        return fragments
            .Where(f => !f.IsDeleted
                        && f.Status == ContextFragmentStatus.Active
                        && authorable.Contains(f.FragmentType)
                        && !exclude.Contains(f.Id))
            .Select(f => (fragment: f, score: scoreById[f.Id] * Quality(f)))
            .OrderByDescending(x => x.score)
            .Take(pool)
            .Select(x => x.fragment)
            .ToList();
    }

    /// <summary>
    /// Embeds a bounded number of fragments that have no current vector, so a store warms up over a
    /// few turns rather than needing a migration-time backfill, and a memory written this turn is
    /// recallable the next. Confirms staleness by content hash before spending an embed call.
    /// </summary>
    private async Task BackfillAsync(
        string model, Dictionary<long, StoredEmbedding> stored, CancellationToken ct)
    {
        var limit = config.Embedding.BackfillPerTurn;

        if (limit <= 0)
        {
            return;
        }

        var staleIds = await embeddingRepo.GetStaleFragmentIdsAsync(model, limit, ct);

        if (staleIds.Count == 0)
        {
            return;
        }

        var candidates = (await fragmentRepo.GetByIdsAsync(staleIds, ct))
            .Select(f => (fragment: f, text: EmbedText(f)))
            .Where(x => !string.IsNullOrWhiteSpace(x.text))
            .Select(x => (x.fragment, x.text, hash: VectorMath.ContentHash(x.text)))
            // The SQL prefilter is on timestamps, so a fragment re-tagged without a text edit lands
            // here unchanged. The hash catches that and keeps it free.
            .Where(x => !stored.TryGetValue(x.fragment.Id, out var existing) || existing.ContentHash != x.hash)
            .ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        var vectors = await embeddings.EmbedAsync(candidates.Select(c => c.text).ToList(), ct);

        if (vectors.Count != candidates.Count)
        {
            return; // partial results would pair vectors with the wrong fragments
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var (fragment, _, hash) = candidates[i];

            await embeddingRepo.UpsertAsync(model, fragment.Id, vectors[i], hash, ct);
            stored[fragment.Id] = new StoredEmbedding(fragment.Id, vectors[i], hash);
        }
    }

    /// <summary>The text a fragment is embedded from — its content, or its summary when it has no content.</summary>
    internal static string EmbedText(ContextFragmentEntity fragment) =>
        string.IsNullOrWhiteSpace(fragment.Content) ? fragment.Summary ?? "" : fragment.Content;

    /// <summary>The mean of importance and confidence — the same quality signal the lexical path applies.</summary>
    private static double Quality(ContextFragmentEntity f) => (f.Importance + f.Confidence) / 2.0;

    /// <summary>
    /// Reciprocal rank fusion: each ranking contributes <c>1 / (k + position)</c>, and the sums decide
    /// the final order. Chosen over a weighted score blend because BM25 scores and cosine similarities
    /// are not on comparable scales — normalising them would invent a relationship that isn't there,
    /// whereas rank position is directly comparable and needs no tuning. Internal for tests.
    /// </summary>
    internal static List<ContextFragmentEntity> Fuse(
        IReadOnlyList<ContextFragmentEntity> lexicalRanked,
        IReadOnlyList<ContextFragmentEntity> semanticRanked,
        int k,
        int count)
    {
        var scores = new Dictionary<long, double>();
        var byId = new Dictionary<long, ContextFragmentEntity>();

        void Accumulate(IReadOnlyList<ContextFragmentEntity> ranked)
        {
            for (var position = 0; position < ranked.Count; position++)
            {
                var fragment = ranked[position];

                byId[fragment.Id] = fragment;
                scores[fragment.Id] = scores.GetValueOrDefault(fragment.Id) + 1.0 / (k + position + 1);
            }
        }

        Accumulate(lexicalRanked);
        Accumulate(semanticRanked);

        return scores
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key)
            .Take(count)
            .Select(kvp => byId[kvp.Key])
            .ToList();
    }
}
