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
/// Client for the Anthropic Claude Messages API (<c>/v1/messages</c>). Talks directly to
/// <c>api.anthropic.com</c> — or a compatible endpoint via <c>ApiBaseUrl</c> (e.g. a proxy) — using
/// Anthropic's own auth (<c>x-api-key</c> + <c>anthropic-version</c> headers), not the OpenAI
/// <c>Authorization: Bearer</c> convention. Requests turn on adaptive thinking with a summarized
/// display so reasoning streams alongside the answer, and map <see cref="ModelProfile.ReasoningEffort"/>
/// onto the Messages API <c>output_config.effort</c> control when it names a Claude effort level.
/// </summary>
[Service(registerAsType: typeof(IModelClient), key: ModelProvider.Anthropic)]
public class AnthropicModelClient : IModelClient, IDisposable
{
    private readonly ISimpleClient client;
    private readonly string model;
    private readonly int maxTokens;
    private readonly string reasoningEffort;
    private readonly IAppConfig config;
    private readonly IDisplayProvider display;

    /// <inheritdoc />
    public ModelUsage? LastUsage { get; private set; }

    /// <inheritdoc />
    public string? LastStopReason { get; private set; }

    // Streaming usage is spread across events: input arrives on message_start, output accrues on
    // message_delta. Captured into these across a stream, then folded into LastUsage at its end.
    private int? streamInputTokens;
    private int? streamOutputTokens;
    private int streamCacheReadTokens;
    private int streamCacheCreationTokens;

    private const string DefaultBaseUrl = "https://api.anthropic.com";
    private const string AnthropicVersion = "2023-06-01";
    private const string PlaceholderApiKey = "YOUR_API_KEY_HERE";

    /// <summary>The effort levels the Messages API accepts; others are dropped so the request
    /// doesn't 400 on an OpenAI-only value like "minimal".</summary>
    private static readonly HashSet<string> ClaudeEfforts =
        new(StringComparer.OrdinalIgnoreCase) { "low", "medium", "high", "xhigh", "max" };

    /// <summary>
    /// Models whose thinking is permanently on — an explicit <c>{type:"disabled"}</c> returns 400, so
    /// when reasoning is off we omit the thinking param for these (it stays on, unavoidably).
    /// </summary>
    private static bool ThinkingAlwaysOn(string model) =>
        model.StartsWith("claude-fable", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("claude-mythos", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Constructor that builds the HTTP client from config
    /// </summary>
    public AnthropicModelClient(IAppConfig config, IDisplayProvider display)
        : this(config, display, CreateClient(config))
    {
    }

    /// <summary>
    /// Test seam: accepts a pre-configured <see cref="ISimpleClient"/> (e.g. a fake) instead of
    /// constructing one from config.
    /// </summary>
    internal AnthropicModelClient(IAppConfig config, IDisplayProvider display, ISimpleClient client)
    {
        model = config.Model;
        maxTokens = config.MaxOutputTokens;
        reasoningEffort = config.ReasoningEffort;

        this.client = client;
        this.config = config;
        this.display = display;
    }

    private static ISimpleClient CreateClient(IAppConfig config)
    {
        // Validate the key here — in the component that actually consumes it — so a missing/placeholder
        // key fails fast at startup rather than 401-ing mid-conversation. A custom ApiBaseUrl is exempt:
        // a proxy or compatible endpoint may authenticate differently (or not at all).
        if (string.IsNullOrEmpty(config.ApiBaseUrl)
            && (string.IsNullOrWhiteSpace(config.ApiKey) || config.ApiKey == PlaceholderApiKey))
        {
            var detail = config.ApiKey == PlaceholderApiKey
                ? " (it's still the persistence.template.json placeholder)"
                : "";

            throw new InvalidOperationException(
                $"Anthropic provider is selected but no API key is set{detail}. Add your real key to "
                + "persistence.json (\"ApiKey\") or set the PERSISTENCE_APIKEY environment variable.");
        }

        var baseUrl = config.ApiBaseUrl ?? DefaultBaseUrl;
        var client = new SimpleClient(baseUrl.TrimEnd('/'));
        // Anthropic auth: the key rides on x-api-key (not Authorization), and every request must
        // declare the API version it was written against.
        client.DefaultHeaders["x-api-key"] = config.ApiKey;
        client.DefaultHeaders["anthropic-version"] = AnthropicVersion;
        // Generous timeout: a large prompt can take a while to ingest before the first byte arrives;
        // the default would cancel the request mid-flight.
        client.Timeout = config.RequestTimeoutSeconds;
        return client;
    }

    /// <summary>
    /// Sends a non-streaming completion request and returns the assistant's text, surfacing any
    /// thinking summary and debug info as a side effect
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
        var responseMessage = ExtractText(doc.RootElement);

        LastUsage = ReadUsage(doc.RootElement);
        LastStopReason = ReadStopReason(doc.RootElement);

        var reasoning = ExtractThinking(doc.RootElement);
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

        // Reset before the stream so a mid-stream failure doesn't leave a prior call's usage current.
        LastUsage = null;
        LastStopReason = null;
        streamInputTokens = null;
        streamOutputTokens = null;
        streamCacheReadTokens = 0;
        streamCacheCreationTokens = 0;

        // Accumulate the streamed text so the response can be logged to the Debug pane on completion,
        // just like the non-streaming path does — otherwise streamed turns would log a request with no
        // matching response.
        var responseText = new StringBuilder();

        await foreach (var evt in AnthropicMessageStreamParser.ParseAsync(CaptureUsage(EventData(response, ct)), ct))
        {
            if (evt.Kind == ModelStreamEventKind.OutputTextDelta)
            {
                responseText.Append(evt.Text);
            }

            yield return evt;
        }

        // Fold the usage gathered across message_start/message_delta into the final figure.
        if (streamInputTokens is { } input)
        {
            LastUsage = new ModelUsage(input, streamOutputTokens ?? 0, streamCacheReadTokens, streamCacheCreationTokens);
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

    private static async Task<string> ReadBodyAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Reads a Messages API usage block from the non-streaming response root — uncached
    /// <c>input_tokens</c>, <c>output_tokens</c>, and the prompt-cache portions
    /// (<c>cache_read_input_tokens</c> / <c>cache_creation_input_tokens</c>) — or null when absent.
    /// </summary>
    private static ModelUsage? ReadUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage)
            && usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var inTok))
        {
            return new ModelUsage(inTok, ReadInt(usage, "output_tokens"),
                ReadInt(usage, "cache_read_input_tokens"), ReadInt(usage, "cache_creation_input_tokens"));
        }

