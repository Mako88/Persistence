namespace Persistence.Services;

/// <summary>
/// Cuts a long query into ordered chunks covering all of it (docs/design/chunked-query-embedding.md, "Split" and
/// "Overlap"). Spans are character offsets <c>[Start, End)</c> in the original message, so every chunk, piece and gap
/// can be reported without quoting the text.
/// </summary>
public static class QueryChunker
{
    /// <summary>
    /// Ordered chunks of at most <paramref name="budget"/> characters, no gaps. Each is cut at the last paragraph break,
    /// else line break, else sentence end, else space before the budget, in the chunk's second half; a hard cut only when
    /// there is none there. Each chunk
    /// after the first starts up to <paramref name="overlap"/> characters before the previous one's end, moved forward
    /// to the nearest sentence end, else space, so it does not start mid-word; when that alignment would lose the
    /// overlap, at the numeric overlap point instead. Each chunk ends strictly after the previous one (acceptance 1).
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> Split(string text, int budget, int overlap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, 1);
        overlap = Math.Clamp(overlap, 0, budget / 4);
        var spans = new List<(int Start, int End)>();
        if (text.Length == 0)
        {
            return spans;
        }

        var start = 0;
        var previousEnd = 0;
        while (true)
        {
            // The next end must pass the previous one: a break at or before it would make no progress.
            // Breaks are looked for in the chunk's second half: a paragraph break ten characters in would otherwise make a
            // ten-character chunk, which says nothing and spends a piece of the cap.
            var end = text.Length - start <= budget ? text.Length : Cut(text, Math.Max(start + budget / 2, previousEnd), start + budget);
            spans.Add((start, end));
            if (end == text.Length)
            {
                return spans;
            }

            previousEnd = end;
            start = overlap == 0 ? end : OverlapStart(text, start, end, overlap);
        }
    }

    /// <summary>
    /// A refused piece <c>[start, end)</c> split in two at a boundary near its middle (Arden: never cut to its tail), or
    /// null when either half would be under <paramref name="floor"/> characters.
    /// </summary>
    public static ((int Start, int End) Head, (int Start, int End) Tail)? Halve(string text, int start, int end, int floor)
    {
        var middle = start + (end - start) / 2;
        // A boundary in the middle half, nearest the middle, else the middle itself.
        var low = start + (end - start) / 4;
        var cut = Cut(text, low, middle);
        if (cut == middle && Boundary(text, middle) == 0)
        {
            var later = Enumerable.Range(middle + 1, Math.Max(0, end - (end - start) / 4 - middle - 1)).FirstOrDefault(p => Boundary(text, p) > 0);
            cut = later > 0 ? later : middle;
        }

        return cut - start < floor || end - cut < floor ? null : ((start, cut), (cut, end));
    }

    /// <summary>
    /// The best cut in <c>(after, limit]</c>: the last paragraph break, else line break, else sentence end, else space;
    /// <paramref name="limit"/> itself when there is none.
    /// </summary>
    private static int Cut(string text, int after, int limit)
    {
        for (var kind = 4; kind >= 1; kind--)
        {
            for (var p = limit; p > after; p--)
            {
                if (Boundary(text, p) == kind)
                {
                    return p;
                }
            }
        }

        return limit;
    }

    /// <summary>
    /// The start of the chunk after <c>[start, end)</c>: <paramref name="overlap"/> characters back from
    /// <paramref name="end"/>, moved forward to the nearest sentence end, else space, inside the previous chunk; the
    /// numeric point when neither keeps it after <paramref name="start"/> and before <paramref name="end"/>.
    /// </summary>
    private static int OverlapStart(string text, int start, int end, int overlap)
    {
        var target = Math.Max(start + 1, end - overlap);
        for (var kind = 2; kind >= 1; kind--)
        {
            for (var p = target; p < end; p++)
            {
                if (Boundary(text, p) >= kind && Boundary(text, p) <= 2)
                {
                    return p;
                }
            }
        }

        return target;
    }

    /// <summary>
    /// What kind of boundary a cut before position <paramref name="p"/> falls on: 4 a paragraph break, 3 a line break,
    /// 2 a sentence end, 1 a space, 0 none. The cut is after the break, so the next chunk starts with text.
    /// </summary>
    private static int Boundary(string text, int p)
    {
        if (p <= 0 || p >= text.Length)
        {
            return 0;
        }

        var before = text[p - 1];
        if (before == '\n')
        {
            return p >= 2 && text[p - 2] == '\n' ? 4 : 3;
        }

        if (before == ' ')
        {
            return p >= 2 && text[p - 2] is '.' or '!' or '?' ? 2 : 1;
        }

        return 0;
    }
}
