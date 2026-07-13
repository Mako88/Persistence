using System.Text.RegularExpressions;
using Terminal.Gui;

namespace Persistence.Console;

/// <summary>
/// The single source of truth for TUI colours. The accent palette is deliberately just five hues —
/// dark green, light green, light yellow, light red, light purple — over neutral white (content) and
/// gray (de-emphasis). Several semantic names intentionally share a value so any one can be retuned
/// without touching the others.
/// </summary>
internal static class TuiColors
{
    public const Color Body = Color.White;            // content / values / dates / session / tag lists
    public const Color User = Color.BrightYellow;     // "You:" role label
    public const Color Peer = Color.BrightMagenta;    // "Remote Peer:" role label (light purple)
    public const Color Gold = Color.BrightYellow;     // action names, R/I/C, html-like tags, Pending, sensory block
    public const Color Purple = Color.BrightMagenta;  // Request:/Response:, fragment type, provider, Triggered
    public const Color Label = Color.BrightGreen;     // field/title labels, markers, compose keys, schedule name, Note
    public const Color Bracket = Color.BrightGreen;   // the [ ] around a fragment header
    public const Color TabUnfocused = Color.Green;    // selected-but-unfocused side tab / inactive pane title (dark green)
    public const Color Error = Color.BrightRed;       // error text, "protected"
    public const Color SuggestedTag = Color.BrightGreen; // the suggested tag inside a "did you mean" error
    public const Color Processing = Color.BrightGreen; // status state chip while thinking (idle is gray)
    public const Color Timestamp = Color.BrightGreen; // leading [time] stamps
    public const Color TypeName = Color.BrightMagenta; // a fragment's type name in a header (light purple)
    public const Color LightGreen = Color.BrightGreen; // [WAKE-UP] line, focus highlight
    public const Color Model = Color.BrightYellow;    // model name in the status bar
    public const Color Muted = Color.Gray;            // de-emphasised text ([Queued], cancelled, idle, /exit hint)
    public const Color StatusBg = Color.Black;        // status-bar background
}

/// <summary>
/// Reusable, composable colour-rule sets for the read-only panes — applied as fluent extensions on
/// <see cref="ColoredTextView"/>. Small "detector" helpers (<see cref="Timestamps"/>,
/// <see cref="FragmentHeaders"/>, …) recognise a text pattern and colour it; each pane is then just
/// the set of detectors it wants. So the display provider stays declarative and the regexes/colours
/// live in one place.
///
/// Convention: rules are first-match-wins per column, so register the most specific (or things that
/// should "win") before broader ones.
/// </summary>
internal static class TuiColoring
{
    // --- Shared patterns ---

    /// <summary>A leading timestamp like <c>[06/09/2026 …]</c> — a "[" followed by a digit (so it
    /// doesn't also match fragment headers like <c>[#6 | …]</c>, which start with "#").</summary>
    private const string Timestamp = @"^\[\d[^\]]*\]";

    /// <summary>The Actions collapse marker (▶ collapsed / ▼ expanded) at the start of an entry header.</summary>
    private const string CollapseMarker = @"^[▶▼]";

    /// <summary>A timestamp anywhere on a line — used in the Actions pane, where the entry header is
    /// "▶ [time] command" so the stamp isn't at the start. Still keyed on "[" + digit, so it won't
    /// match a fragment header (which starts "[#…").</summary>
    private const string EntryTimestamp = @"\[\d[^\]]*\]";

    /// <summary>A fragment id like <c>#42</c>, but only inside a header (<c>[#42 | …</c>) — so a bare
    /// "#5" in prose (e.g. a "recent changes" line) isn't mistaken for one.</summary>
    private const string FragmentId = @"(?<=\[)#\d+(?= \|)";

    /// <summary>The opening <c>[</c> of a fragment header — the bracket immediately before <c>#digit</c>.</summary>
    private const string HeaderOpenBracket = @"\[(?=#\d)";

    /// <summary>The closing <c>]</c> of a fragment header — a <c>]</c> preceded by <c>[#digit…</c> on
    /// the same line (so only a real header's bracket matches, not a stray <c>]</c> in prose).</summary>
    private const string HeaderCloseBracket = @"(?<=\[#\d[^\]]*)\]";

