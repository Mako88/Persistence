using System.Text;
using Terminal.Gui;

namespace Persistence.Console;

/// <summary>
/// What should happen to the reader's scroll position when a pane's text changes.
///
/// This is a policy rather than a bool because the three cases are genuinely different intents, and the
/// wrong one is invisible until it's annoying: a pane that jumps while you're reading, or one that
/// silently stops following a live conversation.
/// </summary>
internal enum ScrollBehaviour
{
    /// <summary>
    /// Jump to the newest line. For content the reader hasn't seen before — a fresh load, or a switch to
    /// a different peer, where the old scroll position means nothing in the new text.
    /// </summary>
    JumpToNewest,

    /// <summary>
    /// Follow the tail, but only if the reader is already at the bottom. For a live append: being at the
    /// bottom is a request to watch the conversation, and being scrolled up is a request to read.
    /// </summary>
    FollowTail,

    /// <summary>
    /// Never move. For content that changed <em>under</em> the reader at a place they're looking at —
    /// expanding an Actions entry, say, where jumping anywhere would lose the thing they just clicked.
    /// </summary>
    KeepPosition,
}

/// <summary>
/// One scrollable text pane: its view, the text it is currently showing, and the rules for updating it.
///
/// This exists because "a pane" used to be smeared across the display provider — a <see cref="TextView"/>
/// field, a <see cref="StringBuilder"/> beside it, a third field remembering what was last rendered, a
/// scroll rule at each call site, and a scrollbar wired separately — with each pane repeating all five.
/// Updating a pane correctly meant remembering every one of them, so each new surface re-derived the
/// rules, and the ones that got them wrong (repainting unchanged text, yanking the reader to the bottom)
/// only ever showed up as "the TUI feels slow" or "it keeps scrolling away from me".
///
/// So the pane owns them:
/// <list type="bullet">
/// <item>its text is <em>here</em>, appended or replaced, and is the single source of truth;</item>
/// <item>it never touches the view when the text hasn't actually changed — <see cref="TextView.Text"/>
/// re-lexes and re-wraps the whole document, which is the expensive thing worth skipping;</item>
/// <item>the scroll rule is named at the call site (<see cref="ScrollBehaviour"/>) rather than
/// re-implemented;</item>
/// <item>updates from any thread are marshalled onto the UI loop, and anything that arrives before the
/// loop is up is buffered and rendered on <see cref="MarkReady"/>.</item>
/// </list>
///
/// Terminal.Gui v1 has no append on <see cref="TextView"/> — every change re-assigns the whole document —
/// so a very long scrollback costs O(text) per update. That's inherent to v1 and fine at conversation
/// scale; it's confined to <see cref="Render"/>, which is the one place a v2 move would need to look.
/// </summary>
internal sealed class Pane
{
    private readonly object sync = new();
    private readonly StringBuilder buffer = new();

    /// <summary>What the view is currently displaying. Only ever touched on the UI thread.</summary>
    private string shown = "";

    private volatile bool ready;

    /// <summary>The pane's view — for focus, key wiring, and the colouring hooks.</summary>
    public ColoredTextView View { get; }

    private Pane(ColoredTextView view) => View = view;

    /// <summary>
    /// Creates a pane. Deliberately takes nothing: a pane must exist — and be able to buffer text —
    /// from the moment the display does, because content arrives before the UI loop is up and long
    /// before the layout is built. Its colours need a <see cref="ConsoleDriver"/>, which only exists
    /// after <c>Application.Init</c>, so they're applied later by <see cref="Configure"/>.
    /// </summary>
    public static Pane Create() => new(NewView());

    /// <summary>
    /// Applies the driver-dependent colour scheme and this pane's colour rules (one named call into
    /// <see cref="TuiColoring"/>). Called once, while building the layout.
    /// </summary>
    public void Configure(ColorScheme scheme, Func<ColoredTextView, ColoredTextView> colouring)
    {
        View.ColorScheme = scheme;
        colouring(View);
    }

    /// <summary>
    /// The standard pane view. A <see cref="ColoredTextView"/> everywhere (even where there are no colour
    /// rules) so read-only text renders at full brightness — a plain read-only TextView dims it — and so
    /// every pane shares the wheel speed and cursor behaviour.
    /// </summary>
    public static ColoredTextView NewView() => new()
    {
        ReadOnly = true,
        WordWrap = true,
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(),
    };

    /// <summary>A standard view with its colours already set — for the one-line rows (the peer selector,
    /// the compose hint) that aren't Panes: they hold no buffer and are written once.</summary>
    public static ColoredTextView NewView(ColorScheme scheme)
    {
        var view = NewView();
        view.ColorScheme = scheme;
        return view;
    }

    // --- Content ---

    /// <summary>Adds to the pane's text, following the tail only if the reader is already at the bottom.</summary>
    public void Append(string text)
    {
        string snapshot;

        lock (sync)
        {
            buffer.Append(text);
            snapshot = buffer.ToString();
        }

        Render(snapshot, ScrollBehaviour.FollowTail);
    }

