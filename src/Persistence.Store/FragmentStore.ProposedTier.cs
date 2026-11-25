using Persistence.Data.Repositories;
using System.Globalization;
using System.Text;

namespace Persistence.Store;

/// <summary>
/// Concept recall with the extractor's proposals in their own tier (room 1574775057589342241, with Arden's asks in
/// 1574775678543470695). Measured first: of 81 proposed facts a wake showed, 55 were off-topic, and almost none is
/// accepted (John 0 of 124), so ranking proposals below accepted facts alone would change nothing. So:
/// <list type="bullet">
/// <item>Accepted facts keep the top, as before. Proposals go after ordinary recall (the hook places them).</item>
/// <item>At most <see cref="ProposedCap"/> proposals per wake across every concept, picked by closeness to the
/// message, not by being newest. Being named only makes a concept's facts candidates: John is named in nearly every
/// message, so his name lets his facts compete and wins them nothing.</item>
/// <item>The cap is a ceiling, not a quota: below <see cref="ProposedFloor"/> nothing shows ("hello again" shows none).</item>
/// <item>A state known more than <see cref="StaleAfterDays"/> days before shows only when its own subject is named
/// directly, and always with its date. A flagged proposal waits for the self.</item>
/// </list>
/// </summary>
internal sealed partial class FragmentStore
{
    /// <summary>How many proposals a wake shows at most: the block spent up to 15 lines on them and a third earned their place.</summary>
    public const int ProposedCap = 3;

    /// <summary>
    /// How close a proposal must be to show: the summed rarity (<see cref="RarityAsync"/>) of the content words it shares
    /// with the message, names left out. Chosen on the 13 replayed room messages of 2026-11-23..24, as of each: the
    /// highest wrong-as-stated fact scored 19.1 (#1008, "no PR opened, token dead", hours after one was) and the lowest
    /// on-topic fact above it 20.2. Every on-topic fact a lower floor added came with as many off-topic ones. To be
    /// checked on the next three days of wakes, which it hasn't seen.
    /// </summary>
    public const double ProposedFloor = 19.5;

    /// <summary>Statekeeper's line (7d685e5): a state unconfirmed for longer than this is shown dated and only when named.</summary>
    public const int StaleAfterDays = 7;

    /// <summary>One proposal that could have shown: its score, the words that earned it, and why it was held back if it was.</summary>
    public sealed record ProposedCandidate(FactRow Fact, string Concept, double Score, IReadOnlyList<string> Shared, string? Held, bool Shown);

    /// <summary>The two parts of concept recall: <paramref name="Top"/> before ordinary recall, <paramref name="After"/> after it.</summary>
    public sealed record ConceptRecall(string Top, string After, IReadOnlyList<ProposedCandidate> Candidates);

    /// <summary>
    /// A turn's concept recall in two parts. <c>Top</c>: the named concepts that hold accepted facts, with those facts only.
    /// <c>After</c>: up to <paramref name="cap"/> proposals scoring at least <paramref name="floor"/>, closest first;
    /// empty when none does. As of <paramref name="asOf"/>, only facts written before it count (a replay).
    /// </summary>
    public async Task<ConceptRecall> ConceptRecallAsync(string text, IReadOnlyDictionary<string, IReadOnlyList<Referent>>? ambiguous = null,
        DateTimeOffset? asOf = null, int maxConcepts = 5, int factsEach = 3, int budget = ConceptBudget, int snippet = 140,
        int cap = ProposedCap, double floor = ProposedFloor)
    {
        var touched = await ConceptsTouchedAsync(text, ambiguous, asOf);
        const string heading = "Concepts this message names, with the facts you accepted about them (proposals by the extractor "
            + "come after recall, if any is close to the message):";
        var top = await ConceptSectionAsync(touched.Where(t => t.Facts.Any(f => f.Status == "active")).ToList(), heading,
            t => Math.Min(factsEach, t.Facts.Count(f => f.Status == "active")), maxConcepts, budget, snippet);

        var candidates = await ProposedCandidatesAsync(text, touched, asOf ?? DateTimeOffset.UtcNow, cap, floor);
        var shown = candidates.Where(c => c.Shown).ToList();
        if (shown.Count == 0)
        {
            return new ConceptRecall(top, "", candidates);
        }

        var names = await NamesAsync(shown.Select(c => c.Fact).ToList());
        var sources = await concepts.SourcesAsync(shown.Select(c => c.Fact.Id).ToList());
        var sb = new StringBuilder($"Proposed facts closest to this message (the extractor's reading of a fragment, not yet accepted; "
            + $"at most {cap}, none when nothing is close; `concept show <name>` lists the rest):");
        foreach (var c in shown)
        {
            var from = sources.GetValueOrDefault(c.Fact.Id) ?? [];
            var line = RenderFact(c.Fact, null, names, from);
            sb.Append("\n  " + (line.Length > 200 ? line[..199] + "…" : line));
            if (from.Count > 0 && await EvidenceAsync(from[0], c.Fact.Text, snippet) is { } evidence)
            {
                sb.Append($"\n    #{from[0]}: \"{evidence}\"");
            }
        }

        return new ConceptRecall(top, sb.ToString(), candidates);
    }

