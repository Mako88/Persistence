using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using System.Text;
using System.Text.RegularExpressions;

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
    /// then nothing was added and the fact returned is the one standing. A <paramref name="flag"/> is written with the
    /// fact, by whoever added it, in the same transaction (the extractor's 'referent unsure', Arden's gate on #70); on a
    /// standing proposal it flags that one, once per reason. A fact the self has accepted is left alone.</summary>
    public async Task<(FactRow Fact, string? WhyProposed, bool AlreadyStood)> AddFactWhyAsync(string subject, string text, IReadOnlyCollection<long> sources, string? @object = null,
        bool isState = false, string? asOf = null, string? extractedBy = null, long? supersedes = null, string? kind = null, string? flag = null)
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
            if (!string.IsNullOrWhiteSpace(flag) && standing.Status == "proposed"
                && !(await concepts.OpenFlagsAsync()).Any(x => x.FactId == standing.Id && x.Reason == flag.Trim()))
            {
                await concepts.FlagAsync(standing.Id, extractedBy ?? "by hand", flag.Trim());
            }

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
            types.Where(t => WhoSources.Contains(t.Value)).Select(t => $"#{t.Key} is {(ContextFragmentType)t.Value}").FirstOrDefault() is { } w ? $"its source {w}" : null,
            // The extractor proposes and the self accepts (concept-store.md), whatever the fact is about.
            FromExtractor(extractedBy) ? "the extractor proposes; only the self accepts" : null }
            .Where(x => x is not null).ToList();
        var proposal = whys.Count > 0;
        var id = await concepts.AddFactAsync(subj.Id, obj?.Id, text, isState, asOf, extractedBy ?? "by hand",
            sources, proposal ? "proposed" : "active", replaced?.Id, flag);
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

    /// <summary>
    /// Moves <paramref name="alias"/> from the concept that holds it to <paramref name="name"/>'s, with why: 'Emma' was
    /// put on the merged Ember, and is a mishearing of John's daughter's name (#60). An alias is unique, so it can't be
    /// kept on both; the move is appended to its note instead, as a fact's resolution keeps its history.
    /// </summary>
    public async Task<(ConceptRow To, ConceptRow From)> MoveAliasAsync(string name, string alias, string reason)
    {
        var to = await concepts.FindAsync(name) ?? throw new RefusedException($"There is no concept '{name}'.");
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new UsageException("concept alias --move needs --reason <why>.");
        }

        var from = await concepts.FindAsync(alias);
        if (from is null || string.Equals(from.Name, alias.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new RefusedException(from is null ? $"'{alias}' is not an alias of any concept: `concept alias {name} {alias}` adds it."
                : $"'{alias}' is a concept's own name, not an alias; only an alias moves.");
        }

        if (from.Id == to.Id)
        {
            throw new RefusedException($"'{alias}' already names {to.Name}.");
        }

        var note = (await concepts.AliasesAsync(from.Id)).First(a => string.Equals(a.Alias, alias.Trim(), StringComparison.OrdinalIgnoreCase)).Note;
        var moved = $"moved from {OneLine(from.Name)} {Today()}: {OneLine(reason.Trim())}";
        await concepts.MoveAliasAsync(from.Id, to.Id, alias, string.IsNullOrWhiteSpace(note) ? moved : $"{note}; {moved}");
        return (to, from);
    }

    /// <summary>Whether a fact was written by the automatic extractor (<c>--by extractor:...</c>), never by the self.</summary>
    public static bool FromExtractor(string? extractedBy) =>
        extractedBy is not null && extractedBy.StartsWith("extractor", StringComparison.OrdinalIgnoreCase);

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
        sb.Append($"{OneLine(c.Name)} ({(c.Confirmed ? c.Kind : c.KindSaid ? $"{c.Kind}, kind unconfirmed: counts as a person" : "no kind said: counts as a person")})");
        if (aliases.Count > 0)
        {
            sb.Append(" aka " + string.Join(", ", aliases.Select(a => OneLine(a.Note is null ? a.Alias : $"{a.Alias} ({a.Note})"))));
        }

        if (c.Summary is not null)
        {
            sb.Append($"\n  {OneLine(c.Summary)}");
        }

        var flags = await OpenFlagsByFactAsync();
        foreach (var f in facts)
        {
            var open = flags.GetValueOrDefault(f.Id) ?? [];
            sb.Append("\n  " + RenderFact(f, c.Id, names, sources.GetValueOrDefault(f.Id) ?? [], open) + FlagLines(open));
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
        var byFact = flags.GroupBy(x => x.FactId).ToDictionary(g => g.Key, g => (IReadOnlyList<FactFlagRow>)g.ToList());
        var sb = new StringBuilder();
        // Flags first: a flagged fact may be wrong in a way the extractor could tell (Arden's gate on #70). A flagged
        // proposal is listed once, under its flags, not again among the proposals.
        if (flags.Count > 0)
        {
            sb.Append($"Flagged ({flags.Count}): `concept accept <id>` or `concept reject <id> --reason` settles a fact and its flags");
            foreach (var f in flagged)
            {
                sb.Append($"\n  {RenderFact(f, null, names, sources.GetValueOrDefault(f.Id) ?? [], byFact[f.Id])}"
                    + string.Concat(byFact[f.Id].Select(flag => $"\n    flagged by {OneLine(flag.FlaggedBy)}, {flag.CreatedUtc[..10]}: {OneLine(flag.Reason)}")));
            }
        }

        var unflagged = proposed.Where(f => !byFact.ContainsKey(f.Id)).ToList();
        if (unflagged.Count > 0)
        {
            sb.Append((sb.Length > 0 ? "\n\n" : "") + $"Proposed ({unflagged.Count}): `concept accept <id>` or `concept reject <id> --reason` (`concept reopen <id>` takes a rejection back)");
            foreach (var f in unflagged)
            {
                sb.Append("\n  " + RenderFact(f, null, names, sources.GetValueOrDefault(f.Id) ?? []));
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

    private static readonly Regex Whitespace = new(@"[\s\u0085\u2028\u2029]+", RegexOptions.Compiled);

    /// <summary>
    /// <paramref name="text"/> with every run of whitespace, line breaks and Unicode line/paragraph separators included,
    /// as one space: a fact or a name is stored as given, and one with a line break could otherwise start a line of its
    /// own in a hook's context, shaped like a frame heading (Arden's F1 on #58, the #547 class).
    /// </summary>
    public static string OneLine(string text) => Whitespace.Replace(text, " ").Trim();

    /// <summary>
    /// One fact as a line: <c>#12 [Ember →] John's daughter [#7153, #14625] (proposed)</c>. A state fact always shows
    /// when it was known, so a stale belief looks stale (Arden); an event shows when it happened when the text says.
    /// </summary>
    public static string RenderFact(FactRow f, long? about, IReadOnlyDictionary<long, string> names, IReadOnlyList<long> sources,
        IReadOnlyList<FactFlagRow>? flags = null)
    {
        var who = OneLine(names.GetValueOrDefault(f.SubjectId, $"concept {f.SubjectId}"));
        var link = f.ObjectId is { } o ? $"{who} → {OneLine(names.GetValueOrDefault(o, $"concept {o}"))}: " : about == f.SubjectId ? "" : $"{who}: ";
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
        return $"#{f.Id} {FlagMark(flags)}{link}{OneLine(f.Text)} [{string.Join(", ", sources.Select(s => $"#{s}"))}]" + (tail.Length > 0 ? $" ({tail})" : "");
    }

    /// <summary><c>[flagged: referent unsure] </c>, right after the id so a line cut for length keeps it: the flag's
    /// kind is its reason up to the first colon, when that is short; <c>[flagged] </c> otherwise. Nothing when unflagged.</summary>
    private static string FlagMark(IReadOnlyList<FactFlagRow>? flags)
    {
        if (flags is not { Count: > 0 })
        {
            return "";
        }

        var kinds = flags.Select(x => OneLine(x.Reason)).Select(r => r.IndexOf(':') is var i and > 0 and <= 40 ? r[..i].Trim() : null)
            .OfType<string>().Distinct().ToList();
        return kinds.Count > 0 ? $"[flagged: {string.Join(", ", kinds)}] " : "[flagged] ";
    }

    /// <summary>Each open flag on its own indented line under the fact, its reason cut at 200 characters.</summary>
    internal static string FlagLines(IReadOnlyList<FactFlagRow> flags) =>
        string.Concat(flags.Select(x => $"\n    flagged by {OneLine(x.FlaggedBy)}: {Cut(OneLine(x.Reason), 200)}"));

    private static string Cut(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;

    /// <summary>The open flags, by the fact they are on.</summary>
    internal async Task<IReadOnlyDictionary<long, IReadOnlyList<FactFlagRow>>> OpenFlagsByFactAsync() =>
        (await concepts.OpenFlagsAsync()).GroupBy(x => x.FactId).ToDictionary(g => g.Key, g => (IReadOnlyList<FactFlagRow>)g.ToList());

    /// <summary>One fact as <see cref="RenderFact"/> shows it, its names, sources and open flags looked up.</summary>
    public async Task<string> RenderFactAsync(FactRow f) =>
        RenderFact(f, null, await NamesAsync([f]), (await concepts.SourcesAsync([f.Id])).GetValueOrDefault(f.Id) ?? [],
            (await OpenFlagsByFactAsync()).GetValueOrDefault(f.Id));

    private static string Day(string? utc) => string.IsNullOrEmpty(utc) ? "?" : utc.Length >= 10 ? utc[..10] : utc;

    private static string Today() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

    private static string Normal(string text) => string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
}
