using System.Text.Json;
using System.Text.RegularExpressions;

namespace PatternContinuity.Actions;

public static partial class ActionParser
{
    /// <summary>
    /// Parses the model's raw response text into an ActionEnvelope.
    /// Handles both clean JSON and JSON wrapped in markdown code blocks.
    /// </summary>
    public static ActionEnvelope Parse(string rawResponse)
    {
        var json = ExtractJson(rawResponse);

        try
        {
            var envelope = JsonSerializer.Deserialize<ActionEnvelope>(json, SerializerOptions);
            if (envelope != null)
                return envelope;
        }
        catch (JsonException)
        {
            // Fall through to fallback
        }

        // Fallback: treat the whole response as a plain reply with no actions
        return new ActionEnvelope
        {
            AssistantReply = rawResponse,
            Actions = []
        };
    }

    private static string ExtractJson(string raw)
    {
        raw = raw.Trim();

        // Strip markdown code fences if present
        var match = JsonFenceRegex().Match(raw);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        // If it starts with {, assume it's raw JSON
        if (raw.StartsWith('{'))
            return raw;

        return raw;
    }

    [GeneratedRegex(@"```(?:json)?\s*(\{[\s\S]*\})\s*```", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRegex();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}