    /// <summary>
    /// Every proposal of the touched concepts, once each, scored by closeness to <paramref name="text"/> and ranked
    /// closest first (newest-known, then highest id, on a tie). The first <paramref name="cap"/> not held back and at or
    /// above <paramref name="floor"/> are marked shown.
    /// </summary>
    private async Task<IReadOnlyList<ProposedCandidate>> ProposedCandidatesAsync(string text, IReadOnlyList<Touched> touched, DateTimeOffset now, int cap, double floor)
    {
        if (touched.Count == 0)
        {
            return [];
        }

        // Names decide which concepts are candidates, never how close a fact is: every fact of John's says "John".
        var named = new HashSet<string>();
        foreach (var t in touched)
        {
            foreach (var label in t.Aliases.Append(t.Concept.Name).Append(t.Via ?? ""))
            {
                named.UnionWith(Stems(label));
            }
        }

        var said = Stems(WithoutLocations(text));
        said.ExceptWith(named);
        var idf = await RarityAsync(said);
        var flags = await OpenFlagsByFactAsync();
        var directly = touched.Where(t => t.Via is null).Select(t => t.Concept.Id).ToHashSet();
        var fragments = new Dictionary<long, string?>();
        var seen = new HashSet<long>();
        var scored = new List<ProposedCandidate>();
        foreach (var t in touched)
        {
            var proposals = t.Facts.Where(f => f.Status == "proposed").ToList();
            var sources = await concepts.SourcesAsync(proposals.Select(f => f.Id).ToList());
            foreach (var f in proposals)
            {
                if (!seen.Add(f.Id))
                {
                    continue;
                }

                // The fact and the sentence of its fragment that backs it: the extractor's words and the self's.
                var words = Stems(f.Text);
                if (sources.GetValueOrDefault(f.Id) is [var first, ..])
                {
                    if (!fragments.TryGetValue(first, out var content))
                    {
                        content = fragments[first] = await ContentAsync(first);
                    }

                    if (content is not null)
                    {
                        words.UnionWith(Stems(Evidence(content, f.Text, int.MaxValue)));
                    }
                }

                var shared = said.Where(words.Contains).OrderByDescending(w => idf(w)).ThenBy(w => w, StringComparer.Ordinal).ToList();
                var held = flags.ContainsKey(f.Id) ? "flagged"
                    : f.State && IsStale(f, now) && !(directly.Contains(f.SubjectId) && f.SubjectId == t.Concept.Id) ? "a stale state whose subject isn't named directly"
                    : null;
                scored.Add(new ProposedCandidate(f, t.Concept.Name, shared.Sum(idf), shared, held, false));
            }
        }

        var ranked = scored.OrderByDescending(c => c.Score).ThenByDescending(c => c.Fact.RecordedAtUtc ?? "").ThenByDescending(c => c.Fact.Id).ToList();
        var room = cap;
        var written = new HashSet<string>();
        for (var i = 0; i < ranked.Count && room > 0; i++)
        {
            if (ranked[i].Held is null && ranked[i].Score > 0 && ranked[i].Score >= floor)
            {
                // The extractor writes the same sentence again from a later fragment (#951, #1008): one line says it.
                ranked[i] = written.Add(Normal(ranked[i].Fact.Text)) ? ranked[i] with { Shown = true } : ranked[i] with { Held = "the same words as a fact shown" };
                room -= ranked[i].Shown ? 1 : 0;
            }
        }

        return ranked;
    }

