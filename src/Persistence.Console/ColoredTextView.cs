using System.Text.RegularExpressions;
using Terminal.Gui;

namespace Persistence.Console;

/// <summary>
/// A read-only <see cref="TextView"/> that colors text by content rules, allowing
/// multiple colors within a single line (e.g. a cyan "You:" label, a white body, and a
/// yellow-highlighted id all on one row).
///
/// Terminal.Gui's per-character color hook is content-based: it receives the current
/// line's runes and a column index, but NOT the line number — and under
/// <see cref="TextView.WordWrap"/> the line is the wrapped fragment. So coloring is
/// expressed as rules over line text rather than absolute document offsets. Each rule
/// maps a line's text to zero or more coloured ranges; rules are applied in registration
/// order and the first rule to claim a column wins. Register the most specific rules
/// (substring/pattern highlights) before broader ones (prefix/line tints) so highlights
/// layer on top.
///
/// Per-line colour maps are computed once and cached, so regex rules cost nothing on redraw.
/// </summary>
internal sealed class ColoredTextView : TextView
{
    private readonly record struct ColorRange(int Start, int Length, Color Color);

    private readonly List<Func<string, IEnumerable<ColorRange>>> rules = [];
    private readonly Dictionary<string, Color?[]> cache = [];

    #region Rule registration

    /// <summary>
    /// Colors a line that begins with <paramref name="prefix"/>: the prefix in one color
    /// and the remainder in another — the common "labelled line" case (e.g. "You: hello").
    /// (Under word-wrap this applies to the first visual row of the message; use
    /// <see cref="ColorLine"/> for a tint that should cover every wrapped row.)
    /// </summary>
    public ColoredTextView ColorPrefix(string prefix, Color prefixColor, Color bodyColor)
    {
        rules.Add(text => text.StartsWith(prefix, StringComparison.Ordinal)
            ?
            [
                new ColorRange(0, prefix.Length, prefixColor),
                new ColorRange(prefix.Length, text.Length - prefix.Length, bodyColor),
            ]
            : []);
        return this;
    }

    /// <summary>Tints a whole line a single color when <paramref name="matches"/> holds.</summary>
    public ColoredTextView ColorLine(Func<string, bool> matches, Color color)
    {
        rules.Add(text => matches(text) ? [new ColorRange(0, text.Length, color)] : []);
        return this;
    }

    /// <summary>Tints a whole line when it starts with <paramref name="prefix"/>.</summary>
    public ColoredTextView ColorLinesStartingWith(string prefix, Color color) =>
        ColorLine(t => t.StartsWith(prefix, StringComparison.Ordinal), color);

    /// <summary>
    /// Colors every literal occurrence of <paramref name="needle"/> anywhere in a line —
    /// the key "multiple colors per line" primitive for scannable highlights.
    /// </summary>
    public ColoredTextView ColorSubstring(string needle, Color color)
    {
        rules.Add(text => Occurrences(text, needle, color));
        return this;
    }

    /// <summary>
    /// Colors every match of <paramref name="pattern"/> anywhere in a line (e.g.
    /// <c>#\d+</c> for ids, <c>\[[^\]]+\]</c> for bracketed tags).
    /// </summary>
    public ColoredTextView ColorPattern(string pattern, Color color)
    {
        var regex = new Regex(pattern, RegexOptions.Compiled);
        rules.Add(text => regex.Matches(text)
            .Where(m => m.Length > 0)
            .Select(m => new ColorRange(m.Index, m.Length, color)));
        return this;
    }

    #endregion

    #region Keyboard

    /// <summary>
    /// Optional hook (set on read-only panes): invoked with a printable character the user typed while
    /// this pane had focus, so the host can redirect it to the compose box. Lets the local peer just
    /// start typing from anywhere and have it land where input is actually accepted.
    /// </summary>
    public Action<string>? OnPrintableInput { get; set; }

    public override bool ProcessKey(KeyEvent kb)
    {
        // A printable keystroke on a read-only pane is redirected to the input (carrying the char).
        if (ReadOnly && OnPrintableInput is not null && IsPrintable(kb))
        {
            OnPrintableInput(((char)kb.KeyValue).ToString());
            return true;
        }

        var handled = base.ProcessKey(kb);

        // Consume plain cursor keys so they only move the cursor / scroll this pane — never shift focus
        // to an adjacent pane. (Pane switching is Ctrl+Left/Right; pane focus is Ctrl+Up/Down.)
        if (kb.Key is Key.CursorLeft or Key.CursorRight or Key.CursorUp or Key.CursorDown)
        {
            return true;
        }

        return handled;
    }

    /// <summary>A plain printable character (a letter/digit/symbol/space), not a chord or special key.</summary>
    private static bool IsPrintable(KeyEvent kb) =>
        !kb.IsCtrl && !kb.IsAlt && kb.KeyValue >= 32 && kb.KeyValue != 127 && kb.KeyValue < 0x10000;

    #endregion

    #region Mouse

    /// <summary>Lines moved per mouse-wheel notch. Terminal.Gui's default of one line is painfully
    /// slow for long panes; a larger step makes the wheel usable.</summary>
    private const int WheelLines = 3;

    public override bool MouseEvent(MouseEvent ev)
    {
        if (ev.Flags.HasFlag(MouseFlags.WheeledDown))
        {
            return ScrollByLines(WheelLines);
        }

        if (ev.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            return ScrollByLines(-WheelLines);
        }

        return base.MouseEvent(ev);
    }

    private bool ScrollByLines(int delta)
    {
        var max = Math.Max(0, Lines - Bounds.Height);
        TopRow = Math.Clamp(TopRow + delta, 0, max);
        SetNeedsDisplay();
        return true;
    }

    #endregion

    #region Rendering

    // Read-only panes draw via SetReadOnlyColor; override the others too so the same
    // rules apply regardless of focus / "used" state.
    protected override void SetNormalColor(List<System.Rune> line, int idx) => Apply(line, idx);
    protected override void SetReadOnlyColor(List<System.Rune> line, int idx) => Apply(line, idx);
    protected override void SetUsedColor(List<System.Rune> line, int idx) => Apply(line, idx);

    private void Apply(List<System.Rune> line, int idx)
    {
        var colors = ColorsFor(line);
        var color = idx >= 0 && idx < colors.Length ? colors[idx] : null;

        Driver.SetAttribute(color is { } c
            ? Driver.MakeAttribute(c, Color.Black)
            : GetNormalColor());
    }

    private Color?[] ColorsFor(List<System.Rune> line)
    {
        var text = string.Concat(line.Select(r => r.ToString()));

        if (cache.TryGetValue(text, out var cached))
        {
            return cached;
        }

        var colors = new Color?[text.Length];

        // First rule to claim a column wins, so only fill cells still unclaimed.
        foreach (var rule in rules)
        {
            foreach (var range in rule(text))
            {
                var end = Math.Min(range.Start + range.Length, colors.Length);
                for (var i = Math.Max(range.Start, 0); i < end; i++)
                {
                    colors[i] ??= range.Color;
                }
            }
        }

        // Bound the cache so a long session can't accumulate unbounded distinct lines.
        if (cache.Count > 4000)
        {
            cache.Clear();
        }

        cache[text] = colors;
        return colors;
    }

    private static IEnumerable<ColorRange> Occurrences(string text, string needle, Color color)
    {
        if (string.IsNullOrEmpty(needle))
        {
            yield break;
        }

        var from = 0;
        while ((from = text.IndexOf(needle, from, StringComparison.Ordinal)) >= 0)
        {
            yield return new ColorRange(from, needle.Length, color);
            from += needle.Length;
        }
    }

    #endregion
}
