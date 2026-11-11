using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Services;

namespace Persistence.Store;

/// <summary>
/// Recall over what was said, not only over what the peer chose to write down. Most of what a peer
/// forgets is a thing said in passing weeks ago, and it sits in the store as conversation that only a
/// search the peer thinks to run would find. This ranks past utterances against a message the way
/// <see cref="EmbeddingMemorySurfacer"/> ranks authored fragments: exact words (BM25) and meaning
/// (cosine over embeddings) fused by rank.
///
/// <para>
/// The vectors are kept under their own model key (<c>&lt;model&gt;/conversation</c>), so thousands of
/// utterances never enter the authored fragments' candidate pool, and Persistence's own recall, which
/// leaves conversation out on purpose (it is already in context in the app's turn loop), is unchanged.
/// </para>
/// </summary>
internal sealed class ConversationRecaller(
    IContextFragmentRepository fragments,
    IFragmentEmbeddingRepository vectors,
    IEmbeddingClient embeddings,
    QueryEmbedder queryEmbedder)
{
    /// <summary>Shorter than this and an utterance is an acknowledgement ("sounds good"), not a memory.</summary>
    public const int MinLength = 80;

    /// <summary>How many missing vectors one call embeds, so a hook warms the store over a few turns.</summary>
    public const int BackfillPerCall = 64;

    private const int Pool = 40;
    private const int FusionK = 60;

    private string Model => embeddings.Model + "/conversation";

    /// <summary>
    /// The past utterances most related to <paramref name="text"/>, said before <paramref name="before"/>
    /// (what the current session said is already in front of the peer), leaving out <paramref name="exclude"/>.
    /// Another session's words are not in front of it whenever they were said: with <paramref name="session"/>
    /// given, only that session's own are held to <paramref name="before"/>.
    /// </summary>
    public async Task<IReadOnlyList<ContextFragmentEntity>> RecallAsync(
        string text, int count, IReadOnlyCollection<long> exclude, DateTimeOffset? before, string? session = null)
    {
        if (count <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        bool Wanted(ContextFragmentEntity f) =>
            f.FragmentType == ContextFragmentType.ChatMessage && !f.IsDeleted && f.Content.Length >= MinLength
            && f.Importance >= Unimportant && !exclude.Contains(f.Id)
            && (before is null || f.CreatedUtc < before || (session is not null && f.SessionId != session));

        var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray())).Where(w => w.Length > 3 && !Filler.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(w => $"\"{w}\"");
        var match = string.Join(" OR ", terms);
        var lexical = match.Length == 0 ? [] : (await fragments.SearchRelevantAsync(match, Pool * 4)).Where(Wanted).Take(Pool).ToList();

        var semantic = new List<ContextFragmentEntity>();
        if (embeddings.IsEnabled)
        {
            try
            {
                semantic = await SemanticAsync(text, Wanted);
            }
            catch
            {
                // Meaning is the enrichment; the words are the floor.
            }
        }

        if (!embeddings.IsEnabled)
        {
            return Fuse(lexical, semantic, count);
        }

        // Fusion always fills its count, so a message about nothing in particular brought back three
        // acknowledgements ("Sounds good!" matched "Sounds good!"). With both senses on, an utterance comes
        // back only when the words and the meaning both find it; none is better than a weak one.
        var agreed = lexical.Select(f => f.Id).Intersect(semantic.Select(f => f.Id)).ToHashSet();
        return Fuse(lexical.Where(f => agreed.Contains(f.Id)).ToList(), semantic.Where(f => agreed.Contains(f.Id)).ToList(), count);
    }

    /// <summary>Words in nearly every message, which match acknowledgements, not what a message is about.</summary>
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "sounds", "good", "great", "thanks", "thank", "okay", "yeah", "cool", "awesome", "perfect", "nice", "sure",
        "just", "like", "that", "this", "with", "what", "whatever", "anything", "something", "have", "would", "could",
        "should", "there", "then", "than", "them", "they", "your", "yours", "from", "will", "also", "really", "want",
        "know", "think", "much", "very", "some", "about", "into", "here", "hows", "whats", "were", "been", "does",
    };

    /// <summary>Embeds up to <paramref name="limit"/> utterances that have no vector yet; returns how many it did.</summary>
    public async Task<int> BackfillAsync(int limit)
    {
        if (!embeddings.IsEnabled || limit <= 0)
        {
            return 0;
        }

        // Asked of the database: loading every utterance and every vector to find the few new ones cost
        // seconds on each recall.
        var ids = await vectors.GetUnembeddedIdsAsync(Model, ContextFragmentType.ChatMessage, MinLength, limit);
        var missing = ids.Count == 0 ? [] : (await fragments.GetByIdsAsync(ids)).ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        // bge-small reads 512 tokens (2000 characters of code ran past it); the opening of a long utterance is what it is about.
        var texts = missing.Select(f => f.Content.Length > 1000 ? f.Content[..1000] : f.Content).ToList();
        var embedded = await embeddings.EmbedAsync(texts);
        if (embedded.Count != missing.Count)
        {
            return 0;
        }

        for (var i = 0; i < missing.Count; i++)
        {
            await vectors.UpsertAsync(Model, missing[i].Id, embedded[i], VectorMath.ContentHash(texts[i]));
            loaded?.Add(new StoredEmbedding(missing[i].Id, embedded[i], VectorMath.ContentHash(texts[i])));
        }

        return missing.Count;
    }

    /// <summary>
    /// Every utterance's vector, read once and kept: in a process that stays up (`serve`), reading them
    /// all again was half of each recall. Only this class writes them, and it adds what it writes.
    /// </summary>
    private List<StoredEmbedding>? loaded;

    private async Task<List<ContextFragmentEntity>> SemanticAsync(string text, Func<ContextFragmentEntity, bool> wanted)
    {
        await BackfillAsync(BackfillPerCall);
        var stored = loaded ??= (await vectors.GetAllAsync(Model)).ToList();
        if (stored.Count == 0 || await queryEmbedder.EmbedAsync(text) is not { } query)
        {
            return [];
        }

        var top = stored.Select(v => (v.ContextFragmentId, score: VectorMath.Cosine(query, v.Vector)))
            .OrderByDescending(x => x.score).Take(Pool * 2).ToList();
        var byId = (await fragments.GetByIdsAsync(top.Select(x => x.ContextFragmentId))).ToDictionary(f => f.Id);
        return top.Where(x => byId.TryGetValue(x.ContextFragmentId, out var f) && wanted(f))
            .Select(x => byId[x.ContextFragmentId]).Take(Pool).ToList();
    }

    /// <summary>Reciprocal rank fusion, as <see cref="EmbeddingMemorySurfacer"/> fuses: rank positions, not scores.</summary>
    private static List<ContextFragmentEntity> Fuse(
        IReadOnlyList<ContextFragmentEntity> lexical, IReadOnlyList<ContextFragmentEntity> semantic, int count)
    {
        var scores = new Dictionary<long, double>();
        var byId = new Dictionary<long, ContextFragmentEntity>();
        foreach (var ranked in new[] { lexical, semantic })
        {
            for (var i = 0; i < ranked.Count; i++)
            {
                byId[ranked[i].Id] = ranked[i];
                scores[ranked[i].Id] = scores.GetValueOrDefault(ranked[i].Id) + 1.0 / (FusionK + i + 1);
            }
        }

        // Weighted by how much each mattered when it was said (Salience), as fragment recall weighs by importance.
        return scores.OrderByDescending(s => s.Value * (0.5 + byId[s.Key].Importance)).ThenBy(s => s.Key)
            .Take(count).Select(s => byId[s.Key]).ToList();
    }

    /// <summary>Below this an utterance is an acknowledgement (<see cref="Salience"/>), never recalled.</summary>
    private const float Unimportant = 0.1f;
}
