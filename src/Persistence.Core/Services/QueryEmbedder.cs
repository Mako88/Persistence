using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;

namespace Persistence.Services;

/// <summary>
/// Embeds a recall query under the store's query policy (<see cref="EmbeddingSettings.QueryTailChars"/>), and
/// says so when it cannot. Recall falls back to exact words (BM25) whenever there is no query vector; that
/// fallback used to be silent, and on long messages it went unseen for weeks (2026-11-11). Now every one is
/// logged and kept in <see cref="Fallback"/> for the recall output to show.
///
/// <para>
/// With a tail set, a refusal (the embedder returns nothing) is retried on a shorter tail, at most
/// <see cref="MaxShrinks"/> times and never below <see cref="MinTailChars"/>: the tail length is a
/// character heuristic for a token limit, and token-dense text (long numbers, code, non-Latin script) runs
/// past 512 tokens in fewer characters. The embedder does not say why it returned nothing, so an unreachable
/// one costs the same few retries; a text that already failed is not tried again until <see cref="Clear"/>.
/// With no tail (0, the default), the whole query is sent once, as before the setting existed.
/// </para>
/// </summary>
[Singleton(typeof(QueryEmbedder))]
public class QueryEmbedder(IEmbeddingClient embeddings, IAppConfig config, IDisplayProvider display)
{
    /// <summary>How many times a refused tail is halved and retried.</summary>
    public const int MaxShrinks = 2;

    /// <summary>The shortest tail retried: shorter and it no longer says what a long message is about.</summary>
    public const int MinTailChars = 300;

    /// <summary>The part of a recall that compares authored fragments by meaning.</summary>
    public const string Fragments = "fragments";

    /// <summary>The part of a recall that compares past utterances by meaning (conversation recall).</summary>
    public const string PastUtterances = "past utterances";

    private string? failed;
    private string? failedWhy;
    private readonly HashSet<string> compared = [];
    private readonly Dictionary<string, string> fellBack = [];

    /// <summary>Why the last query since <see cref="Clear"/> ran on words alone, or null when none did.</summary>
    public string? Fallback { get; private set; }

    /// <summary>
    /// The parts of this recall (<see cref="Fragments"/>, <see cref="PastUtterances"/>) whose meaning comparison ran:
    /// the positive evidence for a recall's path (<see cref="Persistence.Services.RecallPath"/>).
    /// </summary>
    public IReadOnlySet<string> Compared => compared;

    /// <summary>The parts of this recall that asked for meaning and ran on words instead, each with why: the negative evidence.</summary>
    public IReadOnlyDictionary<string, string> FellBack => fellBack;

    /// <summary>Starts a recall afresh: no fallback noted, nothing compared, nothing remembered as failed.</summary>
    public void Clear()
    {
        Fallback = null;
        failed = null;
        failedWhy = null;
        compared.Clear();
        fellBack.Clear();
    }

    /// <summary>Notes that <paramref name="part"/> of a recall compared meaning: its query vector was ranked against stored ones.</summary>
    public void Compare(string part) => compared.Add(part);

    /// <summary>Notes that <paramref name="part"/> of a recall ran on words alone, and why, and logs it.</summary>
    public void Note(string why, string part = Fragments)
    {
        fellBack.TryAdd(part, why);

        // One recall asks the surfacer more than once (here, then elsewhere); the same reason is logged once.
        // The fragments' notice keeps its wording (scripts and the app's log match on it); another part names itself,
        // because when the fragments did compare meaning, "recall ran on exact words alone" would not be true.
        var fallback = part == Fragments
            ? $"Recall ran on exact words alone, without meaning: {why}."
            : $"Recall of {part} ran on exact words alone, without meaning: {why}.";
        if (Fallback is not null && Fallback.Contains(fallback, StringComparison.Ordinal))
        {
            return;
        }

        Fallback = Fallback is null ? fallback : $"{Fallback} {fallback}";
        display.ShowError(fallback);
    }

    /// <summary>The text actually sent for <paramref name="text"/> under a tail of <paramref name="tailChars"/> (0: all of it).</summary>
    public static string Tail(string text, int tailChars) =>
        tailChars > 0 && text.Length > tailChars ? text[^tailChars..] : text;

    /// <summary>The query's vector, or null (noted in <see cref="Fallback"/>) when the embedder gave none.</summary>
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default, string part = Fragments)
    {
        if (text == failed)
        {
            // Not sent again and not logged again, but this part ran on words too, for the same reason.
            fellBack.TryAdd(part, failedWhy!);
            return null;
        }

        var tailChars = config.Embedding.QueryTailChars;
        var sent = Tail(text, tailChars);
        var tried = new List<int>();
        for (var shrinks = 0; ; shrinks++)
        {
            tried.Add(sent.Length);
            var vectors = await embeddings.EmbedAsync([sent], ct);
            if (vectors.Count > 0 && vectors[0].Length > 0)
            {
                return vectors[0];
            }

            if (tailChars <= 0 || shrinks == MaxShrinks || sent.Length <= MinTailChars)
            {
                break;
            }

            sent = Tail(sent, Math.Max(MinTailChars, sent.Length / 2));
        }

        failed = text;
        failedWhy = tailChars <= 0
            ? $"the embedder gave no vector for the whole {text.Length:N0}-character message (query tail is 0: sent whole)"
            : $"the embedder gave no vector for the message ({text.Length:N0} characters; tried its last {string.Join(", then ", tried.Select(n => n.ToString("N0")))})";
        Note(failedWhy, part);
        return null;
    }
}

/// <summary>
/// Which way a recall actually ran, read off what happened rather than inferred from the settings or the
/// message's length (Ember, 2026-11-12). A recall has parts that compare meaning (<see cref="QueryEmbedder.Fragments"/>,
/// <see cref="QueryEmbedder.PastUtterances"/>); a part counts as meaning only when its comparison ran and it did not
/// then fall back. <c>semantic</c>: every part that asked for meaning compared it. <c>semantic-degraded</c>: at least
/// one part compared meaning and another ran on words, with which and why (Ember's CHANGES on f922439: such a recall
/// is neither clean semantic nor words alone, and a benchmark must count it apart from both). <c>words-setting</c>:
/// embedding is off, so exact words were the choice. <c>words-fallback</c>: meaning was asked for and no part compared
/// it, with why. <c>unknown</c>: neither was seen (an empty query, nothing asked of the surfacer).
/// </summary>
public static class RecallPath
{
    public const string Semantic = "semantic";
    public const string SemanticDegraded = "semantic-degraded";
    public const string WordsBySetting = "words-setting";
    public const string WordsByFallback = "words-fallback";
    public const string Unknown = "unknown";

    /// <summary>
    /// The path and, unless it is clean semantic, why. A fallback never outranks a part that compared meaning: that
    /// recall is degraded, not words alone.
    /// </summary>
    public static (string Path, string? Why) Of(bool embeddingEnabled, QueryEmbedder query)
    {
        if (!embeddingEnabled)
        {
            return (WordsBySetting, "embedding is off");
        }

        var meaning = query.Compared.Where(p => !query.FellBack.ContainsKey(p)).ToList();
        var words = string.Join("; ", query.FellBack.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}: {f.Value}"));
        return (meaning.Count > 0, query.FellBack.Count > 0) switch
        {
            (true, false) => (Semantic, null),
            (true, true) => (SemanticDegraded, words),
            (false, true) => (WordsByFallback, words),
            _ => (Unknown, null),
        };
    }
}
