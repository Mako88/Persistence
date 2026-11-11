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
    private readonly QueryEmbedder queryEmbedder;

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
        IAppConfig config,
        QueryEmbedder queryEmbedder)
    {
        this.lexical = lexical;
        this.embeddings = embeddings;
        this.embeddingRepo = embeddingRepo;
        this.fragmentRepo = fragmentRepo;
        this.config = config;
        this.queryEmbedder = queryEmbedder;
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
        catch (Exception ex)
        {
            // Semantic recall is the enrichment, the lexical result is the floor. A store that cannot
            // be embedded today still recalls the way it did yesterday, and says so.
            queryEmbedder.Note($"semantic recall failed ({ex.Message})");
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

        if (await queryEmbedder.EmbedAsync(queryText, ct) is not { } queryVector)
        {
            return [];
        }

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

        // A match on a superseded view stands for the head of its chain.
        var standsFor = await SupersessionHeads.ResolveAsync(
            fragmentRepo, fragments.Where(SupersessionHeads.IsCandidate), ct);

        return fragments
            .Where(f => standsFor.ContainsKey(f.Id))
            .Select(f => (head: standsFor[f.Id], similarity: scoreById[f.Id]))
            .Where(x => !x.head.IsDeleted
                        && x.head.Status == ContextFragmentStatus.Active
                        && authorable.Contains(x.head.FragmentType)
                        && !exclude.Contains(x.head.Id))
            .Select(x => (x.head, score: x.similarity * MemorySurfacer.Quality(x.head, config.Embedding.QualityWeight)))
            .GroupBy(x => x.head.Id)
            .Select(g => g.OrderByDescending(x => x.score).First()) // old and head both matching: the better score
            .OrderByDescending(x => x.score)
            .Take(pool)
            .Select(x => x.head)
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

    /// <summary>
    /// The text a fragment is embedded from — the opening of its content, or of its summary when it has
    /// no content. Small embedders read 512 tokens; one fragment past that failed the whole backfill batch
    /// on every call, so nothing written after it was ever embedded. The opening is what it is about.
    /// </summary>
    internal static string EmbedText(ContextFragmentEntity fragment) =>
        HeadText(string.IsNullOrWhiteSpace(fragment.Content) ? fragment.Summary ?? "" : fragment.Content);

    /// <summary>
    /// The head policy: a text's first <see cref="MaxEmbedChars"/> characters. Every fragment vector is of its
    /// head, so a check that compares a text with the stored fragments (the near-duplicate check on <c>add</c>,
    /// sleep's pairing) must embed the text's head too, whatever the query policy: embedded by its tail, a long
    /// exact duplicate scored 0.793 against itself and was missed (Arden, 2026-11-11). A query's own policy is
    /// <see cref="QueryEmbedder"/>'s.
    /// </summary>
    public static string HeadText(string text) => text.Length > MaxEmbedChars ? text[..MaxEmbedChars] : text;

    /// <summary>Characters of a fragment embedded: inside 512 tokens for prose, a heuristic rather than a guarantee for dense text.</summary>
    public const int MaxEmbedChars = 1200;

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
