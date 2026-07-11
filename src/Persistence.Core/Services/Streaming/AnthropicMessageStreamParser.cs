using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Persistence.Services.Streaming;

/// <summary>
/// Translates an Anthropic Messages API SSE stream into <see cref="ModelStreamEvent"/>s. Consumes
/// the decoded <c>data:</c> payloads produced by SimpleHttpClient's <c>ReadServerSentEventsAsync</c>
/// (SSE framing already stripped). Each payload is a JSON object with a "type" discriminator; the
/// two we care about are <c>content_block_delta</c> (whose nested <c>delta.type</c> is
/// <c>text_delta</c> for output or <c>thinking_delta</c> for reasoning) and <c>message_stop</c>.
/// Every other event (<c>message_start</c>, <c>content_block_start</c>/<c>_stop</c>,
/// <c>message_delta</c>, <c>ping</c>) and any blank or malformed payload is skipped.
/// </summary>
public static class AnthropicMessageStreamParser
{
    /// <summary>
    /// Yields a model stream event for each recognised payload, skipping blank, unrecognised, or
    /// malformed payloads
    /// </summary>
    public static async IAsyncEnumerable<ModelStreamEvent> ParseAsync(
        IAsyncEnumerable<string> dataPayloads, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var data in dataPayloads.WithCancellation(ct))
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                continue;
            }

            if (Map(data) is { } evt)
            {
                yield return evt;
            }
        }
    }

    /// <summary>
    /// Maps a single JSON payload to a model stream event by its "type" discriminator (and, for
    /// content-block deltas, the nested "delta.type"), returning null for malformed or unrecognised
    /// payloads
    /// </summary>
    private static ModelStreamEvent? Map(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "content_block_delta" when root.TryGetProperty("delta", out var delta):
                    var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;

                    return deltaType switch
                    {
                        "text_delta" => ModelStreamEvent.OutputText(
                            delta.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty),
                        "thinking_delta" => ModelStreamEvent.ReasoningSummary(
                            delta.TryGetProperty("thinking", out var think) ? think.GetString() ?? string.Empty : string.Empty),
                        _ => null,
                    };

                case "message_stop":
                    return ModelStreamEvent.Completed();

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null; // malformed event payload — skip
        }
    }
}
