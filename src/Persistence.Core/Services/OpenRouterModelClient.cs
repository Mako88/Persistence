using Persistence.Config;
using Persistence.DI;
using Persistence.Runtime;
using Persistence.Services.Streaming;
using SimpleHttpClient;
using SimpleHttpClient.Models;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Persistence.Services;

/// <summary>
/// Client for <see href="https://openrouter.ai">OpenRouter</see> — a router sitting in front of hundreds
/// of models from many vendors, reachable with one key through a Chat Completions-shaped API. The
/// <c>Model</c> is a namespaced route id (<c>z-ai/glm-5.2</c>, <c>anthropic/claude-opus-4.8</c>, …).
///
/// <para>The wire shape is shared with <see cref="OpenAiChatModelClient"/> via
/// <see cref="ChatCompletionsProtocol"/> — this class is only what's genuinely OpenRouter-specific:</para>
/// <list type="bullet">
/// <item>its endpoint, and a key that is always required (a router has no keyless mode, unlike a local
/// llama.cpp server);</item>
/// <item>an <c>X-Title</c> attribution header, which is how OpenRouter labels traffic in its public
/// rankings;</item>
/// <item><b>usage accounting</b> — asking for it returns the call's <em>actual</em> cost in USD rather
/// than leaving us to multiply tokens by a rate table we maintain by hand. That's a real difference:
/// OpenRouter's per-model prices change and it routes across providers, so a local table would drift.
/// Surfaced in the debug pane today; wiring it into the session cost readout is a follow-up (see
/// TODO.md, which already wants exactly this for the other providers).</item>
/// </list>
/// </summary>
[Service(registerAsType: typeof(IModelClient), key: ModelProvider.OpenRouter)]
public class OpenRouterModelClient : IModelClient, IDisposable
{
    private readonly ISimpleClient client;
    private readonly string model;
    private readonly int maxTokens;
    private readonly IAppConfig config;
    private readonly IDisplayProvider display;

    private const string DefaultBaseUrl = "https://openrouter.ai/api/v1";
    private const string PlaceholderApiKey = "YOUR_API_KEY_HERE";

    /// <summary>Identifies this app in OpenRouter's public rankings. Cosmetic; carries nothing private.</summary>
    private const string AppTitle = "Persistence";

    /// <inheritdoc />
    public ModelUsage? LastUsage { get; private set; }

    /// <summary>
    /// The USD cost OpenRouter reported for the most recent call — the real charge, not an estimate —
    /// or null when it reported none. Read right after a call returns (turns are serialized).
    /// </summary>
    public decimal? LastActualCostUsd { get; private set; }

    /// <summary>
    /// Constructor that builds the HTTP client from config
    /// </summary>
    public OpenRouterModelClient(IAppConfig config, IDisplayProvider display)
        : this(config, display, CreateClient(config))
    {
    }

    /// <summary>
    /// Test seam: accepts a pre-configured <see cref="ISimpleClient"/> (e.g. a fake) instead of
    /// constructing one from config.
    /// </summary>
    internal OpenRouterModelClient(IAppConfig config, IDisplayProvider display, ISimpleClient client)
    {
        model = config.Model;
        maxTokens = config.MaxOutputTokens;

        this.client = client;
        this.config = config;
        this.display = display;
    }

