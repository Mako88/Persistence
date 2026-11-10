using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using System.Text;

namespace Persistence.Store;

/// <summary>
/// The concept graph's rules (docs/design/concept-graph.md, agreed with Arden 2026-11-10). The tables and their
/// triggers hold rule 1 (no fact stands without a source); this holds the rest:
/// <list type="bullet">
/// <item>A fact is a <b>proposal</b>, not active, when its subject or object is a person, a self or a value, or when any
/// source is an Identity or Relational fragment. A concept whose kind the self has not confirmed counts as a person.</item>
/// <item>Change <b>supersedes</b>: a new fact names the one it replaces, which keeps its sources and points forward once
/// the new one stands. A proposal replaces nothing until it is accepted.</item>
/// <item>A <b>rejected</b> fact is kept, and the same text about the same concept is refused rather than proposed again,
/// until the self reopens it (a "not yet" is not a "never", Arden).</item>
/// <item>The same text about the same concept and object as a fact that stands is not added again: the add says which stands.</item>
/// <item>Anyone may <b>flag</b> a fact; accepting, rejecting and confirming a kind are the self's, and never in a sleep pass.</item>
/// </list>
/// </summary>
internal sealed partial class FragmentStore
{
    public static readonly IReadOnlyList<string> ConceptKinds = ["thing", "person", "self", "value"];

    private static readonly IReadOnlySet<int> WhoSources = new HashSet<int>
        { (int)ContextFragmentType.Identity, (int)ContextFragmentType.Relational };

    /// <summary>The kind a concept counts as for the proposal rule: its own once confirmed, a person until then.</summary>
    public static string EffectiveKind(ConceptRow c) => c.Confirmed ? c.Kind : "person";

    private static bool AboutWho(ConceptRow c) => EffectiveKind(c) is "person" or "self" or "value";

    /// <summary>The concept named (by name or alias), made as an unconfirmed <paramref name="kind"/> if there is none.</summary>
    public async Task<ConceptRow> ConceptAsync(string name, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new UsageException("A concept needs a name.");
        }

