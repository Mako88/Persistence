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
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  [PARSE ERROR] Failed to parse action envelope: {ex.Message}");
                Console.Error.WriteLine($"  [PARSE ERROR] Raw response (first 500 chars): {Truncate(rawResponse, 500)}");
                Console.ResetColor();
                throw new InvalidOperationException(
                    $"Action envelope parse failed in strict mode: {ex.Message}", ex);
            }
        }

        // Fallback: treat the whole response as a plain reply with no actions
        if (StrictMode)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine($"  [PARSE WARN] Response was not a valid action envelope. Treating as plain reply.");
            Console.Error.WriteLine($"  [PARSE WARN] Raw response (first 500 chars): {Truncate(rawResponse, 500)}");
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    [GeneratedRegex(@"```(?:json)?\s*(\{[\s\S]*\})\s*```", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRegex();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}
