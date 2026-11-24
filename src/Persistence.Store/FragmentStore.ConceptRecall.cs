using Persistence.Data.Repositories;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Persistence.Store;

/// <summary>
/// Concept-first recall, v0 (John's ask 2026-11-15, room 1571675418992582787; the plan in 1571675638690091049): the
/// concepts a message names come first, each with its current facts and the fragments behind them, ahead of ordinary
/// recall. Matching is exact: a concept's name or one of its aliases as a whole word sequence, never fuzzy or by
/// embedding (concept-store.md "Projection in context"). It reads and never writes.
/// </summary>
internal sealed partial class FragmentStore
{
    /// <summary>Output B's concept section bound (concept-usefulness-contract.md §2), heading and omission line included.</summary>
    public const int ConceptBudget = 3300;

    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    private static string[] WordSequence(string text) =>
        WordPattern.Matches(text.ToLowerInvariant()).Select(m => m.Value).ToArray();

    /// <summary>How many times <paramref name="label"/> occurs in <paramref name="words"/> as a whole word sequence.</summary>
    public static int Occurrences(string[] words, string label)
    {
        var l = WordSequence(label);
        if (l.Length == 0)
        {
            return 0;
        }

        var n = 0;
        for (var i = 0; i + l.Length <= words.Length; i++)
        {
            var j = 0;
            while (j < l.Length && words[i + j] == l[j])
            {
                j++;
            }

            if (j == l.Length)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// A concept the text names, with how often (by any of its labels) and its standing facts, newest first.
    /// <paramref name="Via"/> is the ambiguous name it was reached through (one of the concepts that name means), and
    /// <paramref name="Others"/> the other concepts it may mean; for the merged concept still named that way, Via is its
    /// own name and Others every referent.
    /// </summary>
    public sealed record Touched(ConceptRow Concept, int Spans, IReadOnlyList<string> Aliases, IReadOnlyList<FactRow> Facts,
        IReadOnlyDictionary<long, IReadOnlyList<long>> Sources, string? Via = null, IReadOnlyList<string>? Others = null, int Cues = 0);

    /// <summary>One concept an ambiguous name may mean, and the words that tell it apart (the hook's ambiguous.json, #60).</summary>
    public sealed record Referent(string Name, string? Cues);

    /// <summary>
    /// <c>{"Ember": [{"name": "Ember (peer)", "cues": "..."}, ...]}</c>, as the hook's state/concepts/ambiguous.json holds
    /// it. Only a name with two or more referents counts, as concepts.load_ambiguous reads it.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<Referent>> ParseAmbiguous(string? json)
    {
        var result = new Dictionary<string, IReadOnlyList<Referent>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new UsageException($"--ambiguous is not JSON: {e.Message}");
        }

        using var _ = doc;
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new UsageException("--ambiguous takes a JSON object: {\"<name>\": [{\"name\": ..., \"cues\": ...}, ...]}.");
        }

        foreach (var label in doc.RootElement.EnumerateObject())
        {
            if (label.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var refs = label.Value.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString()!.Trim().Length > 0)
                .Select(r => new Referent(r.GetProperty("name").GetString()!.Trim(),
                    r.TryGetProperty("cues", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null))
                .ToList();
            if (refs.Count >= 2)
            {
                result[label.Name.Trim()] = refs;
            }
        }

        return result;
    }

    /// <summary>
    /// The concepts <paramref name="text"/> names, ranked by matched spans, then by when their newest standing fact
    /// was known, then by id, so the order is total (concept-usefulness-contract.md §2).
    /// A name listed in <paramref name="ambiguous"/> means more than one concept (Ember the peer self; John's daughter
    /// Ember: #60), and ordinary text says only the name, never "Ember (peer)". So each of its referents that exists is
    /// reached with the name's spans (the name itself, and any alias the merged concept of that name still holds), ranked
    /// among themselves by how many of their cue words the text uses: model-free, and both shown. The merged concept of
    /// that name comes after them, and only while it still holds a standing fact.
    /// </summary>
    public async Task<IReadOnlyList<Touched>> ConceptsTouchedAsync(string text, IReadOnlyDictionary<string, IReadOnlyList<Referent>>? ambiguous = null,
        DateTimeOffset? asOf = null)
    {
        var located = text;
        text = WithoutLocations(text);
        var words = WordSequence(text);
        var spans = new Dictionary<long, int>();
        foreach (var (id, label) in await concepts.LabelsAsync())
        {
            var n = LabelOccurrences(located, text, words, label);
            if (n > 0)
            {
                spans[id] = spans.GetValueOrDefault(id) + n;
            }
        }

        var via = new Dictionary<long, (string Label, IReadOnlyList<string> Others, int Cues)>();
        var mergedOf = new Dictionary<long, (string Label, IReadOnlyList<string> Referents)>();
        foreach (var (label, refs) in ambiguous ?? new Dictionary<string, IReadOnlyList<Referent>>())
        {
            var merged = await concepts.FindAsync(label) is { } m && string.Equals(m.Name, label, StringComparison.OrdinalIgnoreCase) ? m : null;
            var n = merged is not null ? spans.GetValueOrDefault(merged.Id) : Occurrences(words, label);
            if (n == 0)
            {
                continue;
            }

            var named = WordSequence(label).ToHashSet();
            var said = words.Where(w => w.Length > 3 && !named.Contains(w)).ToHashSet();
            foreach (var r in refs)
            {
                if (await concepts.FindAsync(r.Name) is not { } c || !string.Equals(c.Name, r.Name, StringComparison.OrdinalIgnoreCase) || c.Id == merged?.Id)
                {
                    continue;
                }

                spans[c.Id] = spans.GetValueOrDefault(c.Id) + n;
                via[c.Id] = (label, refs.Where(o => o != r).Select(o => o.Name).ToList(), WordSequence(r.Cues ?? "").Where(said.Contains).Distinct().Count());
            }

            if (merged is not null)
            {
                mergedOf[merged.Id] = (merged.Name, refs.Select(r => r.Name).ToList());
            }
        }

        var touched = new List<Touched>();
        foreach (var (id, n) in spans)
        {
            if (await concepts.GetAsync(id) is not { } c)
            {
                continue;
            }

            // Accepted facts before proposals, each newest-known first: the self's word outranks the extractor's.
            var facts = StandingAsOf(await concepts.FactsOfAsync(id, history: asOf is not null), asOf)
                .OrderBy(f => f.Status == "active" ? 0 : 1).ThenByDescending(f => f.RecordedAtUtc).ThenByDescending(f => f.Id).ToList();
            var aliases = (await concepts.AliasesAsync(id)).Select(a => a.Alias).ToList();
            var sources = await concepts.SourcesAsync(facts.Select(f => f.Id).ToList());
            if (asOf is not null && facts.Count == 0)
            {
                continue;   // as of then, nothing was known of it: a replay doesn't show a concept the store hadn't filled yet
            }

            if (mergedOf.TryGetValue(id, out var m))
            {
                // Its facts are the ones split left on it (unsure, or about both); with none, its name says nothing.
                if (facts.Count > 0)
                {
                    touched.Add(new Touched(c, n, aliases, facts, sources, m.Label, m.Referents));
                }
            }
            else if (via.TryGetValue(id, out var v))
            {
                touched.Add(new Touched(c, n, aliases, facts, sources, v.Label, v.Others, v.Cues));
            }
            else
            {
                touched.Add(new Touched(c, n, aliases, facts, sources));
            }
        }

        return touched
            .OrderByDescending(t => t.Spans)
            .ThenBy(t => mergedOf.ContainsKey(t.Concept.Id) ? 1 : 0)
            .ThenByDescending(t => t.Cues)
            .ThenByDescending(t => t.Facts.Select(f => f.RecordedAtUtc).Max() ?? "")
            .ThenBy(t => t.Concept.Id)
            .ToList();
    }

    private static readonly Regex Url = new(@"\b[a-z][a-z0-9+.-]*://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Two or more segments, starting at a word's edge: /home/ubuntu/.config, ~/work/x, ./a/b, C:\Users\x. "/loop" and
    // "11/23" are left alone (one segment; a digit before the slash).
    private static readonly Regex FilePath = new(@"(?<![\w/\\])(?:~|\.\.?|[A-Za-z]:)?[/\\][\w.@+-]+(?:[/\\][\w.@+-]*)+", RegexOptions.Compiled);

    // The room's attachment note, "(audio: /home/ubuntu/.config/persistence-room/state/attachments/.../voice-message.ogg)".
    private static readonly Regex Attachment = new(@"\(\s*[a-z]+:\s*(?:[a-z][a-z0-9+.-]*://|~|\.{0,2}/|[A-Za-z]:\\)[^)\n]*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// <paramref name="text"/> without attachment notes, URLs and file paths: a name inside one is where a file lives,
    /// not what the message is about. Every voice message's "(audio: /home/ubuntu/.config/persistence-room/...)" named
    /// ubuntu, home and Persistence, and put 9 off-topic deploy and reboot facts under John's "hello again" (room
    /// 1574775055601107046, cause 2).
    /// </summary>
    public static string WithoutLocations(string text) => FilePath.Replace(Url.Replace(Attachment.Replace(text, " "), " "), " ");

    private static readonly Regex Identifier = new(@"^#\d+$", RegexOptions.Compiled);

    /// <summary>
    /// How often a label is named. A label that is itself a location (a path, a URL) is found in the text as written; an
    /// identifier like "#81" only as "#81", never as the bare number "81" (a fact count, a time, a line); any other label
    /// as a whole word sequence of the text without its locations.
    /// </summary>
    private static int LabelOccurrences(string located, string text, string[] words, string label)
    {
        var l = label.Trim();
        if (Identifier.IsMatch(l))
        {
            return Regex.Matches(text, $@"(?<![\w#]){Regex.Escape(l)}(?!\d)").Count;
        }

        if (WithoutLocations(l) != l)
        {
            return Regex.Matches(located, $@"(?<![\w/]){Regex.Escape(l)}(?![\w/])", RegexOptions.IgnoreCase).Count;
        }

        return Occurrences(words, l);
    }

    /// <summary>
    /// The facts that stood <paramref name="asOf"/>: written before it, and not yet replaced by a fact written before it
    /// (a replacement this list doesn't hold is taken as earlier). Accepted and rejected decisions carry no date, so a
    /// fact counts with the status it has now. Without a time, the standing facts as given.
    /// </summary>
    private static IReadOnlyList<FactRow> StandingAsOf(IReadOnlyList<FactRow> facts, DateTimeOffset? asOf)
    {
        if (asOf is not { } at)
        {
            return facts;
        }

        var written = facts.ToDictionary(f => f.Id, f => DateTimeOffset.Parse(f.CreatedUtc, System.Globalization.CultureInfo.InvariantCulture));
        return facts.Where(f => written[f.Id] < at && (f.Status is "active" or "proposed")
                && (f.SupersededById is not { } by || (written.TryGetValue(by, out var w) && w >= at)))
            .ToList();
    }

    /// <summary>
    /// The concept section of a turn's recall: up to <paramref name="maxConcepts"/> concepts at
    /// <paramref name="factsEach"/> facts each, every fact with the fragment ids behind it and a snippet of the first,
    /// within <paramref name="budget"/> characters. A proposal is labelled as one: the extractor's reading of a
    /// fragment, not something the self has accepted (Ember's first danger: extraction shown as truth). Empty when the
    /// text names no concept.
    /// </summary>
    public async Task<string> ConceptBlockAsync(string text, int maxConcepts = 5, int factsEach = 3, int budget = ConceptBudget, int snippet = 140,
        IReadOnlyDictionary<string, IReadOnlyList<Referent>>? ambiguous = null, DateTimeOffset? asOf = null)
    {
        const string heading = "Concepts this message names (concept-first recall v0: current facts with the fragments behind them; "
            + "'proposed' is the extractor's reading, not yet accepted):";
        return await ConceptSectionAsync(await ConceptsTouchedAsync(text, ambiguous, asOf), heading, t => Math.Min(factsEach, t.Facts.Count),
            maxConcepts, budget, snippet);
    }

    /// <summary>
    /// <paramref name="touched"/> under <paramref name="heading"/>: up to <paramref name="maxConcepts"/> of them, each
    /// with at most <paramref name="factsOf"/> of its facts (fewer when the budget is short), and a line naming what was
    /// cut. Empty when there is nothing to show.
    /// </summary>
    private async Task<string> ConceptSectionAsync(IReadOnlyList<Touched> touched, string heading, Func<Touched, int> factsOf, int maxConcepts, int budget, int snippet)
    {
        if (touched.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder(heading);
        var omitted = new List<string>();
        var names = new Dictionary<long, string>();
        foreach (var t in touched)
        {
            names[t.Concept.Id] = t.Concept.Name;
        }

        // Room kept for the omission line while later concepts may still be cut: its count and a few names.
        const int omissionRoom = 120;
        for (var i = 0; i < touched.Count; i++)
        {
            var t = touched[i];
            if (i >= maxConcepts)
            {
                omitted.Add(t.Concept.Name);
                continue;
            }

            foreach (var id in t.Facts.SelectMany(f => f.ObjectId is { } o ? new[] { f.SubjectId, o } : [f.SubjectId]).Distinct().ToList())
            {
                if (!names.ContainsKey(id) && await concepts.GetAsync(id) is { } other)
                {
                    names[id] = other.Name;
                }
            }

            // Fewer facts if the whole concept doesn't fit; its state line alone if none do; else it is omitted.
            var reserve = i < touched.Count - 1 || omitted.Count > 0 ? omissionRoom : 0;
            string? chunk = null;
            for (var k = factsOf(t); k >= 0 && chunk is null; k--)
            {
                var candidate = await ConceptChunkAsync(t, k, names, snippet);
                if (sb.Length + candidate.Length + reserve <= budget)
                {
                    chunk = candidate;
                }
            }

            if (chunk is null)
            {
                omitted.Add(t.Concept.Name);
                continue;
            }

            sb.Append(chunk);
        }

        sb.Append(OmissionLine(omitted, budget - sb.Length));
        return sb.ToString();
    }

    /// <summary><c>N omitted: a, b</c>, as many names as fit in <paramref name="room"/>; the count always. Nothing when none were.</summary>
    private static string OmissionLine(IReadOnlyList<string> omitted, int room)
    {
        if (omitted.Count == 0)
        {
            return "";
        }

        var line = $"\n{omitted.Count} omitted";
        for (var n = omitted.Count; n > 0; n--)
        {
            var named = $"{line}: {string.Join(", ", omitted.Take(n).Select(OneLine))}{(n < omitted.Count ? ", …" : "")}";
            if (named.Length <= room)
            {
                return named;
            }
        }

        return line;
    }

    private async Task<string> ConceptChunkAsync(Touched t, int facts, IReadOnlyDictionary<long, string> names, int snippet)
    {
        var flags = await OpenFlagsByFactAsync();
        var c = t.Concept;
        var accepted = t.Facts.Count(f => f.Status == "active");
        var proposed = t.Facts.Count(f => f.Status == "proposed");
        var fragments = t.Sources.Values.SelectMany(s => s).Distinct().Count();
        var kind = c.Confirmed ? c.Kind : "kind unconfirmed";
        var ambiguity = t.Via is null || t.Others is null ? ""
            : string.Equals(t.Via, c.Name, StringComparison.OrdinalIgnoreCase)
                ? $"; names more than one: {string.Join(", ", t.Others.Select(OneLine))}"
                : $"; '{OneLine(t.Via)}' may mean this or {string.Join(" or ", t.Others.Select(OneLine))}";
        var sb = new StringBuilder($"\n\n{OneLine(c.Name)} ({kind}{(t.Aliases.Count > 0 ? "; aka " + string.Join(", ", t.Aliases.Select(OneLine)) : "")}{ambiguity}): ");
        sb.Append(t.Facts.Count == 0 ? "no facts yet" : $"{accepted} accepted, {proposed} proposed, from {fragments} fragment{(fragments == 1 ? "" : "s")}");
        foreach (var f in t.Facts.Take(facts))
        {
            var sources = t.Sources.GetValueOrDefault(f.Id) ?? [];
            var open = flags.GetValueOrDefault(f.Id) ?? [];
            var line = RenderFact(f, c.Id, names, sources, open);
            sb.Append("\n  " + (line.Length > 200 ? line[..199] + "…" : line) + FlagLines(open));
            // The words in the fragment that back this fact, not its opening line: a fragment's first line is usually
            // its date and who asked (seen on the first live run, 2026-11-16).
            if (sources.Count > 0 && await EvidenceAsync(sources[0], f.Text, snippet) is { } evidence)
            {
                sb.Append($"\n    #{sources[0]}: \"{evidence}\"");
            }
        }

        if (t.Facts.Count > facts)
        {
            sb.Append($"\n  {t.Facts.Count - facts} more: `concept show {OneLine(c.Name)}`");
        }

        return sb.ToString();
    }

    /// <summary>The sentence of a fragment that shares the most words with <paramref name="fact"/>, on one line and
    /// at most <paramref name="snippet"/> characters, or null if the fragment is gone.</summary>
    private async Task<string?> EvidenceAsync(long fragmentId, string fact, int snippet)
    {
        try
        {
            var f = await GetAsync(fragmentId);
            return f.IsDeleted ? null : Evidence(f.Content, fact, snippet);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static readonly Regex SentenceEnd = new(@"(?<=[.;!?])\s+|\n+|(?<=:)\s+(?=[-•*]|\d+\.)", RegexOptions.Compiled);

    /// <summary>
    /// The sentence of <paramref name="content"/> sharing the most words (over three letters) with
    /// <paramref name="fact"/>, the earliest on a tie; cut to <paramref name="snippet"/> characters around its first
    /// shared word when longer.
    /// </summary>
    public static string Evidence(string content, string fact, int snippet)
    {
        var wanted = WordSequence(fact).Where(w => w.Length > 3).ToHashSet();
        var best = SentenceEnd.Split(content)
            .Select(x => string.Join(' ', x.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(x => x.Length > 0)
            .Select((x, i) => (Text: x, Score: WordSequence(x).Where(wanted.Contains).Distinct().Count(), Order: i))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Order)
            .FirstOrDefault().Text ?? "";
        if (best.Length <= snippet)
        {
            return best;
        }

        var first = WordPattern.Matches(best).FirstOrDefault(m => wanted.Contains(m.Value.ToLowerInvariant()))?.Index ?? 0;
        var start = Math.Max(0, Math.Min(first - snippet / 4, best.Length - (snippet - 2)));
        var cut = best.Substring(start, Math.Min(snippet - 2, best.Length - start));
        return (start > 0 ? "…" : "") + cut + (start + cut.Length < best.Length ? "…" : "");
    }

    /// <summary>Every concept with its aliases, as JSON for the extractor's KNOWN CONCEPTS.</summary>
    public async Task<string> ConceptListJsonAsync()
    {
        var all = new List<object>();
        foreach (var c in await concepts.AllAsync())
        {
            all.Add(new
            {
                id = c.Id, name = c.Name, kind = c.Kind, confirmed = c.Confirmed,
                aliases = (await concepts.AliasesAsync(c.Id)).Select(a => a.Alias).ToList(),
            });
        }

        return JsonSerializer.Serialize(all);
    }

    /// <summary>The concepts a text names and their standing facts, as JSON for the extractor's CURRENT FACTS.</summary>
    public async Task<string> ConceptsTouchedJsonAsync(string text, IReadOnlyDictionary<string, IReadOnlyList<Referent>>? ambiguous = null)
    {
        var names = new Dictionary<long, string>();
        async Task<string> Name(long id) =>
            names.TryGetValue(id, out var n) ? n : names[id] = (await concepts.GetAsync(id))?.Name ?? $"concept {id}";
        var result = new List<object>();
        foreach (var t in await ConceptsTouchedAsync(text, ambiguous))
        {
            var facts = new List<object>();
            foreach (var f in t.Facts)
            {
                facts.Add(new
                {
                    id = f.Id, subject = await Name(f.SubjectId), @object = f.ObjectId is { } o ? await Name(o) : null,
                    text = f.Text, status = f.Status, is_state = f.State, as_of = f.AsOfUtc,
                    sources = t.Sources.GetValueOrDefault(f.Id) ?? [],
                });
            }

            result.Add(new { id = t.Concept.Id, name = t.Concept.Name, kind = t.Concept.Kind, confirmed = t.Concept.Confirmed, spans = t.Spans, aliases = t.Aliases, via = t.Via, facts });
        }

        return JsonSerializer.Serialize(result);
    }
}