        return null;
    }

    /// <summary>
    /// The provider's stop reason for a non-streaming response, or null if absent. Returned verbatim —
    /// see <see cref="IModelClient.LastStopReason"/> for why it isn't normalised here.
    /// </summary>
    private static string? ReadStopReason(JsonElement root) =>
        root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String
            ? sr.GetString()
            : null;

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    /// <summary>
    /// Passes each SSE data payload through unchanged, capturing usage as a side effect: input tokens
    /// from <c>message_start</c> and the running output-token count from each <c>message_delta</c>.
    /// </summary>
    private async IAsyncEnumerable<string> CaptureUsage(
        IAsyncEnumerable<string> dataPayloads, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var data in dataPayloads.WithCancellation(ct))
        {
            TryCaptureStreamUsage(data);
            yield return data;
        }
    }

    private void TryCaptureStreamUsage(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "message_start"
                && root.TryGetProperty("message", out var message)
                && message.TryGetProperty("usage", out var startUsage))
            {
                if (startUsage.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var iv))
                {
                    streamInputTokens = iv;
                }
                if (startUsage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ov))
                {
                    streamOutputTokens = ov;
                }
                streamCacheReadTokens = ReadInt(startUsage, "cache_read_input_tokens");
                streamCacheCreationTokens = ReadInt(startUsage, "cache_creation_input_tokens");
            }
            else if (type == "message_delta")
            {
                if (root.TryGetProperty("usage", out var deltaUsage)
                    && deltaUsage.TryGetProperty("output_tokens", out var od) && od.TryGetInt32(out var odv))
                {
                    streamOutputTokens = odv; // cumulative; last one wins
                }

                // The stop reason arrives here rather than at message_start — it isn't known until
                // generation ends, which is exactly the case a truncated turn needs it for.
                if (root.TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                {
                    LastStopReason = sr.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Not a usage-bearing event (or malformed) — nothing to capture.
        }
    }

    /// <summary>
    /// Builds the Messages API request body. The Messages API forbids a system/developer role inside
    /// <c>messages</c>, so those segments are folded into user-role messages (adjacent same-role
    /// messages are merged) — keeping the end-positioned format instructions at the end rather than
    /// hoisting them into a top-level system prompt. Adaptive thinking is enabled with a summarized
    /// display so reasoning streams; the configured effort is applied only when it names a Claude
    /// effort level.
    /// </summary>
    private SimpleRequest BuildApiRequest(PromptRequest request, bool stream)
    {
        // Anonymous types can't be conditionally shaped, so assemble the body as a dictionary: the
        // effort control is only present when the configured value is one the Messages API accepts.
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = BuildApiMessages(request),
            ["stream"] = stream,
        };

        if (ReasoningEffortValue.IsOff(reasoningEffort))
        {
            // Native reasoning off — the peer reasons in Persistence's <think> channel. Fable/Mythos
            // have thinking permanently on (an explicit disable returns 400), so there we omit the
            // param (thinking stays on, unavoidably) rather than crash.
            if (!ThinkingAlwaysOn(model))
            {
                body["thinking"] = new { type = "disabled" };
            }
        }
        else
        {
            body["thinking"] = new { type = "adaptive", display = "summarized" };

            if (ClaudeEfforts.Contains(reasoningEffort))
            {
                body["output_config"] = new { effort = reasoningEffort.ToLowerInvariant() };
            }
        }

        if (config.DebugMode)
        {
            var debugContent = string.Join("\n\n", request.Messages.Select(m => m.Content));
            display.ShowDebugInfo($"Request ({request.Messages.Count} messages):\n{debugContent}\n");
        }

        return new SimpleRequest("/v1/messages", HttpMethod.Post, body);
    }

    /// <summary>
    /// Re-maps the role-labelled prompt (built by <c>OpenAiPromptBuilder</c>) to the Messages API's
    /// user/assistant-only shape: "assistant" stays assistant (the remote peer's turns), everything
    /// else (system/developer/user) becomes user. Adjacent same-role messages are merged, since the
    /// fold collapses a developer segment onto a neighbouring user one.
    /// </summary>
    /// <summary>
    /// Maps the folded messages to the API's on-the-wire shape, placing one prompt-cache breakpoint
    /// (<c>cache_control: ephemeral</c>) on the second-to-last message. That caches the whole stable
    /// prefix — identity + the conversation/thoughts — while the final message (the volatile
    /// sensory/format block, which changes every turn) stays uncached. Next turn the unchanged prefix
    /// is read from cache at ~10% of input price. Hit rate depends on that prefix staying byte-stable:
    /// an edit, or an archival that drops an earlier fragment, invalidates it for that turn (it
    /// re-caches). Below the provider's minimum cacheable size the breakpoint is simply a no-op.
    /// </summary>
    private static object[] BuildApiMessages(PromptRequest request)
    {
        var built = BuildMessages(request);
        var breakpoint = built.Count - 2; // second-to-last: everything except the volatile final message

        return built
            .Select((m, i) => i == breakpoint
                ? (object)new { role = m.role, content = new object[] { new { type = "text", text = m.content, cache_control = new { type = "ephemeral" } } } }
                : new { role = m.role, content = m.content })
            .ToArray();
    }

    private static List<(string role, string content)> BuildMessages(PromptRequest request)
    {
        var result = new List<(string role, string content)>();

        foreach (var message in request.Messages)
        {
            var role = message.Role == "assistant" ? "assistant" : "user";

            if (result.Count > 0 && result[^1].role == role)
            {
                result[^1] = (role, result[^1].content + "\n\n" + message.Content);
            }
            else
            {
                result.Add((role, message.Content));
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts the assistant text from a Messages API result: the top-level <c>content</c> array
    /// holds typed blocks (thinking, text, ...); the user-visible answer is the concatenation of the
    /// <c>text</c> blocks' <c>text</c> fields.
    /// </summary>
    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("API returned no content.");
        }

        var text = new StringBuilder();

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type)
                && type.GetString() == "text"
                && block.TryGetProperty("text", out var blockText))
            {
                text.Append(blockText.GetString());
            }
        }

        return text.Length == 0
            ? throw new InvalidOperationException("API returned no output text.")
            : text.ToString();
    }

    /// <summary>
    /// Extracts the model's thinking summary, if present. <c>thinking</c> blocks in the top-level
    /// <c>content</c> array carry the summarized reasoning as their <c>thinking</c> field. Returns an
    /// empty string when the model emits none.
    /// </summary>
    private static string ExtractThinking(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type)
                && type.GetString() == "thinking"
                && block.TryGetProperty("thinking", out var blockText))
            {
                if (text.Length > 0)
                {
                    text.Append("\n\n");
                }

                text.Append(blockText.GetString());
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Disposes the underlying HTTP client if it is disposable
    /// </summary>
    public void Dispose() => (client as IDisposable)?.Dispose();
}