    /// <summary>A <c>|</c> separator inside a fragment header (preceded by <c>[#digit…</c> on the line).</summary>
    private const string HeaderPipe = @"(?<=\[#\d[^\]]*)\|";

    /// <summary>A numeric value right after an R:/I:/C: marker (the relevance/importance/confidence number).</summary>
    private const string RicValue = @"(?<=[RIC]:)[\d.]+";

    /// <summary>A fragment's type name — the word right after <c>#42 | </c> in a header.</summary>
    private const string FragmentTypeName = @"(?<=#\d+ \| )[A-Za-z][A-Za-z]+";

    /// <summary>The R:/I:/C: relevance/importance/confidence markers in a header — a letter + colon
    /// immediately before a number (so <c>is_protected</c>, <c>C:\paths</c>, times, etc. don't match).</summary>
    private const string RicMarker = @"[RIC]:(?=\d)";

    /// <summary>The "protected" flag in a *real* header — "protected" following the <c>| </c>
    /// separator on a line that began with a numeric id (<c>[#42 …</c>). This keeps it from colouring
    /// the placeholder example header (<c>[#ID | Type | … | protected]</c>) in the prompt's own
    /// explanation text, where the rest of the header isn't coloured — so the example stays all-white.</summary>
    private const string ProtectedFlag = @"(?<=\[#\d+[^\]]*\| )protected\b";

    /// <summary>"protected" on a header's <em>wrapped continuation</em> line — where the <c>[#42</c>
    /// prefix sits on the previous visual row, so <see cref="ProtectedFlag"/> can't see it. Matches
    /// "protected" before the closing <c>]</c> on a line with no <c>[</c> of its own. (This also
    /// reddens the placeholder example header's wrapped "protected]" — an accepted trade-off so a real
    /// header's "protected" is always red, even when it wraps.)</summary>
    private const string ProtectedFlagWrapped = @"(?<=^[^\[]*)protected(?=\]\s*$)";

    /// <summary>A sensory-block field label (the known set from the prompt's [Sensory] block). Matched
    /// by exact label rather than a generic <c>^word:</c> so prose lines that merely start with a
    /// "word:" (or wrap to look like it) aren't coloured.</summary>
    private const string SensoryLabel =
        @"^(?:Current time \((?:UTC|local)\)|Session|Context(?: budget)?|Time since last prompt|Continue iteration|Recent changes to your memory|Available tags):";

    /// <summary>An html-like tag at the very start of a line, e.g. a leading <c>&lt;respond&gt;</c> or
    /// <c>&lt;continue&gt;</c> — the shape the peer's response tags take.</summary>
    private const string LeadingHtmlTag = @"^\s*</?[A-Za-z][\w-]*>";

    /// <summary>A further html-like tag later on a line that <em>began</em> with one — e.g. the closing
    /// <c>&lt;/continue&gt;</c> in <c>&lt;continue&gt;false&lt;/continue&gt;</c>. Together with
    /// <see cref="LeadingHtmlTag"/> this colours the peer's inline response tags while leaving tags
    /// mentioned mid-sentence in the system prompt (which don't start their line) uncoloured.</summary>
    private const string FollowingHtmlTag = @"(?<=^\s*</?[A-Za-z][\w-]*>[^<]*)</?[A-Za-z][\w-]*>";

    /// <summary>An attribute name immediately before <c>=</c>, e.g. <c>content</c> in <c>content="…"</c>.</summary>
    private const string AttributeName = @"\b[A-Za-z_][A-Za-z0-9_]*(?==)";

    /// <summary>The action name — the first word after the leading timestamp on a header line.</summary>
    private const string ActionName = @"(?<=\] )[A-Za-z_][A-Za-z0-9_]*";

    /// <summary>
    /// The suggested tag inside a "did you mean '…'?" error: the quoted text immediately before the
    /// closing bracket. Keying off the position (rather than "did you mean") means it still matches
    /// when the error wraps and the suggestion lands on the continuation line.
    /// </summary>
    private const string SuggestedTag = @"(?<=')[^']+(?='[?.]?\]\s*$)";

