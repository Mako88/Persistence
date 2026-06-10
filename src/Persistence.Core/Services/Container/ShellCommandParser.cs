namespace Persistence.Services.Container;

/// <summary>
/// Minimal, quote-aware parsing of a shell command line for allowlist enforcement: splits it into
/// pipeline/chain segments (on top-level <c>|</c>, <c>||</c>, <c>&amp;&amp;</c>, <c>&amp;</c>,
/// <c>;</c>) and returns the leading program of each, so every entrypoint can be checked against the
/// allowlist. It is NOT a full shell parser — it only needs to identify which programs are invoked.
/// </summary>
internal static class ShellCommandParser
{
    /// <summary>
    /// Returns the leading program token of each top-level segment (empty segments skipped). Quotes
    /// are respected so operators inside quotes don't split, and a quoted program is unwrapped.
    /// </summary>
    public static IReadOnlyList<string> ExtractProgramNames(string commandLine)
    {
        var programs = new List<string>();

        foreach (var segment in SplitSegments(commandLine))
        {
            var program = FirstToken(segment);
            if (!string.IsNullOrEmpty(program))
            {
                programs.Add(program);
            }
        }

        return programs;
    }

    private static IEnumerable<string> SplitSegments(string commandLine)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                current.Append(c);
                continue;
            }

            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                current.Append(c);
                continue;
            }

            if (!inSingle && !inDouble && IsBoundary(commandLine, i, out var width))
            {
                segments.Add(current.ToString());
                current.Clear();
                i += width - 1; // skip the rest of a two-char operator
                continue;
            }

            current.Append(c);
        }

        segments.Add(current.ToString());
        return segments;
    }

    private static bool IsBoundary(string s, int i, out int width)
    {
        var c = s[i];
        var next = i + 1 < s.Length ? s[i + 1] : '\0';

        switch (c)
        {
            case ';':
                width = 1;
                return true;
            case '&':
                width = next == '&' ? 2 : 1; // && (and) or & (background)
                return true;
            case '|':
                width = next == '|' ? 2 : 1; // || (or) or | (pipe)
                return true;
            default:
                width = 0;
                return false;
        }
    }

    private static string FirstToken(string segment)
    {
        var trimmed = segment.TrimStart();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var token = new System.Text.StringBuilder();
        var inSingle = false;
        var inDouble = false;

        foreach (var c in trimmed)
        {
            if (c == '\'' && !inDouble) { inSingle = !inSingle; continue; }
            if (c == '"' && !inSingle) { inDouble = !inDouble; continue; }
            if (char.IsWhiteSpace(c) && !inSingle && !inDouble) { break; }
            token.Append(c);
        }

        return token.ToString();
    }
}
