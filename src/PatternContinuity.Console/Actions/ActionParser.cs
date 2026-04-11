using System.Text.Json;
using System.Text.RegularExpressions;

namespace PatternContinuity.Actions;

public static partial class ActionParser
{
    /// <summary>
    /// If true, malformed JSON envelopes will log a warning to stderr and throw
    /// instead of silently falling back to a plain reply.
    /// </summary>
    public static bool StrictMode { get; set; }

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
        catch (JsonException ex)
        {
            if (StrictMode)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.Error.WriteLine($"  [PARSE WARN] Failed to parse action envelope: {ex.Message}");
                Console.ResetColor();
            }

            // Attempt to salvage assistant_reply from truncated JSON
            var salvaged = TrySalvageReply(json);
            if (salvaged != null)
            {
                if (StrictMode)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Error.WriteLine("  [PARSE WARN] Salvaged assistant_reply from truncated response. Actions dropped.");
                    Console.ResetColor();
                }
                return new ActionEnvelope
                {
                    AssistantReply = salvaged,
                    Actions = []
                };
            }
        }

        // Fallback: treat the whole response as a plain reply with no actions
        if (StrictMode)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Error.WriteLine($"  [PARSE WARN] Response was not a valid action envelope. Treating as plain reply.");
            Console.ResetColor();
        }

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

    /// <summary>
    /// Attempts to extract assistant_reply from truncated JSON.
    /// Looks for "assistant_reply":"..." and extracts the string value.
    /// </summary>
    private static string? TrySalvageReply(string json)
    {
        var match = AssistantReplyRegex().Match(json);
        if (match.Success)
            return match.Groups[1].Value;
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    [GeneratedRegex(@"```(?:json)?\s*(\{[\s\S]*\})\s*```", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRegex();

    [GeneratedRegex(@"""assistant_reply""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Singleline)]
    private static partial Regex AssistantReplyRegex();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}
