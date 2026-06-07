using System.Text.Json.Nodes;

namespace Persistence.Runtime.ActionHandlers;

/// <summary>
/// Shared extraction for action payloads that carry free text — used by the respond and think
/// handlers, whose <c>data</c> may be either a bare string or an object with a <c>"text"</c> property.
/// </summary>
public static class TextPayload
{
    /// <summary>
    /// Extracts the text from a payload that is either a plain string value or an object with a
    /// <c>"text"</c> property. Returns null if neither shape is present.
    /// </summary>
    public static string? Extract(JsonNode? data)
    {
        if (data is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return data?["text"]?.GetValue<string>();
    }
}