    /// <summary>The quoted suggestion right after "did you mean '" — the inline form used in command
    /// results (e.g. the Actions pane), where the suggestion isn't at the end of the line.</summary>
    private const string SuggestedInline = @"(?<=did you mean ')[^']+(?=')";

    // --- Detector helpers (recognise a pattern, colour it) ---

    private static ColoredTextView Timestamps(this ColoredTextView v) => v.ColorPattern(Timestamp, TuiColors.Timestamp);

    private static ColoredTextView Attributes(this ColoredTextView v) => v.ColorPattern(AttributeName, TuiColors.Label);

    /// <summary>Colours the peer's response tags gold — a tag at the start of a line, plus any further
    /// tags on that same line (so <c>&lt;continue&gt;false&lt;/continue&gt;</c> colours both tags). Tags
    /// mentioned inside system-prompt prose (not at a line start) stay uncoloured.</summary>
    private static ColoredTextView ResponseTags(this ColoredTextView v) => v
        .ColorPattern(LeadingHtmlTag, TuiColors.Gold)
        .ColorPattern(FollowingHtmlTag, TuiColors.Gold);

    /// <summary>Colours the known [Sensory] field labels green (and only those — not arbitrary prose).</summary>
    private static ColoredTextView SensoryLabels(this ColoredTextView v) => v.ColorPattern(SensoryLabel, TuiColors.Label);

    /// <summary>Colours a "did you mean '…'" suggestion green — both the end-of-line error form and the
    /// inline command-result form — so a suggested fix stands out wherever an error offers one.</summary>
    private static ColoredTextView Suggestions(this ColoredTextView v) => v
        .ColorPattern(SuggestedTag, TuiColors.SuggestedTag)
        .ColorPattern(SuggestedInline, TuiColors.SuggestedTag);

    /// <summary>Colours a <c>[#id | Type | R:x I:x C:x | protected]</c> header so the whole thing
    /// stands out: brackets, id, pipes and the R/I/C values green; type light purple; the R/I/C markers
    /// yellow; "protected" red (even when the header wraps). Each part is matched by a position-anchored
    /// pattern, so these colours only land inside an actual header, never in prose. Reusable across panes.</summary>
    private static ColoredTextView FragmentHeaders(this ColoredTextView v) => v
        .ColorPattern(HeaderOpenBracket, TuiColors.Bracket)
        .ColorPattern(HeaderCloseBracket, TuiColors.Bracket)
        .ColorPattern(HeaderPipe, TuiColors.Bracket)
        .ColorPattern(FragmentId, TuiColors.Label)
        .ColorPattern(FragmentTypeName, TuiColors.TypeName)
        .ColorPattern(RicMarker, TuiColors.Gold)
        .ColorPattern(RicValue, TuiColors.Bracket)
        .ColorPattern(ProtectedFlag, TuiColors.Error)          // "protected" stands out in red…
        .ColorPattern(ProtectedFlagWrapped, TuiColors.Error);  // …including on a wrapped header line

    // --- Per-pane schemes ---

    /// <summary>
    /// Conversation: the suggested tag inside an error is claimed bright green first; the whole error
    /// (its first line and any wrapped continuation, detected as a line ending in "]" that isn't a
    /// fresh "[…" marker) reds over the rest. Then the other markers, timestamp, and role labels.
    /// </summary>
    public static ColoredTextView ForConversation(this ColoredTextView v, IReadOnlyCollection<string> humanNames, Color userColor, Color peerColor)
    {
        v.Suggestions()
            .ColorLinesStartingWith("[Error", TuiColors.Error)
            .ColorLinesStartingWith("[WAKE-UP", TuiColors.Gold)
            .ColorLinesStartingWith("[Queued", TuiColors.Muted)
            .ColorSubstring("Unknown command:", TuiColors.Error)        // label red; the command stays white
            // The echoed local slash command (yellow) — anchored to the "[time] Executed " prefix so the
            // word "executed" inside a normal message body is never coloured.
            .ColorPattern(@"(?<=^\[\d[^\]]*\] Executed ).+", TuiColors.Gold)
            .Timestamps();

        // Colour the speaker label at the head of a chat line ("[time] Name:"). Human name(s) → user
        // colour; every other speaker → one shared digital-peer colour. Humans are registered first so,
        // with first-match-wins per column, a human's label wins over the catch-all peer rule below.
        foreach (var name in humanNames)
        {
            v.ColorPattern($@"(?<=^\[\d[^\]]*\] ){Regex.Escape(name)}(?=:)", userColor);
        }

        // Any remaining "Author:" right after a leading timestamp is a digital peer (Arden, Ember, …).
        return v.ColorPattern(@"(?<=^\[\d[^\]]*\] )[^:\n]+(?=:)", peerColor);
    }