    private static ISimpleClient CreateClient(IAppConfig config)
    {
        // Unlike the OpenAI-compatible client, there's no keyless case to exempt: OpenRouter is a hosted
        // router and always authenticates, whatever ApiBaseUrl points at.
        if (string.IsNullOrWhiteSpace(config.ApiKey) || config.ApiKey == PlaceholderApiKey)
        {
            var detail = config.ApiKey == PlaceholderApiKey
                ? " (it's still the placeholder)"
                : "";

            throw new InvalidOperationException(
                $"OpenRouter provider is selected but no API key is set{detail}. Add your OpenRouter key "
                + "(it starts with \"sk-or-\") to this peer's config as \"ApiKey\", or set PERSISTENCE_APIKEY.");
        }

        var baseUrl = config.ApiBaseUrl ?? DefaultBaseUrl;
        var client = new SimpleClient(baseUrl.TrimEnd('/'));
        client.DefaultHeaders["Authorization"] = $"Bearer {config.ApiKey}";
        client.DefaultHeaders["X-Title"] = AppTitle;
        // A router can queue behind a busy upstream provider, so keep the same generous timeout the
        // local-model client uses rather than cancelling a request that's merely waiting its turn.
        client.Timeout = config.RequestTimeoutSeconds;
        return client;
    }

    /// <summary>
    /// Sends a non-streaming chat-completions request and returns the assistant's message text
    /// </summary>
    public async Task<string> CompleteAsync(PromptRequest request, CancellationToken ct = default)
    {
        var apiRequest = BuildApiRequest(request);
        var response = await client.MakeRequest(apiRequest);

        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"API call failed ({response.StatusCode}): {response.StringBody}");
        }

        using var doc = JsonDocument.Parse(response.StringBody);
        var responseMessage = ChatCompletionsProtocol.ExtractContent(doc.RootElement);

        LastActualCostUsd = ReadActualCost(doc.RootElement);
        LastUsage = ChatCompletionsProtocol.ReadUsage(doc.RootElement) is { } u ? u with { ActualCostUsd = LastActualCostUsd } : null;

        if (config.DebugMode)
        {
            var cost = LastActualCostUsd is { } c ? $"\n[OpenRouter reported actual cost: ${c:0.######}]" : "";
            display.ShowDebugInfo($"Response:\n{responseMessage}\n{cost}");
        }

        return responseMessage;
    }

    /// <summary>
    /// Streams the response. Like the Chat Completions client, this yields the completed text as a
    /// single output-text event followed by completion — enough for the turn pipeline; live token
    /// deltas are a future enhancement.
    /// </summary>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        PromptRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await CompleteAsync(request, ct);

        yield return ModelStreamEvent.OutputText(response);
        yield return ModelStreamEvent.Completed();
    }

    /// <summary>
    /// Reads the actual USD charge OpenRouter reports for the call (<c>usage.cost</c>, returned because
    /// the request asks for usage accounting). Null when absent — the caller then has only the token
    /// counts, exactly as with every other provider.
    /// </summary>
    private static decimal? ReadActualCost(JsonElement root) =>
        root.TryGetProperty("usage", out var usage)
        && usage.TryGetProperty("cost", out var cost)
        && cost.ValueKind == JsonValueKind.Number
        && cost.TryGetDecimal(out var value)
            ? value
            : null;

    /// <summary>
    /// Builds the request body. The prompt is flattened to a strict-template-safe shape by
    /// <see cref="ChatCompletionsProtocol.BuildMessages"/> — which matters more here than anywhere
    /// else, since a router fans out to models with wildly different chat templates.
    /// </summary>
    private SimpleRequest BuildApiRequest(PromptRequest request)
    {
        var body = new
        {
            model,
            messages = ChatCompletionsProtocol.ToWireMessages(request),
            max_tokens = maxTokens,
            stream = false,
            // Ask OpenRouter to report what the call actually cost (see LastActualCostUsd).
            usage = new { include = true },
        };

        if (config.DebugMode)
        {
            // Show the logical prompt segments rather than the flattened two-message body — the inline
            // [role] labels the flatten adds are for the model, and are noise when reading the prompt.
            var debugContent = string.Join("\n\n", request.Messages.Select(m => m.Content));
            display.ShowDebugInfo($"Request:\n{debugContent}");
        }

        return new SimpleRequest("/chat/completions", HttpMethod.Post, body);
    }

    /// <summary>
    /// Disposes the underlying HTTP client if it is disposable
    /// </summary>
    public void Dispose() => (client as IDisposable)?.Dispose();
}