    /// <summary>Replaces the pane's text wholesale (a snapshot pane, or a push from the multi-peer hub).</summary>
    public void Set(string text, ScrollBehaviour behaviour)
    {
        lock (sync)
        {
            buffer.Clear();
            buffer.Append(text);
        }

        Render(text, behaviour);
    }

    /// <summary>
    /// Marks the UI loop up and paints whatever arrived before it was. Everything buffers until this
    /// point, so nothing written during start-up is lost to an empty view.
    /// </summary>
    public void MarkReady(ScrollBehaviour onLoad)
    {
        ready = true;

        string snapshot;
        lock (sync)
        {
            snapshot = buffer.ToString();
        }

        Render(snapshot, onLoad);
    }

    /// <summary>
    /// Pushes text to the view on the UI thread, skipping the work entirely when nothing changed.
    ///
    /// The skip is the point: callers repaint far more often than the text actually changes (the hub
    /// repaints every pane on every recorded event, and a streamed reasoning delta fires one per chunk),
    /// and assigning <see cref="TextView.Text"/> re-lexes and re-wraps the whole document. Callers that
    /// hand back the same string when nothing changed get this for free — <c>string.Equals</c>
    /// short-circuits on reference equality.
    /// </summary>
    private void Render(string text, ScrollBehaviour behaviour)
    {
        if (!ready)
        {
            return;   // buffered; MarkReady paints it
        }

        Application.MainLoop?.Invoke(() =>
        {
            if (string.Equals(shown, text, StringComparison.Ordinal))
            {
                return;
            }

            // Measure before replacing the text: "was the reader at the bottom" is a question about what
            // they were looking at, not about what's arriving.
            var follow = behaviour switch
            {
                ScrollBehaviour.JumpToNewest => true,
                ScrollBehaviour.FollowTail => IsAtBottom(),
                _ => false,
            };
            var topRow = View.TopRow;

            View.Text = text;
            shown = text;

            if (follow)
            {
                ScrollToBottom();
                return;
            }

            // Hold the reader's place. Clamped because the text may have got shorter under them.
            View.TopRow = Math.Clamp(topRow, 0, MaxTopRow());
            View.SetNeedsDisplay();
        });
    }

    // --- Scrolling ---

    /// <summary>The last row the pane can scroll to — i.e. the newest content in view.</summary>
    private int MaxTopRow() => Math.Max(0, View.Lines - View.Bounds.Height);

    /// <summary>
    /// True when the pane's last line is on screen — the reader is watching the live tail. An empty or
    /// not-yet-laid-out pane counts as at-bottom, so the default is to follow.
    /// </summary>
    private bool IsAtBottom() => View.TopRow >= MaxTopRow();

    /// <summary>
    /// Scrolls to the newest content without stealing focus (the compose box keeps it). Moves the top row
    /// directly rather than the cursor, so it works even when the pane isn't focused.
    /// </summary>
    private void ScrollToBottom()
    {
        View.TopRow = MaxTopRow();
        View.SetNeedsDisplay();
    }

    // --- Layout ---

    /// <summary>
    /// Wraps the pane in a container with a scrollbar, for hosting in a tab. The container is the view's
    /// stable parent — so it has a SuperView even while its tab isn't selected, which
    /// <see cref="ScrollBarView"/> requires.
    /// </summary>
    public View InContainer(ColorScheme scheme)
    {
        var container = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = scheme,
        };

        container.Add(View);
        AttachScrollbar(View);

        return container;
    }

    /// <summary>Attaches the scrollbar to a pane that already has a parent (e.g. one inside a frame).</summary>
    public void AttachScrollbar() => AttachScrollbar(View);

    /// <summary>
    /// Attaches a vertical scrollbar to a text view and keeps it in sync with the content height and
    /// scroll position. Also used for the compose box, which isn't a Pane (it's editable and owns no
    /// buffer) but wants the same scrollbar.
    /// </summary>
    public static void AttachScrollbar(TextView view)
    {
        var scrollbar = new ScrollBarView(view, isVertical: true, showBothScrollIndicator: false)
        {
            // Don't let the scrollbar take keyboard focus — it would interfere with Ctrl+Up/Down cycling
            // focus through the panes (and isn't keyboard-driven here anyway).
            CanFocus = false,
        };

        // DrawContent fires on every repaint of the pane, but the scrollbar only needs touching when the
        // content length or the scroll position actually moved — and Refresh()/LayoutSubviews() do real
        // layout work (Refresh re-runs the show/hide sizing pass). Syncing unconditionally made every
        // repaint, including each streamed chunk, pay for a full scrollbar relayout.
        var lastSize = -1;
        var lastPosition = -1;

        view.DrawContent += _ =>
        {
            if (view.Lines == lastSize && view.TopRow == lastPosition)
            {
                return;
            }

            lastSize = view.Lines;
            lastPosition = view.TopRow;

            scrollbar.Size = view.Lines;
            scrollbar.Position = view.TopRow;
            scrollbar.LayoutSubviews();
            scrollbar.Refresh();
        };

        scrollbar.ChangedPosition += () =>
        {
            view.TopRow = scrollbar.Position;
            view.SetNeedsDisplay();
        };
    }
}