        return await concepts.FindAsync(name)
            ?? (kind is null ? await concepts.AddConceptAsync(name, "thing", null, kindSaid: false) : await concepts.AddConceptAsync(name, KindOrRefuse(kind), null));
    }

    private static string KindOrRefuse(string kind) =>
        ConceptKinds.Contains(kind) ? kind : throw new UsageException($"'{kind}' is not a kind; one of {string.Join(", ", ConceptKinds)}.");

    /// <summary>
    /// Adds a fact about <paramref name="subject"/>, citing <paramref name="sources"/>. Returns the fact as stored:
    /// active, or proposed when it is about who someone is.
    /// </summary>
    public async Task<FactRow> AddFactAsync(string subject, string text, IReadOnlyCollection<long> sources, string? @object = null,
        bool isState = false, string? asOf = null, string? extractedBy = null, long? supersedes = null, string? kind = null) =>
        (await AddFactWhyAsync(subject, text, sources, @object, isState, asOf, extractedBy, supersedes, kind)).Fact;

    /// <summary><see cref="AddFactAsync"/>, with why it is a proposal (null when it stands), and whether it already stood:
    /// then nothing was added and the fact returned is the one standing.</summary>
    public async Task<(FactRow Fact, string? WhyProposed, bool AlreadyStood)> AddFactWhyAsync(string subject, string text, IReadOnlyCollection<long> sources, string? @object = null,
        bool isState = false, string? asOf = null, string? extractedBy = null, long? supersedes = null, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new UsageException("A fact needs its text (--fact).");
        }

        if (sources.Count == 0)
        {
            throw new RefusedException("A fact must cite at least one source fragment (--source <id>): it is a view of fragments, never a source of truth.");
        }

        var types = await concepts.FragmentTypesAsync(sources);
        var missing = sources.Where(s => !types.ContainsKey(s)).ToList();
        if (missing.Count > 0)
        {
            throw new RefusedException($"No such fragment to cite: {string.Join(", ", missing.Select(m => $"#{m}"))}.");
        }

        var subj = await ConceptAsync(subject, kind);
        var obj = @object is null ? null : await ConceptAsync(@object);
        if (obj?.Id == subj.Id)
        {
            obj = null;
        }

        FactRow? replaced = null;
        if (supersedes is { } old)
        {
            replaced = await concepts.GetFactAsync(old) ?? throw new RefusedException($"There is no fact #{old} to supersede.");
            if (replaced.SupersededById is not null || replaced.Status is not ("active" or "proposed"))
            {
                throw new RefusedException($"Fact #{old} no longer stands ({replaced.Status}{(replaced.SupersededById is { } by ? $", superseded by #{by}" : "")}).");
            }
        }

        var normal = Normal(text);
        var known = await concepts.FactsOfAsync(subj.Id, history: true);
        // An exact copy of a fact that stands (accepted, or still a proposal) went in as a new proposal (Arden, 2026-11-10).
        var standing = known.FirstOrDefault(f => f.SupersededById is null && f.Status is "active" or "proposed"
            && f.SubjectId == subj.Id && f.ObjectId == obj?.Id && Normal(f.Text) == normal);
        if (standing is not null && supersedes is null)
        {
            return (standing, null, true);
        }

        var rejected = known
            .FirstOrDefault(f => f.Status == "rejected" && Normal(f.Text) == normal && f.SubjectId == subj.Id);
        if (rejected is not null)
        {
            throw new RefusedException($"This was rejected before as #{rejected.Id} ({rejected.Resolution ?? "no reason given"}); it is not proposed again. `concept reopen {rejected.Id} --reason` makes it a proposal again.");
        }

        string? Why(ConceptRow c, string role) => !AboutWho(c) ? null
            : c.Confirmed ? $"its {role} {c.Name} is a {c.Kind}" : $"its {role} {c.Name} has no confirmed kind, so counts as a person (`concept kind`)";
        var whys = new[] { Why(subj, "subject"), obj is null ? null : Why(obj, "object"),
            types.Where(t => WhoSources.Contains(t.Value)).Select(t => $"#{t.Key} is {(ContextFragmentType)t.Value}").FirstOrDefault() is { } w ? $"its source {w}" : null }
            .Where(x => x is not null).ToList();
        var proposal = whys.Count > 0;
        var id = await concepts.AddFactAsync(subj.Id, obj?.Id, text, isState, asOf, extractedBy ?? "by hand",
            sources, proposal ? "proposed" : "active", replaced?.Id);
        if (!proposal && replaced is not null)
        {
            await concepts.SupersedeFactAsync(replaced.Id, id, $"superseded by #{id}");
        }

        return ((await concepts.GetFactAsync(id))!, proposal ? string.Join("; ", whys) : null, false);
    }

    /// <summary>The self accepts a proposal: it stands, and replaces the fact it names, if any.</summary>
    public async Task<FactRow> AcceptFactAsync(long id, string? reason)
    {
        var fact = await StandingFactAsync(id);
        if (fact.Status != "proposed")
        {
            throw new RefusedException($"Fact #{id} is {fact.Status}, not a proposal.");
        }

        await concepts.SetFactStatusAsync(id, "active", $"accepted {Today()}{(reason is null ? "" : $": {reason}")}");
        if (fact.Supersedes is { } old && await concepts.GetFactAsync(old) is { SupersededById: null })
        {
            await concepts.SupersedeFactAsync(old, id, $"superseded by #{id}");
        }

        await concepts.ResolveFlagsAsync(id);
        return (await concepts.GetFactAsync(id))!;
    }

    /// <summary>The self rejects a fact (a proposal, or an active one it finds wrong): kept, with the reason, never offered again.</summary>
    public async Task<FactRow> RejectFactAsync(long id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new UsageException("Say why (--reason): a rejection is kept so the extractor doesn't offer it again, and the reason is what it learns from.");
        }

        _ = await StandingFactAsync(id);
        await concepts.SetFactStatusAsync(id, "rejected", $"rejected {Today()}: {reason}");
        await concepts.ResolveFlagsAsync(id);
        return (await concepts.GetFactAsync(id))!;
    }

    /// <summary>The self takes back a rejection: the fact is a proposal again, for a later `concept review`. Its rejection stays in its history.</summary>
    public async Task<FactRow> ReopenFactAsync(long id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new UsageException("Say why (--reason): what changed since it was rejected.");
        }

        var fact = await concepts.GetFactAsync(id) ?? throw new RefusedException($"There is no fact #{id}.");
        if (fact.Status != "rejected")
        {
            throw new RefusedException($"Fact #{id} is {fact.Status}, not rejected: there is nothing to reopen.");
        }

        await concepts.SetFactStatusAsync(id, "proposed", $"{fact.Resolution}; reopened {Today()}: {reason.Trim()}");
        return (await concepts.GetFactAsync(id))!;
    }

    /// <summary>Anyone may flag a fact; the flag waits in the self's review.</summary>
    public async Task<long> FlagFactAsync(long id, string by, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new UsageException("Say what may be wrong (--reason).");
        }

        _ = await StandingFactAsync(id);
        return await concepts.FlagAsync(id, string.IsNullOrWhiteSpace(by) ? "unknown" : by.Trim(), reason.Trim());
    }

    /// <summary>The self confirms what a concept is; until then it counts as a person.</summary>
    public async Task<ConceptRow> ConfirmKindAsync(string name, string kind)
    {
        var c = await concepts.FindAsync(name) ?? throw new RefusedException($"There is no concept '{name}'.");
        await concepts.SetKindAsync(c.Id, KindOrRefuse(kind));
        return (await concepts.GetAsync(c.Id))!;
    }

    public async Task<ConceptRow> AddAliasAsync(string name, string alias, string? note)
    {
        var c = await concepts.FindAsync(name) ?? throw new RefusedException($"There is no concept '{name}'.");
        if (await concepts.FindAsync(alias) is { } taken)
        {
            throw new RefusedException(taken.Id == c.Id ? $"'{alias}' already names {c.Name}." : $"'{alias}' already names {taken.Name}.");
        }

        await concepts.AddAliasAsync(c.Id, alias, note);
        return c;
    }

    private async Task<FactRow> StandingFactAsync(long id)
    {
        var fact = await concepts.GetFactAsync(id) ?? throw new RefusedException($"There is no fact #{id}.");
        if (fact.SupersededById is { } by)
        {
            throw new RefusedException($"Fact #{id} was superseded by #{by}.");
        }

        if (fact.Status is "rejected" or "pending")
        {
            throw new RefusedException($"Fact #{id} is {fact.Status}.");
        }

        return fact;
    }

    /// <summary>A concept with its standing facts (and, with <paramref name="history"/>, what they replaced and what was rejected).</summary>
    public async Task<string> ConceptShowAsync(string name, bool history)
    {
        var c = await concepts.FindAsync(name) ?? throw new RefusedException($"There is no concept '{name}'.");
        var aliases = await concepts.AliasesAsync(c.Id);
        var facts = await concepts.FactsOfAsync(c.Id, history);
        var sources = await concepts.SourcesAsync(facts.Select(f => f.Id).ToList());
        var names = await NamesAsync(facts);
        var sb = new StringBuilder();
        sb.Append($"{c.Name} ({(c.Confirmed ? c.Kind : c.KindSaid ? $"{c.Kind}, kind unconfirmed: counts as a person" : "no kind said: counts as a person")})");
        if (aliases.Count > 0)
        {
            sb.Append(" aka " + string.Join(", ", aliases.Select(a => a.Note is null ? a.Alias : $"{a.Alias} ({a.Note})")));
        }

        if (c.Summary is not null)
        {
            sb.Append($"\n  {c.Summary}");
        }

        foreach (var f in facts)
        {
            sb.Append("\n  " + RenderFact(f, c.Id, names, sources.GetValueOrDefault(f.Id) ?? []));
        }

        if (facts.Count == 0)
        {
            sb.Append("\n  (no facts yet)");
        }

        return sb.ToString();
    }

    /// <summary>What waits for the self: proposals, flags, and concepts whose kind is unconfirmed.</summary>
    public async Task<string> ConceptReviewAsync()
    {
        var proposed = await concepts.FactsByStatusAsync("proposed");
        var flags = await concepts.OpenFlagsAsync();
        var unconfirmed = await concepts.UnconfirmedAsync();
        var flagged = new List<FactRow>();
        foreach (var id in flags.Select(f => f.FactId).Distinct())
        {
            if (await concepts.GetFactAsync(id) is { } f)
            {
                flagged.Add(f);
            }
        }

        var all = proposed.Concat(flagged).ToList();
        var sources = await concepts.SourcesAsync(all.Select(f => f.Id).Distinct().ToList());
        var names = await NamesAsync(all);
        var sb = new StringBuilder();
        if (proposed.Count > 0)
        {
            sb.Append($"Proposed ({proposed.Count}): `concept accept <id>` or `concept reject <id> --reason` (`concept reopen <id>` takes a rejection back)");
            foreach (var f in proposed)
            {
                sb.Append("\n  " + RenderFact(f, null, names, sources.GetValueOrDefault(f.Id) ?? []));
            }
        }

        if (flags.Count > 0)
        {
            sb.Append((sb.Length > 0 ? "\n\n" : "") + $"Flagged ({flags.Count}):");
            foreach (var flag in flags)
            {
                var f = flagged.First(x => x.Id == flag.FactId);
                sb.Append($"\n  {RenderFact(f, null, names, sources.GetValueOrDefault(f.Id) ?? [])}\n    flagged by {flag.FlaggedBy}, {flag.CreatedUtc[..10]}: {flag.Reason}");
            }
        }

        if (unconfirmed.Count > 0)
        {
            sb.Append((sb.Length > 0 ? "\n\n" : "") + $"Kind unconfirmed ({unconfirmed.Count}), counted as people until `concept kind <name> <{string.Join("|", ConceptKinds)}>`:\n  "
                + string.Join(", ", unconfirmed.Select(u => u.KindSaid ? $"{u.Name} (said {u.Kind})" : $"{u.Name} (no kind said)")));
        }

        return sb.Length > 0 ? sb.ToString() : "Nothing waits: no proposals, flags or unconfirmed kinds.";
    }

    private async Task<Dictionary<long, string>> NamesAsync(IEnumerable<FactRow> facts)
    {
        var names = new Dictionary<long, string>();
        foreach (var id in facts.SelectMany(f => f.ObjectId is { } o ? new[] { f.SubjectId, o } : [f.SubjectId]).Distinct())
        {
            if (await concepts.GetAsync(id) is { } c)
            {
                names[id] = c.Name;
            }
        }

        return names;
    }

    /// <summary>
    /// One fact as a line: <c>#12 [Ember →] John's daughter [#7153, #14625] (proposed)</c>. A state fact always shows
    /// when it was known, so a stale belief looks stale (Arden); an event shows when it happened when the text says.
    /// </summary>
    public static string RenderFact(FactRow f, long? about, IReadOnlyDictionary<long, string> names, IReadOnlyList<long> sources)
    {
        var who = names.GetValueOrDefault(f.SubjectId, $"concept {f.SubjectId}");
        var link = f.ObjectId is { } o ? $"{who} → {names.GetValueOrDefault(o, $"concept {o}")}: " : about == f.SubjectId ? "" : $"{who}: ";
        var dates = new List<string>();
        if (f.State)
        {
            dates.Add($"a state, known as of {Day(f.RecordedAtUtc)}");
        }

        if (f.AsOfUtc is not null)
        {
            dates.Add($"happened {Day(f.AsOfUtc)}");
        }

        var status = f.SupersededById is { } by ? $"superseded by #{by}" : f.Status == "active" ? null : f.Status;
        var tail = string.Join("; ", dates.Concat(status is null ? [] : [status]).Concat(f.Supersedes is { } s && f.Status == "proposed" ? [$"would replace #{s}"] : []));
        return $"#{f.Id} {link}{f.Text} [{string.Join(", ", sources.Select(s => $"#{s}"))}]" + (tail.Length > 0 ? $" ({tail})" : "");
    }

    /// <summary>One fact as <see cref="RenderFact"/> shows it, its names and sources looked up.</summary>
    public async Task<string> RenderFactAsync(FactRow f) =>
        RenderFact(f, null, await NamesAsync([f]), (await concepts.SourcesAsync([f.Id])).GetValueOrDefault(f.Id) ?? []);

    private static string Day(string? utc) => string.IsNullOrEmpty(utc) ? "?" : utc.Length >= 10 ? utc[..10] : utc;

    private static string Today() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

    private static string Normal(string text) => string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
}
