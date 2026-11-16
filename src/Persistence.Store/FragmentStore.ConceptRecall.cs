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

    /// <summary>A concept the text names, with how often (by any of its labels) and its standing facts, newest first.</summary>
    public sealed record Touched(ConceptRow Concept, int Spans, IReadOnlyList<string> Aliases, IReadOnlyList<FactRow> Facts,
        IReadOnlyDictionary<long, IReadOnlyList<long>> Sources);

    /// <summary>
    /// The concepts <paramref name="text"/> names, ranked by matched spans, then by when their newest standing fact
    /// was known, then by id, so the order is total (concept-usefulness-contract.md §2).
    /// </summary>
    public async Task<IReadOnlyList<Touched>> ConceptsTouchedAsync(string text)
    {
        var words = WordSequence(text);
        var spans = new Dictionary<long, int>();
        foreach (var (id, label) in await concepts.LabelsAsync())
        {
            var n = Occurrences(words, label);
            if (n > 0)
            {
                spans[id] = spans.GetValueOrDefault(id) + n;
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
            var facts = (await concepts.FactsOfAsync(id, history: false))
                .OrderBy(f => f.Status == "active" ? 0 : 1).ThenByDescending(f => f.RecordedAtUtc).ThenByDescending(f => f.Id).ToList();
            touched.Add(new Touched(c, n, (await concepts.AliasesAsync(id)).Select(a => a.Alias).ToList(), facts,
                await concepts.SourcesAsync(facts.Select(f => f.Id).ToList())));
        }

        return touched
            .OrderByDescending(t => t.Spans)
            .ThenByDescending(t => t.Facts.Select(f => f.RecordedAtUtc).Max() ?? "")
            .ThenBy(t => t.Concept.Id)
            .ToList();
    }

    /// <summary>
    /// The concept section of a turn's recall: up to <paramref name="maxConcepts"/> concepts at
    /// <paramref name="factsEach"/> facts each, every fact with the fragment ids behind it and a snippet of the first,
    /// within <paramref name="budget"/> characters. A proposal is labelled as one: the extractor's reading of a
    /// fragment, not something the self has accepted (Ember's first danger: extraction shown as truth). Empty when the
    /// text names no concept.
    /// </summary>
    public async Task<string> ConceptBlockAsync(string text, int maxConcepts = 5, int factsEach = 3, int budget = ConceptBudget, int snippet = 140)
    {
        var touched = await ConceptsTouchedAsync(text);
        if (touched.Count == 0)
        {
            return "";
        }

        const string heading = "Concepts this message names (concept-first recall v0: current facts with the fragments behind them; "
            + "'proposed' is the extractor's reading, not yet accepted):";
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
            for (var k = Math.Min(factsEach, t.Facts.Count); k >= 0 && chunk is null; k--)
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
            var named = $"{line}: {string.Join(", ", omitted.Take(n))}{(n < omitted.Count ? ", …" : "")}";
            if (named.Length <= room)
            {
                return named;
            }
        }

        return line;
    }

    private async Task<string> ConceptChunkAsync(Touched t, int facts, IReadOnlyDictionary<long, string> names, int snippet)
    {
        var c = t.Concept;
        var accepted = t.Facts.Count(f => f.Status == "active");
        var proposed = t.Facts.Count(f => f.Status == "proposed");
        var fragments = t.Sources.Values.SelectMany(s => s).Distinct().Count();
        var kind = c.Confirmed ? c.Kind : "kind unconfirmed";
        var sb = new StringBuilder($"\n\n{c.Name} ({kind}{(t.Aliases.Count > 0 ? "; aka " + string.Join(", ", t.Aliases) : "")}): ");
        sb.Append(t.Facts.Count == 0 ? "no facts yet" : $"{accepted} accepted, {proposed} proposed, from {fragments} fragment{(fragments == 1 ? "" : "s")}");
        foreach (var f in t.Facts.Take(facts))
        {
            var sources = t.Sources.GetValueOrDefault(f.Id) ?? [];
            var line = RenderFact(f, c.Id, names, sources);
            sb.Append("\n  " + (line.Length > 200 ? line[..199] + "…" : line));
            // The words in the fragment that back this fact, not its opening line: a fragment's first line is usually
            // its date and who asked (seen on the first live run, 2026-11-16).
            if (sources.Count > 0 && await EvidenceAsync(sources[0], f.Text, snippet) is { } evidence)
            {
                sb.Append($"\n    #{sources[0]}: \"{evidence}\"");
            }
        }

        if (t.Facts.Count > facts)
        {
            sb.Append($"\n  {t.Facts.Count - facts} more: `concept show {c.Name}`");
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
    public async Task<string> ConceptsTouchedJsonAsync(string text)
    {
        var names = new Dictionary<long, string>();
        async Task<string> Name(long id) =>
            names.TryGetValue(id, out var n) ? n : names[id] = (await concepts.GetAsync(id))?.Name ?? $"concept {id}";
        var result = new List<object>();
        foreach (var t in await ConceptsTouchedAsync(text))
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

            result.Add(new { id = t.Concept.Id, name = t.Concept.Name, kind = t.Concept.Kind, confirmed = t.Concept.Confirmed, spans = t.Spans, aliases = t.Aliases, facts });
        }

        return JsonSerializer.Serialize(result);
    }
}
