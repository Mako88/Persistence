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

    /// <summary>
    /// Optional hook (set on read-only panes): invoked with the cursor's line index when the user
    /// "activates" a line (Enter, or a left-click — which moves the cursor first). Lets a pane make
    /// its lines interactive, e.g. expand/collapse an entry.
    /// </summary>
    public Action<int>? OnLineActivated { get; set; }

    public override bool ProcessKey(KeyEvent kb)
    {
        // Shift+F10 opens the context menu, and the base class mis-places it exactly as the right-click
        // path does — route it through the same screen-anchored open, from the cursor.
        if (kb.Key == ContextMenu.Key)
        {
            ShowContextMenuAt(CursorPosition.X - LeftColumn, CursorPosition.Y - TopRow);
            return true;
        }

        // A printable keystroke on a read-only pane is redirected to the input (carrying the char).
        if (ReadOnly && OnPrintableInput is not null && IsPrintable(kb))
        {
            OnPrintableInput(((char)kb.KeyValue).ToString());
            return true;
        }

        // Enter activates the current line (e.g. toggles an entry) when a handler is wired.
        if (kb.Key == Key.Enter && OnLineActivated is not null)
        {
            OnLineActivated(CurrentRow);
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

    /// <summary>
    /// Lines moved per mouse-wheel notch. Terminal.Gui's default of one line is painfully slow for long
    /// panes, and a small fixed step still crawls once a pane is tall — so scale the step to the pane
    /// (about a third of a screenful per notch), clamped so a short pane still moves usefully and a tall
    /// one never jumps so far that you lose your place.
    /// </summary>
    private int WheelLines => Math.Clamp(Bounds.Height / 3, 3, 12);

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

        // Right-click: anchor the context menu to this pane (see ShowContextMenuAt) rather than letting
        // the base class mis-place it. Matched by equality, as TextView itself does.
        if (ev.Flags == ContextMenu.MouseFlags)
        {
            ShowContextMenuAt(ev.X, ev.Y);
            return true;
        }

        // A left-click activates the clicked line (Enter-equivalent): let the base move the cursor to
        // the click first, then report the now-current line.
        if (ev.Flags.HasFlag(MouseFlags.Button1Clicked) && OnLineActivated is not null)
        {
            base.MouseEvent(ev);
            OnLineActivated(CurrentRow);
            return true;
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

    #region Context menu

    /// <summary>
    /// Opens the built-in context menu (cut/copy/paste/select-all) anchored to <em>this</em> pane.
    ///
    /// Terminal.Gui positions a <see cref="ContextMenu"/> in <b>screen</b> coordinates, but TextView sets
    /// <c>ContextMenu.Position</c> from the <b>view-relative</b> click (and, for Shift+F10, the
    /// view-relative cursor). The two only agree for a pane at the screen origin — so right-clicking any
    /// pane in the right-hand column popped the menu up over the conversation pane instead. Converting to
    /// screen coordinates first puts it under the pointer, in whichever pane was clicked.
    /// </summary>
    private void ShowContextMenuAt(int col, int row)
    {
        var screen = ViewToScreen(col, row);

        // The +2 nudge matches TextView's own placement, so the menu sits just off the click rather than
        // under the pointer. Show() clamps to the screen, so a pane at the right edge is still fine.
        ContextMenu.Position = new Point(screen.X + 2, screen.Y + 2);
        ContextMenu.Show();
    }

    /// <summary>
    /// Converts a view-relative position to screen coordinates by summing frame offsets up the SuperView
    /// chain. Mirrors Terminal.Gui's own <c>View.ViewToScreen</c>, which is <c>internal</c> and so out of
    /// reach for a subclass in another assembly.
    /// </summary>
    private Point ViewToScreen(int col, int row)
    {
        var x = col + Frame.X;
        var y = row + Frame.Y;

        for (var parent = SuperView; parent is not null; parent = parent.SuperView)
        {
            x += parent.Frame.X;
            y += parent.Frame.Y;
        }

        return new Point(x, y);
    }

    #endregion

    #region Rendering

    // Terminal.Gui asks for the colour one character at a time, handing us the row's runes and a column
    // index. Rebuilding the row's text on every one of those calls makes drawing a row O(chars²) — and a
    // full-pane redraw O(rows × chars²), which is the dominant cost when scrolling a long pane. Since
    // Redraw walks a row's columns contiguously (and hands us the same List<Rune> throughout), memoising
    // just the row being drawn collapses that back to one text build per row. Cleared each draw pass, so
    // a row whose content changed between passes is never served stale colours.
    private List<System.Rune>? drawingLine;
    private Color?[]? drawingLineColors;

    public override void Redraw(Rect bounds)
    {
        drawingLine = null;
        drawingLineColors = null;
        base.Redraw(bounds);
    }

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
        // The row currently being drawn — the overwhelmingly common case, one lookup per character.
        if (ReferenceEquals(line, drawingLine) && drawingLineColors is not null)
        {
            return drawingLineColors;
        }

        var text = string.Concat(line.Select(r => r.ToString()));

        if (cache.TryGetValue(text, out var cached))
        {
            drawingLine = line;
            drawingLineColors = cached;
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
        drawingLine = line;
        drawingLineColors = colors;
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
