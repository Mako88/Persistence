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

    private string? failed;

    /// <summary>Why the last query since <see cref="Clear"/> ran on words alone, or null when none did.</summary>
    public string? Fallback { get; private set; }

    /// <summary>Starts a recall afresh: no fallback noted, nothing remembered as failed.</summary>
    public void Clear()
    {
        Fallback = null;
        failed = null;
    }

    /// <summary>Notes that recall ran on words alone, and why, and logs it.</summary>
    public void Note(string why)
    {
        Fallback = $"Recall ran on exact words alone, without meaning: {why}.";
        display.ShowError(Fallback);
    }

    /// <summary>The text actually sent for <paramref name="text"/> under a tail of <paramref name="tailChars"/> (0: all of it).</summary>
    public static string Tail(string text, int tailChars) =>
        tailChars > 0 && text.Length > tailChars ? text[^tailChars..] : text;

    /// <summary>The query's vector, or null (noted in <see cref="Fallback"/>) when the embedder gave none.</summary>
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (text == failed)
        {
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
        Note(tailChars <= 0
            ? $"the embedder gave no vector for the whole {text.Length:N0}-character message (query tail is 0: sent whole)"
            : $"the embedder gave no vector for the message ({text.Length:N0} characters; tried its last {string.Join(", then ", tried.Select(n => n.ToString("N0")))})");
        return null;
    }
}
