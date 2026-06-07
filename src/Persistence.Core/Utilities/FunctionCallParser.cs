using System.Text;
using System.Text.Json.Nodes;

namespace Persistence.Utilities;

/// <summary>
/// Parses a block of named-argument function calls into the same JSON command-array shape
/// that <see cref="CommandParser"/> consumes — so the existing command handlers need no
/// changes. Each call <c>name(field=value, ...)</c> becomes <c>{ "name": { field: value } }</c>.
///
/// Parsing is resilient: a malformed call does not abort the whole block. The bad call becomes
/// an <c>__error__</c> command carrying a plain-language message and the offending snippet, then
/// parsing resumes at the next line. This lets a peer's valid calls (and its other tags) still
/// run, and gives it a precise, correctable error for the one that failed.
///
/// Supported argument value types:
/// <list type="bullet">
///   <item>numbers — <c>42</c>, <c>0.9</c>, <c>-3</c></item>
///   <item>booleans — <c>true</c>, <c>false</c></item>
///   <item>quoted strings — <c>"text"</c> with <c>\"</c>, <c>\\</c>, <c>\n</c>, <c>\t</c> escapes</item>
///   <item>triple-quoted strings — <c>"""multi-line, unescaped"""</c> (ideal for prose/content)</item>
///   <item>arrays — <c>["a", "b"]</c></item>
///   <item>barewords — unquoted tokens become strings (e.g. an ISO date)</item>
/// </list>
/// </summary>
public static class FunctionCallParser
{
    /// <summary>The command name used for a call that failed to parse.</summary>
    public const string ErrorCommandName = "__error__";

    // Stop emitting error commands after this many failures in one block — past this the input is
    // garbled enough (e.g. an unterminated multi-line string) that more errors just add noise.
    private const int MaxErrors = 5;

    /// <summary>
    /// Parses zero or more whitespace/newline-separated function calls into a JSON array of
    /// single-property command objects. Returns an empty array for empty input. Malformed calls
    /// become <c>__error__</c> command objects rather than throwing.
    /// </summary>
    public static JsonArray Parse(string input)
    {
        var calls = new JsonArray();
        var pos = 0;
        var errors = 0;

        SkipTrivia(input, ref pos);
        while (pos < input.Length)
        {
            var callStart = pos;

            try
            {
                var name = ReadIdentifier(input, ref pos);
                if (name.Length == 0)
                {
                    throw new FormatException("expected a command name");
                }

                Expect(input, ref pos, '(');
                var fields = ReadArgs(input, ref pos);
                Expect(input, ref pos, ')');

                calls.Add(new JsonObject { [name] = fields });
            }
            catch (FormatException ex)
            {
                calls.Add(ErrorCommand(ex.Message, Snippet(input, callStart)));

                if (++errors >= MaxErrors)
                {
                    break;
                }

                SkipToNextCall(input, ref pos, callStart);
            }

            SkipTrivia(input, ref pos);
        }

        return calls;
    }

    #region Parsing pieces

    private static JsonObject ReadArgs(string s, ref int pos)
    {
        var obj = new JsonObject();
        SkipTrivia(s, ref pos);

        while (pos < s.Length && s[pos] != ')')
        {
            var name = ReadIdentifier(s, ref pos);
            if (name.Length == 0)
            {
                throw new FormatException("expected a field name (each argument is name=value)");
            }

            SkipTrivia(s, ref pos);
            Expect(s, ref pos, '=');
            SkipTrivia(s, ref pos);

            obj[name] = ReadValue(s, ref pos);

            SkipTrivia(s, ref pos);
            if (pos < s.Length && s[pos] == ',')
            {
                pos++;
                SkipTrivia(s, ref pos);
            }
        }

        return obj;
    }

    private static JsonNode? ReadValue(string s, ref int pos)
    {
        if (pos >= s.Length)
        {
            throw new FormatException("a field is missing its value (expected name=value)");
        }

        var c = s[pos];

        if (StartsWith(s, pos, "\"\"\""))
        {
            return JsonValue.Create(ReadTripleQuoted(s, ref pos));
        }

        if (c == '"')
        {
            return JsonValue.Create(ReadQuoted(s, ref pos));
        }

        if (c == '[')
        {
            return ReadArray(s, ref pos);
        }

        return ReadBareword(s, ref pos);
    }

