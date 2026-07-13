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
/// Client for the OpenAI-compatible Chat Completions API (<c>/chat/completions</c>) — the de-facto
/// standard exposed by local servers like llama.cpp, Ollama, LM Studio, and vLLM. Distinct from
/// <see cref="OpenAiModelClient"/>, which talks the newer Responses API (<c>/responses</c>): the two
/// have different request/response shapes, so they're separate clients (point this one at a local
/// server via <c>ApiBaseUrl</c>, e.g. <c>http://localhost:8080/v1</c>).
/// </summary>
[Service(registerAsType: typeof(IModelClient), key: ModelProvider.OpenAiChat)]
public class OpenAiChatModelClient : IModelClient, IDisposable
{
    private readonly ISimpleClient client;
    private readonly string model;
    private readonly int maxTokens;
    private readonly IAppConfig config;
    private readonly IDisplayProvider display;

    private const string DefaultBaseUrl = "https://api.openai.com/v1";
    private const string PlaceholderApiKey = "YOUR_API_KEY_HERE";

    /// <inheritdoc />
    public ModelUsage? LastUsage { get; private set; }

    /// <summary>
    /// Constructor that builds the HTTP client from config
    /// </summary>
    public OpenAiChatModelClient(IAppConfig config, IDisplayProvider display)
        : this(config, display, CreateClient(config))
    {
    }

    /// <summary>
    /// Test seam: accepts a pre-configured <see cref="ISimpleClient"/> (e.g. a fake) instead of
    /// constructing one from config.
    /// </summary>
    internal OpenAiChatModelClient(IAppConfig config, IDisplayProvider display, ISimpleClient client)
    {
        model = config.Model;
        maxTokens = config.MaxOutputTokens;

        this.client = client;
        this.config = config;
        this.display = display;
    }

    private static ISimpleClient CreateClient(IAppConfig config)
    {
        // Same precondition as the Responses client: the default (real OpenAI) endpoint needs a key;
        // a custom ApiBaseUrl (e.g. a local llama.cpp server) is exempt — those are usually keyless.
        if (string.IsNullOrEmpty(config.ApiBaseUrl)
            && (string.IsNullOrWhiteSpace(config.ApiKey) || config.ApiKey == PlaceholderApiKey))
        {
            var detail = config.ApiKey == PlaceholderApiKey
                ? " (it's still the persistence.template.json placeholder)"
                : "";

            throw new InvalidOperationException(
                $"OpenAiChat provider is selected against the default endpoint but no API key is set{detail}. "
                + "Add your real key to persistence.json (\"ApiKey\"), set PERSISTENCE_APIKEY, or point "
                + "ApiBaseUrl at a local OpenAI-compatible server.");
        }

        var baseUrl = config.ApiBaseUrl ?? DefaultBaseUrl;
        var client = new SimpleClient(baseUrl.TrimEnd('/'));
        client.DefaultHeaders["Authorization"] = $"Bearer {config.ApiKey}";
        // Slow local models can spend a long time ingesting a large prompt before the first byte;
        // a generous timeout keeps the request from being cancelled mid-think.
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
        var responseMessage = ExtractContent(doc.RootElement);

        LastUsage = ReadUsage(doc.RootElement);

        if (config.DebugMode)
        {
            display.ShowDebugInfo($"Response:\n{responseMessage}\n");
        }

        return responseMessage;
    }

    /// <summary>
    /// Streams the response. Chat Completions can stream token deltas, but this client yields the
    /// completed text as a single output-text event followed by completion — enough for the turn
    /// pipeline; live token deltas are a future enhancement.
    /// </summary>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        PromptRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await CompleteAsync(request, ct);

