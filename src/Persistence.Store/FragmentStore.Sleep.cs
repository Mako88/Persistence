using System.Text;
using Persistence.Data.Entities;

namespace Persistence.Store;

internal sealed partial class FragmentStore
{
    /// <summary>
    /// The worklist for a sleep pass (`sleep-list`): what a peer's periodic curation should look at, since a store
    /// only grows otherwise. Oversized fragments, the likeliest to be mostly passing work around a small lasting
    /// part (John, 2026-11-09: distill the 10% that lasts into a fragment that supersedes the whole); pairs near
    /// enough to be one view held twice; and the authored fragments in id order, so the caller can join use
    /// counts it keeps (the hook's uses.jsonl) without the store knowing them. The judgment is the peer's: this
    /// only says where to look.
    /// </summary>
    public async Task<string> SleepListAsync(int over, int limit)
    {
        var authored = new List<ContextFragmentEntity>();
        foreach (var type in CoreTypes)
        {
            authored.AddRange((await fragments.GetByTypeAsync(type)).Where(f => !f.IsDeleted && !f.IsProtected));
        }

        var text = new StringBuilder();
        var big = authored.Where(f => f.Content.Length > over).OrderByDescending(f => f.Content.Length).Take(limit).ToList();
        text.Append($"## Oversized ({big.Count} over {over} characters, longest first): distill what lasts, supersede the rest\n\n");
        foreach (var f in big)
        {
            text.Append($"- #{f.Id} ({f.Content.Length} chars, i:{f.Importance:0.##}, {f.LastModifiedUtc.ToUniversalTime():yyyy-MM-dd}): {Clip(f.Content, 120)}\n");
        }

        var pairs = new List<(long A, long B, double Score, string By)>();
        var paired = new HashSet<long>();
        foreach (var f in authored.OrderBy(f => f.Id))
        {
            if (paired.Contains(f.Id) || pairs.Count >= limit)
            {
                continue;
            }

            if (await NearestAsync(f.Content, f.Id) is { } near && !paired.Contains(near.Fragment.Id))
            {
                pairs.Add((f.Id, near.Fragment.Id, near.Score, near.By));
                paired.Add(f.Id);
                paired.Add(near.Fragment.Id);
            }
        }

        text.Append($"\n## Near-duplicates ({pairs.Count} pairs): one view held twice, merge with `supersede a,b`\n\n");
        foreach (var (a, b, score, by) in pairs)
        {
            text.Append($"- #{a} and #{b} ({by}, {score:0.00})\n");
        }

        text.Append($"\n## Authored ({authored.Count}): ids for joining use counts\n\n");
        text.Append(string.Join(' ', authored.Select(f => f.Id).Order())).Append('\n');
        return text.ToString();
    }
}
