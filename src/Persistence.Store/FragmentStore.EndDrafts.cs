using System.Text;
using Persistence.Data.Repositories;

namespace Persistence.Store;

/// <summary>
/// Close PR B (docs/design/close-build-plan.md B1 to B3): concept facts whose sources are all closed. The flag is
/// derived when a fact is read; a draft of the fact's end is made by the close that closes its last source, when
/// every source shares one ended-by; only the waking self approves or drops a draft, and approval revalidates it.
/// </summary>
internal sealed partial class FragmentStore
{
    private const string EndDraftAsleep =
        "A sleep pass doesn't approve or drop a fact's end: the night may list drafts (`concept end-drafts`); the waking self decides.";

    /// <summary>
    /// <c>[sources closed] </c> when the fact has at least one source and every one is closed. Zero sources is never
    /// "all closed" (Ember 4). An ended fact doesn't need it: its own tail says it ended.
    /// </summary>
    internal static string ClosedMark(FactRow f, IReadOnlyList<FactSourceCloseRow>? closes) =>
        !f.Ended && AllClosed(closes) ? "[sources closed] " : "";

    private static bool AllClosed(IReadOnlyList<FactSourceCloseRow>? closes) => closes is { Count: > 0 } && closes.All(c => c.Closed);

    /// <summary>
    /// Under a fact in <c>concept show</c>: for a standing fact with every source closed, each source with its own
    /// ended-by (or "no ref") and reason, and its pending draft or why there is none; for an ended fact, the sources'
    /// close reasons as its approved draft kept them.
    /// </summary>
    internal static string ClosedLines(FactRow f, IReadOnlyList<FactSourceCloseRow>? closes, IReadOnlyList<FactEndDraftRow> drafts)
    {
        var sb = new StringBuilder();
        if (f.Ended)
        {
            if (drafts.FirstOrDefault(d => d.Status == "approved") is { } approved)
            {
                foreach (var reason in approved.Reasons.Split('\n'))
                {
                    sb.Append($"\n    source closed: {Cut(OneLine(reason), 200)}");
                }
            }

            return sb.ToString();
        }

        if (!AllClosed(closes))
        {
            return "";
        }

        foreach (var c in closes!)
        {
            sb.Append($"\n    source #{c.FragmentId} closed {Day(c.ClosedUtc)} (ended by {c.EndedBy ?? "no ref"}): {Cut(OneLine(c.Reason ?? ""), 160)}");
        }

        if (drafts.FirstOrDefault(d => d.Status == "pending") is { } pending)
        {
            sb.Append($"\n    end draft #{pending.Id} pending: `concept end-draft {pending.Id} --approve --reason <why>` ends it; `--drop --reason <why>` keeps it standing.");
        }
        else
        {
            // Say which of the three it is: no shared event, a draft the self dropped, or a close from before drafts existed.
            var last = drafts.FirstOrDefault();
            sb.Append(ConceptRepository.SharedEnding(closes!) is null
                ? "\n    no end draft: the sources don't share one ended-by, so decide by hand (`concept reject`, or leave it)."
                : last is not null
                    ? $"\n    no end draft pending: #{last.Id} was {last.Status} ({Cut(OneLine(last.Resolution ?? ""), 120)})."
                    : "\n    no end draft: its last source closed before end drafts existed (close PR B), so decide by hand (`concept reject`, or leave it).");
        }

        return sb.ToString();
    }

    /// <summary>The pending drafts, each with its fact and the sources' reasons. Readable in a sleep pass.</summary>
    public async Task<string> EndDraftsAsync()
    {
        var pending = await concepts.PendingEndDraftsAsync();
        if (pending.Count == 0)
        {
            return "No end drafts pending.";
        }

        var sb = new StringBuilder($"{pending.Count} end draft{(pending.Count == 1 ? "" : "s")} pending (`concept end-draft <id> --approve|--drop --reason <why>`):");
        foreach (var d in pending)
        {
            var fact = await concepts.GetFactAsync(d.FactId);
            sb.Append($"\n  end draft #{d.Id}, drafted {Day(d.CreatedUtc)}, ended by {d.EndedBy}:");
            sb.Append("\n    " + (fact is null ? $"fact #{d.FactId} (missing)" : await RenderFactAsync(fact)));
            foreach (var reason in d.Reasons.Split('\n'))
            {
                sb.Append($"\n    source closed: {Cut(OneLine(reason), 200)}");
            }
        }

        return sb.ToString();
    }

    /// <summary>The pending drafts that rest on <paramref name="fragmentId"/>'s close: what a close just drafted.</summary>
    public async Task<IReadOnlyList<FactEndDraftRow>> EndDraftsRestingOnAsync(long fragmentId)
    {
        var pending = await concepts.PendingEndDraftsAsync();
        var sources = await concepts.SourcesAsync(pending.Select(d => d.FactId).ToList());
        return pending.Where(d => (sources.GetValueOrDefault(d.FactId) ?? []).Contains(fragmentId)).ToList();
    }

    /// <summary>
    /// The self approves (<paramref name="approve"/>) or drops a pending draft, with a reason. Approval revalidates
    /// in its own transaction; a stale draft is dropped and the command refuses with which check failed.
    /// </summary>
    public async Task<string> DecideEndDraftAsync(long id, bool approve, bool drop, string? reason)
    {
        if (approve == drop)
        {
            throw new UsageException("concept end-draft needs exactly one of --approve or --drop.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new UsageException(approve
                ? "concept end-draft --approve needs --reason: it becomes the fact's own reason for ending."
                : "concept end-draft --drop needs --reason: why the fact still stands although its sources closed.");
        }

        if (await OpenPassAsync(includeExpired: false) is not null)
        {
            throw new RefusedException(EndDraftAsleep);
        }

        var draft = await concepts.GetEndDraftAsync(id) ?? throw new RefusedException($"There is no end draft #{id}.");
        if (draft.Status != "pending")
        {
            throw new RefusedException($"End draft #{id} is {draft.Status}{(draft.Resolution is { } r ? $" ({OneLine(r)})" : "")}, not pending.");
        }

        if (drop)
        {
            return await concepts.DropEndDraftAsync(id, reason.Trim())
                ? $"End draft #{id} dropped; fact #{draft.FactId} stands, still marked 'sources closed'."
                : throw new RefusedException($"End draft #{id} is no longer pending.");
        }

        if (await concepts.ApproveEndDraftAsync(id, reason.Trim()) is { } stale)
        {
            throw new RefusedException($"End draft #{id} is stale ({stale}), so it was dropped and fact #{draft.FactId} is unchanged. "
                + "A fresh draft comes with the next close that closes its last source; or end it by hand later.");
        }

        var fact = (await concepts.GetFactAsync(draft.FactId))!;
        return $"Fact #{fact.Id} ended {Day(fact.EndedUtc)}: {OneLine(fact.EndReason ?? "")} (ended by {fact.EndedBy}). "
            + "It leaves current facts; `concept show <name> --history` keeps it, with its sources' reasons.";
    }
}