    private static bool IsStale(FactRow f, DateTimeOffset now) =>
        DateTimeOffset.TryParse(f.RecordedAtUtc ?? f.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var known)
        && now - known > TimeSpan.FromDays(StaleAfterDays);

    private async Task<string?> ContentAsync(long fragmentId)
    {
        try
        {
            var f = await GetAsync(fragmentId);
            return f.IsDeleted ? null : f.Content;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// How rare each of <paramref name="words"/> is across the store's fragments: ln((N + 1) / (df + 1)), df counted
    /// by the full-text index as fragments holding a word that starts with the stem. Over the ~1,000 facts alone the
    /// range was too narrow ("head" outscored "crop"); over ~20,000 fragments "main", "review" and "work" count for
    /// little and a commit hash or "decoy" for a lot. The whole store, not as of a time: it is a statistic of the
    /// language used, not something known.
    /// </summary>
    private async Task<Func<string, double>> RarityAsync(IReadOnlyCollection<string> words)
    {
        var (n, df) = await fragments.PrefixDocumentCountsAsync(words);
        return w => Math.Log((n + 1.0) / (df.GetValueOrDefault(w) + 1.0));
    }

    // Words of four letters or more that say nothing about what a message is about (dictation's fillers included).
    private static readonly HashSet<string> Unsaying =
    [
        "about", "above", "after", "again", "against", "also", "although", "always", "among", "another", "anyone", "anything", "around",
        "away", "back", "because", "been", "before", "being", "below", "between", "both", "came", "come", "comes", "could", "didn",
        "does", "doesn", "doing", "done", "down", "during", "each", "else", "even", "ever", "every", "from", "gets", "getting", "give",
        "given", "goes", "going", "gone", "good", "have", "having", "here", "hers", "herself", "himself", "into", "itself", "just",
        "know", "knew", "like", "look", "made", "make", "makes", "many", "maybe", "might", "more", "most", "much", "must", "myself",
        "need", "needs", "never", "next", "none", "nothing", "okay", "once", "only", "onto", "other", "others", "ours", "ourselves",
        "over", "really", "said", "same", "says", "seem", "seems", "should", "since", "some", "something", "sometimes", "still", "such",
        "sure", "take", "than", "that", "thats", "their", "theirs", "them", "themselves", "then", "there", "these", "they", "thing",
        "things", "think", "this", "those", "though", "through", "thus", "today", "together", "took", "toward", "under", "until",
        "upon", "very", "want", "wanted", "wants", "well", "went", "were", "what", "whatever", "when", "where", "whether", "which",
        "while", "whom", "whose", "will", "with", "within", "without", "would", "yeah", "your", "yours", "yourself", "isn", "wasn",
        "aren", "wouldn", "couldn", "shouldn", "hasn", "haven", "pause", "kind", "sort", "actually", "basically", "able", "thank",
        "thanks", "hello", "please",
    ];

    /// <summary>
    /// The content words of <paramref name="text"/>, crudely stemmed: four letters or more, not all digits, not in
    /// <see cref="Unsaying"/>, a plural s dropped and cut to six letters, so "deployed" meets "deploy" and "reboots" "reboot".
    /// Words are split on <see cref="ContextFragmentRepository.IsPrefixChar"/>, the rule the rarity lookup checks, so every
    /// stem is one it can count: "12m²" gives "12m", never a word the lookup refuses.
    /// </summary>
    internal static HashSet<string> Stems(string text) =>
        PrefixWords(text).Where(w => w.Length >= 4 && !w.All(char.IsDigit) && !Unsaying.Contains(w)).Select(Stem).ToHashSet();

    private static IEnumerable<string> PrefixWords(string text)
    {
        var lower = text.ToLowerInvariant();
        for (var i = 0; i < lower.Length;)
        {
            var start = i;
            while (i < lower.Length && ContextFragmentRepository.IsPrefixChar(lower[i]))
            {
                i++;
            }

            if (i > start)
            {
                yield return lower[start..i];
            }

            i = Math.Max(i, start + 1);
        }
    }

    private static string Stem(string w)
    {
        if (w.Length > 4 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal))
        {
            w = w[..^1];
        }

        return w.Length > 6 ? w[..6] : w;
    }
}