    private static JsonArray ReadArray(string s, ref int pos)
    {
        Expect(s, ref pos, '[');
        var array = new JsonArray();
        SkipTrivia(s, ref pos);

        while (pos < s.Length && s[pos] != ']')
        {
            array.Add(ReadValue(s, ref pos));
            SkipTrivia(s, ref pos);
            if (pos < s.Length && s[pos] == ',')
            {
                pos++;
                SkipTrivia(s, ref pos);
            }
        }

        Expect(s, ref pos, ']');
        return array;
    }

    private static string ReadTripleQuoted(string s, ref int pos)
    {
        pos += 3; // opening """
        var end = s.IndexOf("\"\"\"", pos, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new FormatException("unterminated triple-quoted string (missing closing \"\"\")");
        }

        var value = s[pos..end];
        pos = end + 3;

        // A leading/trailing newline right inside the delimiters is framing, not content.
        return value.Trim('\r', '\n');
    }

    private static string ReadQuoted(string s, ref int pos)
    {
        pos++; // opening "
        var sb = new StringBuilder();

        while (pos < s.Length)
        {
            var c = s[pos++];

            if (c == '\\' && pos < s.Length)
            {
                var esc = s[pos++];
                sb.Append(esc switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    _ => esc,
                });
                continue;
            }

            if (c == '"')
            {
                return sb.ToString();
            }

            sb.Append(c);
        }

        throw new FormatException("unterminated string (missing closing \")");
    }

    private static JsonNode? ReadBareword(string s, ref int pos)
    {
        var start = pos;
        while (pos < s.Length && s[pos] is not (',' or ')' or ']'))
        {
            pos++;
        }

        var token = s[start..pos].Trim();

        if (token.Length == 0)
        {
            throw new FormatException("a field is missing its value (expected name=value)");
        }

        if (token.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(true);
        }

        if (token.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(false);
        }

        if (token.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (long.TryParse(token, out _) ||
            double.TryParse(token, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            // Parse as a JSON number node (JsonElement-backed) rather than JsonValue.Create(double),
            // so command fields can read it as float/int/long/double interchangeably — matching how
            // JsonNode.Parse handles numbers in the JSON format. A CLR-typed JsonValue<double> would
            // throw on GetValue<float>().
            return JsonNode.Parse(token);
        }

        // Unquoted, non-numeric token — treat as a string (e.g. a bare ISO date).
        return JsonValue.Create(token);
    }

    #endregion

    #region Helpers

    private static string ReadIdentifier(string s, ref int pos)
    {
        var start = pos;
        while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_'))
        {
            pos++;
        }

        return s[start..pos];
    }

    private static void SkipTrivia(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos]))
        {
            pos++;
        }
    }

    private static bool StartsWith(string s, int pos, string token) =>
        pos + token.Length <= s.Length && s.AsSpan(pos, token.Length).SequenceEqual(token);

    private static void Expect(string s, ref int pos, char c)
    {
        if (pos >= s.Length || s[pos] != c)
        {
            var found = pos < s.Length ? $"'{s[pos]}'" : "end of input";
            var hint = c == '=' ? " (arguments are name=value, not name:value)" : "";
            throw new FormatException($"expected '{c}' but found {found}{hint}");
        }

        pos++;
    }

    /// <summary>
    /// Builds the synthetic command emitted for a call that failed to parse: an
    /// <c>__error__</c> object carrying a plain-language message and the offending text.
    /// </summary>
    private static JsonObject ErrorCommand(string message, string snippet) => new()
    {
        [ErrorCommandName] = new JsonObject
        {
            ["message"] = message,
            ["text"] = snippet,
        },
    };

    /// <summary>
    /// Returns the offending call's text (its first line, trimmed and length-capped) so the error
    /// can point at exactly what the peer wrote.
    /// </summary>
    private static string Snippet(string s, int start)
    {
        var stop = s.IndexOf('\n', Math.Min(start, s.Length));
        if (stop < 0)
        {
            stop = s.Length;
        }

        var snippet = s[start..stop].Trim();
        return snippet.Length > 80 ? snippet[..80] + "…" : snippet;
    }

    /// <summary>
    /// Advances past a failed call to the next line so parsing can resume. Guarantees forward
    /// progress (always moves beyond <paramref name="callStart"/>) so a stubborn character can't
    /// cause an infinite loop.
    /// </summary>
    private static void SkipToNextCall(string s, ref int pos, int callStart)
    {
        var from = Math.Max(pos, callStart + 1);
        var newline = from < s.Length ? s.IndexOf('\n', from) : -1;
        pos = newline < 0 ? s.Length : newline + 1;
    }

    #endregion
}
