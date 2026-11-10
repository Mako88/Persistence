using Persistence.Data.Entities;
using Persistence.Data.Repositories;

namespace Persistence.Services;

/// <summary>
/// Recall's way through a superseded fragment: a match on an old view stands for the current one, the head
/// of its chain, however many supersessions on. Shared by the lexical and the semantic surfacer.
/// </summary>
internal static class SupersessionHeads
{
    /// <summary>
    /// Maps each candidate's id to the fragment that stands for it: itself, or for a superseded one the head of
    /// its chain. A forgotten candidate stands for nothing; a head that cannot be loaded leaves the candidate
    /// unmapped. The caller still applies its own filters (status, type, exclusion) to what comes back.
    /// </summary>
    public static async Task<Dictionary<long, ContextFragmentEntity>> ResolveAsync(
        IContextFragmentRepository repo, IEnumerable<ContextFragmentEntity> candidates, CancellationToken ct)
    {
        var live = candidates.Where(c => !c.IsDeleted).ToList();
        var standsFor = live.Where(c => c.SupersededById is null).ToDictionary(c => c.Id, c => c);
        var superseded = live.Where(c => c.SupersededById is not null).ToList();

        if (superseded.Count == 0)
        {
            return standsFor;
        }

        var heads = await repo.GetHeadsAsync(superseded.Select(c => c.Id).ToList(), ct);
        var loaded = (await repo.GetByIdsAsync(heads.Values.Select(h => h.HeadId).Distinct().ToList(), ct))
            .ToDictionary(h => h.Id);

        foreach (var old in superseded)
        {
            if (heads.TryGetValue(old.Id, out var head) && loaded.TryGetValue(head.HeadId, out var current))
            {
                standsFor[old.Id] = current;
            }
        }

        return standsFor;
    }

    /// <summary>Whether a fragment may be a candidate at all: live, or archived only because it was superseded.</summary>
    public static bool IsCandidate(ContextFragmentEntity f) =>
        !f.IsDeleted && (f.Status == ContextFragmentStatus.Active || f.SupersededById is not null);
}