    /// <summary>Thoughts: just the leading timestamp; thoughts/summaries stay white.</summary>
    public static ColoredTextView ForThoughts(this ColoredTextView v) => v
        .Timestamps();

    /// <summary>
    /// Actions: a collapse marker (muted) + timestamp + action name (gold) on each entry's header;
    /// when expanded, Request:/Response: labels (light purple), attribute names (yellow), and any
    /// fragment headers in a response (same colours as the Debug tab). The timestamp follows the
    /// marker so it's matched unanchored (a header is the only place a "[digit…]" appears here).
    /// </summary>
    public static ColoredTextView ForActions(this ColoredTextView v) => v
        .Suggestions()                                          // a "did you mean" hint in a result
        .ColorPattern(CollapseMarker, TuiColors.Muted)
        .ColorPattern(EntryTimestamp, TuiColors.Timestamp)
        .ColorPattern(ActionName, TuiColors.Gold)
        .ColorSubstring("Request:", TuiColors.Purple)
        .ColorSubstring("Response:", TuiColors.Purple)
        .Attributes()
        .FragmentHeaders();

    /// <summary>
    /// Schedule: each event's name (light purple) on its own line, then indented label/value rows —
    /// labels (yellow) with white values, except the status (Pending gold, Triggered/complete green,
    /// Cancelled muted).
    /// </summary>
    public static ColoredTextView ForSchedule(this ColoredTextView v) => v
        .ColorLine(t => t.Length > 0 && !char.IsWhiteSpace(t[0]) && t != "No scheduled events.", TuiColors.Purple)
        .ColorSubstring("Scheduled At:", TuiColors.Label)
        .ColorSubstring("Scheduled For:", TuiColors.Label)
        .ColorSubstring("Status:", TuiColors.Label)
        .ColorSubstring("Note:", TuiColors.Label)
        .ColorSubstring("Pending", TuiColors.Gold)
        .ColorSubstring("Triggered", TuiColors.TabUnfocused)
        .ColorSubstring("Cancelled", TuiColors.Muted);

    /// <summary>
    /// Debug shows the raw prompt and response, so it colours only unambiguous structure — never
    /// prose. Each entry's leading timestamp; the Request/Response header right after it (light
    /// purple); the sensory header (light green) and its known field labels (yellow); fragment
    /// headers (shared detector); and html-like tags only when alone on a line (the peer's response
    /// tags). Everything else — instruction text, message bodies, values — stays white.
    /// </summary>
    public static ColoredTextView ForDebug(this ColoredTextView v) => v
        .Timestamps()
        .ColorPattern(@"(?<=\] )Request[^:\n]*:", TuiColors.Purple)   // the whole entry header incl. its colon
        .ColorPattern(@"(?<=\] )Response[^:\n]*:", TuiColors.Purple)
        .ColorLinesStartingWith("[Sensory]", TuiColors.Gold)
        .FragmentHeaders()
        .SensoryLabels()
        .ResponseTags();

    /// <summary>The centred key-bindings hint row (above the input box): the chord keys accented, the
    /// framing dashes and connective text white.</summary>
    public static ColoredTextView ForComposeHint(this ColoredTextView v) => v
        .ColorSubstring("Shift+Enter:", TuiColors.Label)
        .ColorSubstring("Enter:", TuiColors.Label)
        .ColorSubstring("Ctrl+Left/Right:", TuiColors.Label)
        .ColorSubstring("Ctrl+Up/Down:", TuiColors.Label);
}
