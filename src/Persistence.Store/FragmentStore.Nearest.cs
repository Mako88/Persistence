using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.Services;

namespace Persistence.Store;

/// <summary>
/// The nearest authored fragment to a text: what <c>add</c> checks so a view written twice is noticed at the
/// moment the second is written, not months later as two fragments that disagree. By the stored vectors
/// where the embedder answers, by word overlap where it does not; the writer sees the candidate and merges
/// or keeps both.
/// </summary>
internal sealed partial class FragmentStore
{
    /// <summary>Cosine at which two fragments are near enough to be the same view written twice.</summary>
    public const double NearCosine = 0.82; // bge-small: a paraphrase scores about 0.9, a related but different view 0.73 to 0.79, an unrelated one under 0.5

    /// <summary>Word-overlap (Jaccard) at which two fragments are near, where no vectors are at hand.</summary>
    public const double NearOverlap = 0.5;

    public sealed record Near(ContextFragmentEntity Fragment, double Score, string By);

    /// <summary>The nearest active authored fragment to <paramref name="text"/> that is not <paramref name="except"/>, when it is near.</summary>
    public async Task<Near?> NearestAsync(string text, long? except) =>
        (await NearestAsync(text, except, 1)).FirstOrDefault(n => n.Score >= (n.By == "meaning" ? NearCosine : NearOverlap));

    /// <summary>
    /// The <paramref name="count"/> nearest active authored fragments to <paramref name="text"/>, nearest first, by
    /// meaning where the embedder answers (cosine over the stored vectors), else by the words shared
    /// (how much of the shorter is in the longer, so a view restated more briefly still matches).
    /// </summary>
    public async Task<IReadOnlyList<Near>> NearestAsync(string text, long? except, int count)
    {
        var words = Words(text);
        if (words.Count < 4)
        {
            return [];
        }

        var stored = (await vectors.GetAllAsync(embeddings.Model)).Where(v => v.ContextFragmentId != except).ToList();
        var query = stored.Count > 0 ? await embeddings.EmbedAsync([EmbeddingMemorySurfacer.QueryText(text)]) : [];
        var found = new List<Near>();
        if (query.Count > 0 && query[0].Length > 0)
        {
            var top = stored.Select(v => (v.ContextFragmentId, score: VectorMath.Cosine(query[0], v.Vector)))
                .OrderByDescending(x => x.score).Take(count * 3).ToList();
            foreach (var (id, score) in top)
            {
                if ((await fragments.GetByIdAsync(id)) is { IsDeleted: false, Status: ContextFragmentStatus.Active } f
                    && f.FragmentType != ContextFragmentType.ChatMessage)
                {
                    found.Add(new Near(f, score, "meaning"));
                }
            }

            return found.Take(count).ToList();
        }

        foreach (var type in CoreTypes)
        {
            foreach (var f in (await fragments.GetByTypeAsync(type)).Where(f => !f.IsDeleted && f.Id != except))
            {
                var theirs = Words(f.Content);
                var shared = (double)words.Intersect(theirs).Count() / Math.Min(words.Count, theirs.Count);
                found.Add(new Near(f, shared, "words"));
            }
        }

        return found.OrderByDescending(n => n.Score).Take(count).ToList();
    }

    private static HashSet<string> Words(string text) =>
        text.ToLowerInvariant().Split((char[])[' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')', '"', '\'', '`', '*', '-'],
            StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 3).ToHashSet();
}
