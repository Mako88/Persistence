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

    /// <inheritdoc />
    public ModelUsage? LastUsage { get; private set; }

    /// <inheritdoc />
    public string? LastStopReason { get; private set; }

    /// <summary>
    /// Constructor that builds the HTTP client from config
    /// </summary>
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

    /// <summary>
    /// The value shipped in persistence.template.json — treated as "no key set" so a copied-but-
    /// unedited template fails fast with a clear message instead of a runtime 401.
    /// </summary>
    private const string PlaceholderApiKey = "YOUR_API_KEY_HERE";

    private static ISimpleClient CreateClient(IAppConfig config)
    {
        // Validate the key here — in the component that actually consumes it — so the check lives
        // with its owner (not AppConfig or the entry points) and fires at startup: this client is a
        // startup-resolved singleton, so a bad key fails fast rather than 401-ing mid-conversation.
        // A custom ApiBaseUrl is exempt: an OpenAI-compatible endpoint (e.g. a local model) may
        // legitimately need no key.
        if (string.IsNullOrEmpty(config.ApiBaseUrl)
            && (string.IsNullOrWhiteSpace(config.ApiKey) || config.ApiKey == PlaceholderApiKey))
        {
            var detail = config.ApiKey == PlaceholderApiKey
                ? " (it's still the persistence.template.json placeholder)"
                : "";

            throw new InvalidOperationException(
                $"OpenAI provider is selected but no API key is set{detail}. Add your real key to "
                + "persistence.json (\"ApiKey\") or set the PERSISTENCE_APIKEY environment variable.");
        }

        var baseUrl = config.ApiBaseUrl ?? DefaultBaseUrl;
        var client = new SimpleClient(baseUrl.TrimEnd('/'));
        client.DefaultHeaders["Authorization"] = $"Bearer {config.ApiKey}";
        // Generous timeout: slow/local endpoints can take a while to ingest a large prompt and
        // begin responding; the default would cancel the request mid-flight.
        client.Timeout = config.RequestTimeoutSeconds;
        return client;
    }

    /// <summary>
    /// Sends a non-streaming completion request and returns the assistant's text, surfacing any
    /// reasoning summary and debug info as a side effect
    /// </summary>
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

        LastUsage = ReadUsage(doc.RootElement);
        // The Responses API reports truncation as incomplete_details.reason = "max_output_tokens"
        // rather than a finish_reason; normalise to the shared spelling so IsTruncation sees it.
        LastStopReason = doc.RootElement.TryGetProperty("incomplete_details", out var incomplete)
            && incomplete.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
            && reason.GetString() == "max_output_tokens"
                ? ModelStopReason.Length
                : null;

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

    /// <summary>
    /// Records the provider's real input-token count (Responses API: <c>usage.input_tokens</c>)
    /// alongside our estimate of the same prompt, so the budget readout can self-calibrate.
    /// </summary>
    /// <summary>
    /// Reads a Responses API usage block from an element that contains <c>usage</c>
    /// (<c>input_tokens</c> / <c>output_tokens</c>) — the response root for a non-streaming call, or
    /// the <c>response</c> object inside the streamed <c>response.completed</c> event. Null if absent.
    /// </summary>
    private static ModelUsage? ReadUsage(JsonElement container)
    {
        if (container.TryGetProperty("usage", out var usage)
            && usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var inTok))
        {
            var outTok = usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ot) ? ot : 0;

            // OpenAI auto-caches long shared prefixes and reports the cached portion under
            // input_tokens_details.cached_tokens. input_tokens is the TOTAL (cached + uncached), so split
            // it: ModelUsage.InputTokens is the uncached part (billed at full rate) and CacheReadTokens is
            // the cached part (billed at the discounted rate), matching how the cost readout sums them.
            // OpenAI doesn't bill a separate cache-creation cost, so CacheCreationTokens stays 0.
            var cached = usage.TryGetProperty("input_tokens_details", out var details)
                && details.TryGetProperty("cached_tokens", out var c) && c.TryGetInt32(out var cTok) ? cTok : 0;

            return new ModelUsage(Math.Max(0, inTok - cached), outTok, CacheReadTokens: cached);
        }

        return null;
    }

    /// <summary>
    /// Sends a streaming completion request and yields parsed model stream events as they arrive
    /// </summary>
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

        // Reset so a mid-stream failure doesn't leave a prior call's usage looking current.
        LastUsage = null;

        // Accumulate the streamed text so the response can be logged to the Debug pane on completion,
        // just like the non-streaming path does — otherwise streamed turns would log a request with no
        // matching response.
        var responseText = new StringBuilder();

        await foreach (var evt in OpenAiResponseStreamParser.ParseAsync(CaptureUsage(EventData(response, ct)), ct))
        {
            if (evt.Kind == ModelStreamEventKind.OutputTextDelta)
            {
                responseText.Append(evt.Text);
            }

            yield return evt;
        }

        if (config.DebugMode)
        {
            display.ShowDebugInfo($"Response:\n{responseText}");
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

    /// <summary>
    /// Passes each SSE data payload through unchanged, capturing token usage as a side effect: the
    /// terminal <c>response.completed</c> event carries the full response (including <c>usage</c>), so
    /// <see cref="LastUsage"/> ends the stream set to the real provider counts.
    /// </summary>
    private async IAsyncEnumerable<string> CaptureUsage(
        IAsyncEnumerable<string> dataPayloads, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var data in dataPayloads.WithCancellation(ct))
        {
            TryCaptureCompletedUsage(data);
            yield return data;
        }
    }

    /// <summary>Sets <see cref="LastUsage"/> from a <c>response.completed</c> payload's nested response object.</summary>
    private void TryCaptureCompletedUsage(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var type) && type.GetString() == "response.completed"
                && root.TryGetProperty("response", out var resp)
                && ReadUsage(resp) is { } usage)
            {
                LastUsage = usage;
            }
        }
        catch (JsonException)
        {
            // Not the completed event (or malformed) — nothing to capture.
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

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = messages,
            ["max_output_tokens"] = maxCompletionTokens,
            ["store"] = false,
            ["stream"] = stream,
        };

        // Native reasoning off — the peer reasons in Persistence's <think> channel — so don't ask the
        // provider to reason. Otherwise pass the configured effort.
        if (!ReasoningEffortValue.IsOff(reasoningEffort))
        {
            body["reasoning"] = new { effort = reasoningEffort, summary = "auto" };
        }

        if (config.DebugMode)
        {
            // Show the tail of the prompt (last two segments). TakeLast is safe for 0/1/2+ messages —
            // messages[^2] would index out of range on a single-message prompt.
            var debugContent = string.Join("\n\n", messages.TakeLast(2).Select(m => m.content));
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

    /// <summary>
    /// Disposes the underlying HTTP client if it is disposable
    /// </summary>
    public void Dispose() => (client as IDisposable)?.Dispose();
}
