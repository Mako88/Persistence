using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;
using Persistence.Services.Streaming;
using SimpleHttpClient;
using SimpleHttpClient.Models;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Persistence.Services;

/// <summary>
/// OpenAI Responses API client. Works with OpenAI and any endpoint that
/// implements the same API shape. When Model is "custom", the client uses
/// ApiBaseUrl instead of the default OpenAI endpoint. Requests use the
/// configured reasoning effort and do not persist responses server-side
/// (store = false).
/// </summary>
[Service(registerAsType: typeof(IModelClient), key: ModelProvider.OpenAI)]
public class OpenAiModelClient : IModelClient, IDisposable
{
    private readonly ISimpleClient client;
    private readonly string model;
    private readonly int maxCompletionTokens;
    private readonly string reasoningEffort;
    private readonly IAppConfig config;
    private readonly IDisplayProvider display;

    private const string DefaultBaseUrl = "https://api.openai.com/v1";

    public OpenAiModelClient(IAppConfig config, IDisplayProvider display)
        : this(config, display, CreateClient(config))
    {
    }

    /// <summary>
    /// Test seam: accepts a pre-configured <see cref="ISimpleClient"/> (e.g. a fake)
    /// instead of constructing one from config.
    /// </summary>
    internal OpenAiModelClient(IAppConfig config, IDisplayProvider display, ISimpleClient client)
    {
        model = config.Model;
        maxCompletionTokens = config.MaxOutputTokens;
        reasoningEffort = config.ReasoningEffort;

        this.client = client;
        this.config = config;
        this.display = display;
    }

    private static ISimpleClient CreateClient(IAppConfig config)
    {
        var baseUrl = config.ApiBaseUrl ?? DefaultBaseUrl;
        var client = new SimpleClient(baseUrl.TrimEnd('/'));
        client.DefaultHeaders["Authorization"] = $"Bearer {config.ApiKey}";
        return client;
    }

    public async Task<string> CompleteAsync(PromptRequest request, CancellationToken ct = default)
    {
        var apiRequest = BuildApiRequest(request, stream: false);
        var response = await client.MakeRequest(apiRequest);

        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"API call failed ({response.StatusCode}): {response.StringBody}");
        }

        using var doc = JsonDocument.Parse(response.StringBody);
        var responseMessage = ExtractOutputText(doc.RootElement);

        var reasoning = ExtractReasoningSummary(doc.RootElement);
        if (reasoning.Length > 0)
        {
            display.ShowReasoning(reasoning);
        }

        if (config.DebugMode)
        {
            display.ShowDebugInfo($"Response:\n{responseMessage}\n");
        }

        return responseMessage;
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        PromptRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var apiRequest = BuildApiRequest(request, stream: true);

        using var response = await client.MakeStreamRequest(apiRequest, ct);

        if (!response.IsSuccessful)
        {
            var error = await ReadBodyAsync(response.Body, ct);
            throw new InvalidOperationException($"API call failed ({response.StatusCode}): {error}");
        }

        await foreach (var evt in OpenAiResponseStreamParser.ParseAsync(EventData(response, ct), ct))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// Projects SimpleHttpClient's parsed Server-Sent Events down to their decoded
    /// <c>data:</c> payloads (the package owns the SSE wire-format parsing).
    /// </summary>
    private static async IAsyncEnumerable<string> EventData(
        ISimpleStreamResponse response, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var sse in response.ReadServerSentEventsAsync(ct))
        {
            yield return sse.Data;
        }
    }

    private static async Task<string> ReadBodyAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Builds the Responses API request, emitting request debug info when enabled.
    /// </summary>
    private SimpleRequest BuildApiRequest(PromptRequest request, bool stream)
    {
        var messages = request.Messages
            .Select(m => new { role = m.Role, content = m.Content })
            .ToArray();

        var body = new
        {
            model,
            input = messages,
            max_output_tokens = maxCompletionTokens,
            reasoning = new { effort = reasoningEffort, summary = "auto" },
            store = false,
            stream
        };

        if (config.DebugMode)
        {
            var debugContent = $"{messages[^2]?.content}\n\n{messages[^1]?.content}";
            display.ShowDebugInfo($"Request ({request.Messages.Count} messages):\n{debugContent}\n");
        }

        return new SimpleRequest("/responses", HttpMethod.Post, body);
    }

    /// <summary>
    /// Extracts the assistant text from a Responses API result. The top-level
    /// "output" array holds typed items (reasoning, message, ...); message items
    /// carry the user-visible text as "output_text" content parts.
    /// </summary>
    private static string ExtractOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("API returned no output.");
        }

        var text = new StringBuilder();

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "message")
            {
                continue;
            }

            if (!item.TryGetProperty("content", out var parts) || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var partType)
                    && partType.GetString() == "output_text"
                    && part.TryGetProperty("text", out var partText))
                {
                    text.Append(partText.GetString());
                }
            }
        }

        return text.Length == 0
            ? throw new InvalidOperationException("API returned no output text.")
            : text.ToString();
    }

    /// <summary>
    /// Extracts the model's reasoning summary, if present. Reasoning items in the
    /// "output" array carry summary parts as "summary_text" entries. Returns an
    /// empty string when the model emits no summary.
    /// </summary>
    private static string ExtractReasoningSummary(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "reasoning")
            {
                continue;
            }

            if (!item.TryGetProperty("summary", out var parts) || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var partType)
                    && partType.GetString() == "summary_text"
                    && part.TryGetProperty("text", out var partText))
                {
                    if (text.Length > 0)
                    {
                        text.Append("\n\n");
                    }

                    text.Append(partText.GetString());
                }
            }
        }

        return text.ToString();
    }

    public void Dispose() => (client as IDisposable)?.Dispose();
}
