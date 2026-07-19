using System.Text;
using System.Text.Json;

namespace Persistence.Services;

/// <summary>
/// The wire shape of the OpenAI-compatible Chat Completions API (<c>/chat/completions</c>), shared by
/// every client that speaks it — <see cref="OpenAiChatModelClient"/> (OpenAI's endpoint or a local
/// llama.cpp / Ollama / LM Studio server) and <see cref="OpenRouterModelClient"/>.
///
/// Pure functions over the request/response bodies, deliberately holding no HTTP client, config or
/// state: what differs between those clients is endpoint, auth headers, and a few body fields — not how
/// a prompt becomes messages or how a reply is read back. Keeping the shape here means a fix to the
/// flattening or the usage split lands for every such provider at once.
/// </summary>
internal static class ChatCompletionsProtocol
{
    /// <summary>
    /// Collapses the prompt into a strict-template-safe shape: an optional leading system message (only
    /// if the first segment is a system/developer one), then a single user message carrying the rest,
    /// each part role-labelled inline so attribution survives.
    ///
    /// <para>The flattening exists because chat templates are often strict — they want one system message
    /// at the very start and don't tolerate the system segments Persistence appends at the <em>end</em>
    /// (format instructions + sensory block). Rather than guess which of the many models behind a router
    /// are strict, every Chat Completions client sends the shape that works everywhere; the inline
    /// <c>[role]</c> labels keep attribution legible and the end-positioned format instructions stay
    /// last, which is what makes the model follow them.</para>
    /// </summary>
    public static (string Role, string Content)[] BuildMessages(PromptRequest request)
    {
        var msgs = request.Messages;
        var result = new List<(string Role, string Content)>();
        var start = 0;

        if (msgs.Count > 0 && IsSystem(msgs[0].Role))
        {
            result.Add(("system", msgs[0].Content));
            start = 1;
        }

        if (start < msgs.Count)
        {
            var sb = new StringBuilder();

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
    /// Projects the flattened messages to the anonymous objects the serializer emits as
    /// <c>{"role":…,"content":…}</c> (a ValueTuple would serialize as Item1/Item2).
    /// </summary>
    public static object[] ToWireMessages(PromptRequest request) =>
        BuildMessages(request).Select(m => new { role = m.Role, content = m.Content }).Cast<object>().ToArray();

    /// <summary>
    /// Extracts the assistant text from a result: <c>choices[0].message.content</c>.
    /// </summary>
    public static string ExtractContent(JsonElement root)
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
    /// Reads why generation stopped from <c>choices[0].finish_reason</c> — the chat-completions
    /// spelling of a stop reason (<c>stop</c>, <c>length</c>, <c>tool_calls</c>). Null when absent.
    /// Returned verbatim; interpretation is <see cref="ModelStopReason"/>'s job.
    /// </summary>
    public static string? ReadFinishReason(JsonElement root) =>
        root.TryGetProperty("choices", out var choices)
        && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
        && choices[0].TryGetProperty("finish_reason", out var finish)
        && finish.ValueKind == JsonValueKind.String
            ? finish.GetString()
            : null;

    /// <summary>
    /// Reads the usage block (<c>usage.prompt_tokens</c> / <c>completion_tokens</c>), splitting out the
    /// cached prefix (<c>prompt_tokens_details.cached_tokens</c>) so cached input can be billed at the
    /// discounted rate: <see cref="ModelUsage.InputTokens"/> is the uncached remainder and
    /// <see cref="ModelUsage.CacheReadTokens"/> the cached part. Null when the provider omitted usage.
    /// </summary>
    public static ModelUsage? ReadUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage)
            && usage.TryGetProperty("prompt_tokens", out var input) && input.TryGetInt32(out var inTok))
        {
            var outTok = usage.TryGetProperty("completion_tokens", out var o) && o.TryGetInt32(out var ot) ? ot : 0;

            var cached = usage.TryGetProperty("prompt_tokens_details", out var details)
                && details.TryGetProperty("cached_tokens", out var c) && c.TryGetInt32(out var cTok) ? cTok : 0;

            return new ModelUsage(Math.Max(0, inTok - cached), outTok, CacheReadTokens: cached);
        }

        return null;
    }
}