        yield return ModelStreamEvent.OutputText(response);
        yield return ModelStreamEvent.Completed();
    }

    /// <summary>
    /// Reads the Chat Completions usage block (<c>usage.prompt_tokens</c> / <c>completion_tokens</c>),
    /// splitting out the cached prefix (<c>prompt_tokens_details.cached_tokens</c>) so cached input is
    /// billed at the discounted rate. Null when the provider omitted usage.
    /// </summary>
    private static ModelUsage? ReadUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage)
            && usage.TryGetProperty("prompt_tokens", out var input) && input.TryGetInt32(out var inTok))
        {
            var outTok = usage.TryGetProperty("completion_tokens", out var o) && o.TryGetInt32(out var ot) ? ot : 0;

            // prompt_tokens is the TOTAL (cached + uncached); cached is the auto-cached prefix. Split so
            // InputTokens is uncached (full rate) and CacheReadTokens is cached (discounted). No separate
            // cache-creation charge on OpenAI.
            var cached = usage.TryGetProperty("prompt_tokens_details", out var details)
                && details.TryGetProperty("cached_tokens", out var c) && c.TryGetInt32(out var cTok) ? cTok : 0;

            return new ModelUsage(Math.Max(0, inTok - cached), outTok, CacheReadTokens: cached);
        }

        return null;
    }

    /// <summary>
    /// Builds the Chat Completions request body. Local chat templates (e.g. Qwen via llama.cpp)
    /// are strict — they require a single system message at the very start and don't tolerate the
    /// system segments our prompt injects at the END (format instructions + sensory block). So we
    /// flatten to at most two messages: a leading <c>system</c> message (the persona/identity head)
    /// and one <c>user</c> message carrying everything else, each part role-labelled inline so
    /// attribution survives and the end-positioned format instructions stay last.
    /// </summary>
    private SimpleRequest BuildApiRequest(PromptRequest request)
    {
        var built = BuildChatMessages(request);

        // Project to anonymous objects so the serializer emits {"role":..,"content":..}
        // (a ValueTuple would serialize as Item1/Item2).
        var messages = built.Select(m => new { role = m.role, content = m.content }).ToArray();

        var body = new
        {
            model,
            messages,
            max_tokens = maxTokens,
            stream = false,
        };

        if (config.DebugMode)
        {
            // Show the logical prompt segments (each already carries its own fragment header / [Sensory]
            // marker) rather than the flattened two-message body. The flatten adds inline [role]
            // attribution labels for the model's benefit; those are noise in the debug view, so we
            // render the pre-flatten segments here instead.
            var debugContent = string.Join("\n\n", request.Messages.Select(m => m.Content));
            display.ShowDebugInfo($"Request:\n{debugContent}");
        }

        return new SimpleRequest("/chat/completions", HttpMethod.Post, body);
    }

    /// <summary>
    /// Collapses the prompt into a strict-template-safe shape: an optional leading system message
    /// (only if the first segment is a system/developer one), then a single user message containing
    /// the remaining segments concatenated with inline role labels.
    /// </summary>
    private static (string role, string content)[] BuildChatMessages(PromptRequest request)
    {
        var msgs = request.Messages;
        var result = new List<(string role, string content)>();
        var start = 0;

        if (msgs.Count > 0 && IsSystem(msgs[0].Role))
        {
            result.Add(("system", msgs[0].Content));
            start = 1;
        }

        if (start < msgs.Count)
        {
            var sb = new System.Text.StringBuilder();

            for (var i = start; i < msgs.Count; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append("\n\n");
                }

                var label = IsSystem(msgs[i].Role) ? "system" : msgs[i].Role;
                sb.Append($"[{label}]\n{msgs[i].Content}");
            }

            result.Add(("user", sb.ToString()));
        }

        return [.. result];
    }

    private static bool IsSystem(string role) => role is "developer" or "system";

    /// <summary>
    /// Extracts the assistant text from a Chat Completions result: <c>choices[0].message.content</c>.
    /// </summary>
    private static string ExtractContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("API returned no choices.");
        }

        var first = choices[0];

        if (!first.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("API returned no message content.");
        }

        return content.GetString() ?? string.Empty;
    }

    /// <summary>
    /// Disposes the underlying HTTP client if it is disposable
    /// </summary>
    public void Dispose() => (client as IDisposable)?.Dispose();
}
