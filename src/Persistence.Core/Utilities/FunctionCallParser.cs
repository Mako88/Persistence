using System.Text;
using System.Text.Json.Nodes;

namespace Persistence.Utilities;

/// <summary>
/// Parses a block of named-argument function calls into the same JSON command-array shape
/// that <see cref="CommandParser"/> consumes — so the existing command handlers need no
/// changes. Each call <c>name(field=value, ...)</c> becomes <c>{ "name": { field: value } }</c>.
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
    /// <summary>
    /// Parses zero or more whitespace/newline-separated function calls into a JSON array of
    /// single-property command objects. Returns an empty array for empty input.
    /// </summary>
    public static JsonArray Parse(string input)
    {
        var calls = new JsonArray();
        var pos = 0;

        SkipTrivia(input, ref pos);
        while (pos < input.Length)
        {
            var name = ReadIdentifier(input, ref pos);
            if (name.Length == 0)
            {
                throw new FormatException($"Expected a command name at position {pos}.");
            }

            Expect(input, ref pos, '(');
            var fields = ReadArgs(input, ref pos);
            Expect(input, ref pos, ')');

            calls.Add(new JsonObject { [name] = fields });

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
                throw new FormatException($"Expected an argument name at position {pos}.");
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
            throw new FormatException("Unexpected end of input while reading a value.");
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
            throw new FormatException("Unterminated triple-quoted string.");
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

        throw new FormatException("Unterminated string.");
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
            throw new FormatException($"Empty value at position {start}.");
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
            var found = pos < s.Length ? s[pos].ToString() : "end of input";
            throw new FormatException($"Expected '{c}' at position {pos} but found {found}.");
        }

        pos++;
    }

    #endregion
}
