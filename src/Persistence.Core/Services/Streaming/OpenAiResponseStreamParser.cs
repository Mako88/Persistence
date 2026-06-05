using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Persistence.Services.Streaming;

/// <summary>
/// Translates an OpenAI Responses API SSE stream into <see cref="ModelStreamEvent"/>s.
/// Consumes the decoded <c>data:</c> payloads produced by SimpleHttpClient's
/// <c>ReadStreamAsync</c> (SSE framing already stripped). Each payload is a JSON object
/// carrying a "type" discriminator (e.g. "response.output_text.delta") and, for deltas,
/// a "delta" string. The <c>[DONE]</c> sentinel and unrecognised or malformed payloads
/// are skipped.
/// </summary>
public static class OpenAiResponseStreamParser
{
    public static async IAsyncEnumerable<ModelStreamEvent> ParseAsync(
        IAsyncEnumerable<string> dataPayloads, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var data in dataPayloads.WithCancellation(ct))
        {
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
            {
                continue;
            }

            if (Map(data) is { } evt)
            {
                yield return evt;
            }
        }
    }

    private static ModelStreamEvent? Map(string data)
    {
        string? type;
        string? delta;

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            delta = root.TryGetProperty("delta", out var d) ? d.GetString() : null;
        }
        catch (JsonException)
        {
            return null; // malformed event payload — skip
        }

        return type switch
        {
            "response.output_text.delta" => ModelStreamEvent.OutputText(delta ?? string.Empty),
            "response.reasoning_summary_text.delta" => ModelStreamEvent.ReasoningSummary(delta ?? string.Empty),
            "response.completed" => ModelStreamEvent.Completed(),
            _ => null,
        };
    }
}
