using Terminal.Gui;

namespace Persistence.Console;

/// <summary>
/// The single source of truth for TUI colours. Different *kinds* of information use different
/// colours, and adjacent elements never share one. Foregrounds avoid dark gray / dark magenta, which
/// render too dim in common terminal themes. Several semantic names intentionally share a value
/// (e.g. User/Model/Gold are all gold) so any one can be retuned without touching the others.
/// </summary>
internal static class TuiColors
{
    public const Color Body = Color.White;            // content / values / dates / session / tag lists
    public const Color User = Color.Brown;            // "You:" role label (gold)
    public const Color Peer = Color.Magenta;          // "Remote Peer:" role label (light purple)
    public const Color Gold = Color.Brown;            // action names, R/I/C, "protected", html-like tags, Pending
    public const Color Purple = Color.Magenta;        // Request:/Response:, Triggered
    public const Color Label = Color.BrightYellow;    // field/title labels, markers, compose keys, schedule name, Note
    public const Color Error = Color.BrightRed;       // error text
    public const Color SuggestedTag = Color.BrightGreen; // the suggested tag inside a "did you mean" error
    public const Color Processing = Color.Green;      // status state chip while working (idle is white)
    public const Color Timestamp = Color.BrightBlue;  // leading [time] stamps
    public const Color TypeName = Color.BrightCyan;   // a fragment's type name in a header
    public const Color LightGreen = Color.BrightGreen; // [Sensory] header, [WAKE-UP] line
    public const Color Model = Color.Brown;           // model name in the status bar (gold)
    public const Color Muted = Color.Gray;            // de-emphasised text ([Queued], cancelled, /exit hint)
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

    // --- Detector helpers (recognise a pattern, colour it) ---

    private static ColoredTextView Timestamps(this ColoredTextView v) => v.ColorPattern(Timestamp, TuiColors.Timestamp);

    private static ColoredTextView Attributes(this ColoredTextView v) => v.ColorPattern(AttributeName, TuiColors.Label);

    /// <summary>Colours the peer's response tags gold — a tag at the start of a line, plus any further
    /// tags on that same line (so <c>&lt;continue&gt;false&lt;/continue&gt;</c> colours both tags). Tags
    /// mentioned inside system-prompt prose (not at a line start) stay uncoloured.</summary>
    private static ColoredTextView ResponseTags(this ColoredTextView v) => v
        .ColorPattern(LeadingHtmlTag, TuiColors.Gold)
        .ColorPattern(FollowingHtmlTag, TuiColors.Gold);

    /// <summary>Colours the known [Sensory] field labels yellow (and only those — not arbitrary prose).</summary>
    private static ColoredTextView SensoryLabels(this ColoredTextView v) => v.ColorPattern(SensoryLabel, TuiColors.Label);

    /// <summary>Colours a <c>[#id | Type | R:x I:x C:x | protected]</c> header: id yellow, type cyan,
    /// R/I/C and "protected" gold. Brackets, pipes and values stay white. Each part is matched by a
    /// position-anchored pattern, so these colours only land inside an actual header, never in prose.
    /// Reusable across panes.</summary>
    private static ColoredTextView FragmentHeaders(this ColoredTextView v) => v
        .ColorPattern(FragmentId, TuiColors.Label)
        .ColorPattern(FragmentTypeName, TuiColors.TypeName)
        .ColorPattern(RicMarker, TuiColors.Gold)
        .ColorPattern(ProtectedFlag, TuiColors.Error);   // "protected" stands out in red

    // --- Per-pane schemes ---

    /// <summary>
    /// Conversation: the suggested tag inside an error is claimed bright green first; the whole error
    /// (its first line and any wrapped continuation, detected as a line ending in "]" that isn't a
    /// fresh "[…" marker) reds over the rest. Then the other markers, timestamp, and role labels.
    /// </summary>
    public static ColoredTextView ForConversation(this ColoredTextView v, Color userColor, Color peerColor) => v
        .ColorPattern(SuggestedTag, TuiColors.SuggestedTag)
        .ColorLinesStartingWith("[Error", TuiColors.Error)
        .ColorLine(t => t.TrimEnd().EndsWith("]") && !t.TrimStart().StartsWith("["), TuiColors.Error)
        .ColorLinesStartingWith("[WAKE-UP", TuiColors.LightGreen)
        .ColorLinesStartingWith("[Queued", TuiColors.Muted)
        .ColorSubstring("Unknown command:", TuiColors.Label)   // label only; the command stays white
        .ColorSubstring("Executed", TuiColors.Muted)           // local-command echo, not a peer message
        .Timestamps()
        .ColorSubstring("You:", userColor)
        .ColorSubstring("Remote Peer:", peerColor);

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
        .ColorSubstring("Triggered", TuiColors.Processing)
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
        .ColorPattern(@"(?<=\] )Request\b", TuiColors.Purple)   // only the entry header, not the word in prose
        .ColorPattern(@"(?<=\] )Response\b", TuiColors.Purple)
        .ColorLinesStartingWith("[Sensory]", TuiColors.LightGreen)
        .FragmentHeaders()
        .SensoryLabels()
        .ResponseTags();

    /// <summary>The key-bindings hint row (above the input box): the "Key Bindings" header in purple
    /// (so it's distinct from the yellow chords sitting just below the yellow "Compose" title); the
    /// chord keys yellow; the rest white.</summary>
    public static ColoredTextView ForComposeHint(this ColoredTextView v) => v
        .ColorSubstring("Key Bindings", TuiColors.Purple)
        .ColorSubstring("Shift+Enter:", TuiColors.Label)
        .ColorSubstring("Enter:", TuiColors.Label)
        .ColorSubstring("Ctrl+Left/Right:", TuiColors.Label)
        .ColorSubstring("Ctrl+Up/Down:", TuiColors.Label);
}
