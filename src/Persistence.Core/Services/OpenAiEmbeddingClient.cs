using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;
using SimpleHttpClient;
using SimpleHttpClient.Models;
using System.Text.Json;

namespace Persistence.Services;

/// <summary>
/// <see cref="IEmbeddingClient"/> over the OpenAI-compatible <c>/embeddings</c> endpoint — the same
/// de-facto standard <see cref="OpenAiChatModelClient"/> speaks, so a llama.cpp server started with
/// <c>--embedding</c> serves it without any adapter.
///
/// Every failure path returns an empty result rather than throwing. Semantic recall is an enrichment
/// layered over a lexical path that already works; a wedged or missing embedding server must degrade
/// recall, not break the peer's turn.
/// </summary>
[Singleton(typeof(IEmbeddingClient))]
public class OpenAiEmbeddingClient : IEmbeddingClient, IDisposable
{
    private readonly EmbeddingSettings settings;
    private readonly IDisplayProvider display;
    private readonly ISimpleClient? client;

    /// <inheritdoc />
    public bool IsEnabled => client != null;

    /// <inheritdoc />
    public string Model => settings.Model;

    /// <summary>
    /// Constructor that builds the HTTP client from config
    /// </summary>
    public OpenAiEmbeddingClient(IAppConfig config, IDisplayProvider display)
        : this(config.Embedding, display, CreateClient(config.Embedding))
    {
    }

    /// <summary>
    /// Test seam: accepts a pre-configured <see cref="ISimpleClient"/> (e.g. a fake) instead of
    /// constructing one from config.
    /// </summary>
    internal OpenAiEmbeddingClient(EmbeddingSettings settings, IDisplayProvider display, ISimpleClient? client)
    {
        this.settings = settings;
        this.display = display;
        this.client = client;
    }

    private static ISimpleClient? CreateClient(EmbeddingSettings settings)
    {
        // A missing base URL is a configuration omission, not an error worth crashing a peer over:
        // treat it as "semantic recall is off" and let the lexical path carry recall.
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.ApiBaseUrl))
        {
            return null;
        }

        var client = new SimpleClient(settings.ApiBaseUrl.TrimEnd('/'))
        {
            Timeout = settings.RequestTimeoutSeconds,
        };

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            client.DefaultHeaders["Authorization"] = $"Bearer {settings.ApiKey}";
        }

        return client;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (client == null || texts.Count == 0)
        {
            return [];
        }

        var results = new List<float[]>(texts.Count);

        foreach (var batch in Chunk(texts, Math.Max(1, settings.BatchSize)))
        {
            var vectors = await EmbedBatchAsync(batch, ct);

            // A partial failure would silently misalign vectors with their texts, which would attach
            // one fragment's meaning to another. Abandon the whole call instead.
            if (vectors.Count != batch.Count)
            {
                return [];
            }

            results.AddRange(vectors);
        }

        return results;
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> batch, CancellationToken ct)
    {
        try
        {
            var request = new SimpleRequest("/embeddings", HttpMethod.Post, new
            {
                model = settings.Model,
                input = batch,
            });

            var response = await client!.MakeRequest(request, ct);

            if (!response.IsSuccessful)
            {
                display.ShowDebugInfo(
                    $"Embedding request failed ({response.StatusCode}): {response.StringBody}");
                return [];
            }

            return ParseEmbeddings(response.StringBody);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller's cancellation is its decision, not an embedder failure: let it escape so it
            // is never read as "no vectors, fall back to words". SimpleHttpClient 5.1.0's own request
            // timeout does not arrive here: it catches its internal cancellation while the caller's token
            // is live and throws TimeoutException, which the catch below turns into no vectors.
            throw;
        }
        catch (Exception ex)
        {
            display.ShowDebugInfo($"Embedding request threw: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Reads <c>data[].embedding</c> from an OpenAI-compatible embeddings response, ordered by the
    /// response's own <c>index</c> when present — the spec allows results out of order, and silently
    /// trusting array order would pair vectors with the wrong text. Internal for tests.
    /// </summary>
    internal static IReadOnlyList<float[]> ParseEmbeddings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var indexed = new List<(int index, float[] vector)>();

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("embedding", out var embedding)
                || embedding.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var vector = ReadVector(embedding);

            if (vector.Length == 0)
            {
                continue;
            }

            var index = item.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i)
                ? i
                : indexed.Count;

            indexed.Add((index, vector));
        }

        return indexed.OrderBy(x => x.index).Select(x => x.vector).ToList();
    }

    /// <summary>
    /// Reads one <c>embedding</c> value. Normally a flat array of floats — but a llama.cpp server run
    /// without <c>--pooling mean</c> returns one vector <em>per token</em>, a nested array. Verified
    /// against a live server: the flat shape is what a pooled server sends. Mean-pooling the nested
    /// shape here is exactly what the pooled server would have done, so a misconfigured server degrades
    /// to slightly different vectors rather than to no semantic recall at all — which would otherwise
    /// be invisible, since the failure path is a silent fall back to lexical.
    /// </summary>
    private static float[] ReadVector(JsonElement embedding)
    {
        var rows = embedding.EnumerateArray().ToList();

        if (rows.Count == 0)
        {
            return [];
        }

        if (rows[0].ValueKind != JsonValueKind.Array)
        {
            return rows.Select(v => v.GetSingle()).ToArray();
        }

        var tokenVectors = rows
            .Select(r => r.EnumerateArray().Select(v => v.GetSingle()).ToArray())
            .Where(v => v.Length > 0)
            .ToList();

        if (tokenVectors.Count == 0)
        {
            return [];
        }

        var width = tokenVectors[0].Length;
        var pooled = new float[width];

        foreach (var tokenVector in tokenVectors.Where(v => v.Length == width))
        {
            for (var i = 0; i < width; i++)
            {
                pooled[i] += tokenVector[i];
            }
        }

        for (var i = 0; i < width; i++)
        {
            pooled[i] /= tokenVectors.Count;
        }

        return pooled;
    }

    private static IEnumerable<IReadOnlyList<T>> Chunk<T>(IReadOnlyList<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
        {
            yield return items.Skip(i).Take(size).ToList();
        }
    }

    /// <summary>
    /// Disposes the underlying HTTP client if it is disposable
    /// </summary>
    public void Dispose() => (client as IDisposable)?.Dispose();
}
