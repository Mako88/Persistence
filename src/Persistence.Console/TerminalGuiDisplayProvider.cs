using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Terminal.Gui;

namespace Persistence.Console;

/// <summary>
/// Terminal.Gui (v1) implementation of <see cref="IDisplayProvider"/>. Runs the TUI
/// main loop on a dedicated thread and renders into a multi-pane layout: a framed
/// conversation pane on the left (which also shows prior history with timestamps), a tabbed
/// side column (Reasoning / Actions / Schedule / Debug) on the right, a multi-line compose
/// box, and a status line at the bottom.
///
/// Per-pane colouring lives in <see cref="TuiColoring"/> (one named call per pane); colours in
/// <see cref="TuiColors"/>. All pane mutations are marshalled onto the UI thread via
/// <see cref="MainLoop.Invoke"/>. Output that arrives before the loop is ready is buffered and
/// flushed on load.
/// </summary>
[Singleton(typeof(IDisplayProvider), UiMode.Tui)]
public class TerminalGuiDisplayProvider : IDisplayProvider
{
    /// <summary>Fixed-width local timestamp (leading zeros) used for every in-pane stamp.</summary>
    private const string TimeFormat = "MM/dd/yyyy hh:mm tt";

    // Preview-only: which side-column tab to open on (set by the --preview harness for screenshots).
    internal static int PreviewInitialTab;

    private readonly IEventBus eventBus;
    private readonly ISessionContext sessionContext;
    private readonly IAppConfig config;

    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Per-pane text buffers are the source of truth; the TextViews render from them.
    private readonly object sync = new();
    private readonly StringBuilder outputBuffer = new();
    private readonly StringBuilder reasoningBuffer = new();
    private readonly StringBuilder toolsBuffer = new();
    private readonly StringBuilder scheduleBuffer = new();
    private readonly StringBuilder debugBuffer = new();

    private TextView output = null!;
    private TextView reasoning = null!;
    private TextView tools = null!;
    private TextView schedule = null!;
    private TextView debug = null!;
    private TextView input = null!;
    private TabView tabView = null!;
    private Label stateLabel = null!;
    private Label modelLabel = null!;
    private Label sessionLabel = null!;
    private Label exitLabel = null!;

    private Thread? uiThread;
    private volatile bool ready;

    public TerminalGuiDisplayProvider(IEventBus eventBus, ISessionContext sessionContext, IAppConfig config)
    {
        this.eventBus = eventBus;
        this.sessionContext = sessionContext;
        this.config = config;

        // The TUI has a dedicated Debug pane, so request/response logging is always on here — there's
        // a place to show it and no console to clutter. (The API surface still honours config.DebugMode.)
        config.DebugMode = true;
    }

    /// <summary>
    /// Subscribes to events, launches the TUI on its own thread, and returns a task
    /// that completes when the main loop exits.
    /// </summary>
    public Task Start(CancellationToken ct)
    {
        eventBus.Subscribe<RemotePeerReplied>((_, e) => { ShowReply(e.Reply); return Task.CompletedTask; });
        eventBus.Subscribe<ScheduledEventTriggered>((_, e) => { ShowWakeUpEvent(e.Event); return Task.CompletedTask; });
        eventBus.Subscribe<ToolInvoked>((_, e) => { ShowToolUse(e.Tool, e.Request, e.Result); return Task.CompletedTask; });
        eventBus.Subscribe<ModelReasoningDelta>((_, e) => { ShowReasoningDelta(e.Delta); return Task.CompletedTask; });
        eventBus.Subscribe<ModelThought>((_, e) => { ShowThought(e.Thought); return Task.CompletedTask; });

        uiThread = new Thread(RunUi) { IsBackground = true, Name = "tui" };
        uiThread.Start();

        ct.Register(Stop);
        return stopped.Task;
    }

    /// <summary>
    /// Requests the main loop to stop. Idempotent and safe to call from any thread.
    /// </summary>
    public void Stop()
    {
        if (ready)
        {
            Application.MainLoop?.Invoke(() => Application.RequestStop());
        }
        else
        {
            // Loop never came up; complete the task directly.
            stopped.TrySetResult();
        }
    }

    #region Output

    public void ShowReply(string reply)
    {
        Append(outputBuffer, () => output, $"{Stamp()}Remote Peer: {reply}\n\n");
        SetStatus("idle");
    }

    public void ShowError(string message)
    {
        Append(outputBuffer, () => output, $"[Error: {message}]\n\n");
        SetStatus("idle");
    }

    public void ShowWakeUpEvent(ScheduledEventEntity evt)
    {
        Append(outputBuffer, () => output, $"[WAKE-UP: {evt.Name}]\n\n");
        SetStatus("idle");
    }

    public void ShowMessageQueued(string text) => Append(outputBuffer, () => output, $"[Queued: {text}]\n\n");

    public void ShowSystemMessage(string message) => Append(outputBuffer, () => output, $"{message}\n\n");

    public void ShowUnknownCommand(string command) => Append(outputBuffer, () => output, $"Unknown command: {command}\n\n");

    // Trim trailing whitespace so every debug entry is separated by exactly one blank line, matching
    // the other panes (model responses often arrive with their own trailing newline, which would
    // otherwise add a second blank line here).
    public void ShowDebugInfo(string info) => Append(debugBuffer, () => debug, $"{Stamp()}{info.TrimEnd()}\n\n");

    public void ShowReasoning(string summary) => Append(reasoningBuffer, () => reasoning, $"{Stamp()}{summary}\n\n");

    public void ShowReasoningDelta(string delta) => Append(reasoningBuffer, () => reasoning, delta);

    public void ShowThought(string thought) => Append(reasoningBuffer, () => reasoning, $"{Stamp()}{thought}\n\n");

    public void ShowToolUse(string tool, string request, string result)
    {
        // Header line carries the timestamp + action name; the Request/Response labels sit flush
        // beneath it with their content indented under each — one request parameter per line — and a
        // blank line between the request and the response for separation.
        var sb = new StringBuilder();
        sb.AppendLine($"{Stamp()}{tool}");
        sb.AppendLine("Request:");
        sb.AppendLine(FormatRequestParameters(request));
        sb.AppendLine();
        sb.AppendLine("Response:");
        sb.AppendLine(Indent(result, 4));
        sb.AppendLine();

        Append(toolsBuffer, () => tools, sb.ToString());
    }

    /// <summary>
    /// Renders a command's request fields one-per-line and indented. The request is the command's
    /// JSON fields; each property becomes <c>name=value</c>. Falls back to the raw (indented) text if
    /// it isn't a JSON object.
    /// </summary>
    private static string FormatRequestParameters(string request)
    {
        try
        {
            if (JsonNode.Parse(request) is JsonObject obj && obj.Count > 0)
            {
                return string.Join("\n", obj.Select(kv => $"    {kv.Key}={kv.Value?.ToJsonString() ?? "null"}"));
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the raw rendering.
        }

        return Indent(request, 4);
    }

    /// <summary>Indents every line of <paramref name="text"/> by <paramref name="spaces"/> columns.</summary>
    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        return string.Join("\n", text.Split('\n').Select(line => pad + line));
    }

    /// <summary>
    /// Replaces the Schedule pane with the current set of pending scheduled events (a snapshot,
    /// not an append-log — the orchestrator pushes the full list whenever it changes).
    /// </summary>
    public void ShowScheduledEvents(IReadOnlyList<ScheduledEventEntity> events)
    {
        var sb = new StringBuilder();

        if (events.Count == 0)
        {
            sb.AppendLine("No scheduled events.");
        }
        else
        {
            // "name — date — status", date without leading zeros; a blank line between entries.
            foreach (var e in events.OrderBy(e => e.ScheduledForUtc))
            {
                sb.AppendLine($"{e.Name} — {e.ScheduledForUtc.LocalDateTime:M/d/yyyy h:mm tt} — {e.Status}");

                if (!string.IsNullOrWhiteSpace(e.WakePrompt))
                {
                    sb.AppendLine($"    Note: {e.WakePrompt}");
                }

                sb.AppendLine();
            }
        }

        Set(scheduleBuffer, () => schedule, sb.ToString());
    }

    public void ShowChatHistory(IReadOnlyList<(string Role, string Content, DateTimeOffset Timestamp)> messages)
    {
        // History now lives in the main conversation pane, shown on startup with timestamps.
        foreach (var (role, content, ts) in messages)
        {
            var who = role == "user" ? "You" : "Remote Peer";
            Append(outputBuffer, () => output, $"[{ts.LocalDateTime.ToString(TimeFormat)}] {who}: {content}\n\n");
        }
    }

    public void ShowThinking(string? label = null) => SetStatus($"{label ?? "thinking"}…");

    #endregion

    #region UI construction & lifecycle

    private void RunUi()
    {
        try
        {
            Application.Init();

            var top = Application.Top;
            BuildLayout(top);

            top.Ready += OnReady;
            Application.Run(top);
        }
        catch (Exception ex)
        {
            // Surface init/run failures (e.g. no interactive terminal) instead of
            // hanging the awaiting orchestrator forever.
            stopped.TrySetException(ex);
            return;
        }
        finally
        {
            ready = false;
            try { Application.Shutdown(); }
            catch { /* shutdown is best-effort */ }
        }

        stopped.TrySetResult();
    }

    private void BuildLayout(Toplevel top)
    {
        var driver = Application.Driver;

        var baseTheme = Scheme(driver, TuiColors.Body, Color.Black);
        Colors.TopLevel = baseTheme;
        Colors.Base = baseTheme;
        Colors.Dialog = baseTheme;
        Colors.Menu = baseTheme;
        top.ColorScheme = baseTheme;

        // All panes share a white base; meaning is carried by per-rule accent colours (TuiColoring).
        var paneScheme = Scheme(driver, TuiColors.Body, Color.Black);

        // Reserve rows at the bottom: 5 for the compose box (hint + input), 1 for the status bar.
        const int bottomRows = 6;

        var outputFrame = new FrameView("Conversation")
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(62),
            Height = Dim.Fill(bottomRows),
            ColorScheme = baseTheme,
        };
        output = MakeColoredPaneView(paneScheme).ForConversation(TuiColors.User, TuiColors.Peer);
        outputFrame.Add(output);
        AddScrollbar(output);

        tabView = new HighlightedTabView
        {
            X = Pos.Right(outputFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(bottomRows),
            ColorScheme = baseTheme,
        };

        // Use ColoredTextView everywhere so read-only text renders at full brightness (a plain
        // read-only TextView dims its text). Each pane's colours are one named call into TuiColoring.
        reasoning = MakeColoredPaneView(paneScheme).ForThoughts();
        tools = MakeColoredPaneView(paneScheme).ForActions();
        schedule = MakeColoredPaneView(paneScheme).ForSchedule();
        debug = MakeColoredPaneView(paneScheme).ForDebug();

        // Tab titles get a space of horizontal padding on each side.
        tabView.AddTab(new TabView.Tab(" Thoughts ", WrapPane(reasoning, paneScheme)), andSelect: true);
        tabView.AddTab(new TabView.Tab(" Actions ", WrapPane(tools, paneScheme)), andSelect: false);
        tabView.AddTab(new TabView.Tab(" Schedule ", WrapPane(schedule, paneScheme)), andSelect: false);
        tabView.AddTab(new TabView.Tab(" Debug ", WrapPane(debug, paneScheme)), andSelect: false);

        // Tab switching is reserved for Ctrl+Left/Right (handled on the focused view). Drop TabView's
        // built-in plain-arrow bindings so a stray Left/Right doesn't change tabs when the tab bar or a
        // pane has focus — plain arrows should just move the cursor / scroll the focused pane.
        tabView.ClearKeybinding(Key.CursorLeft);
        tabView.ClearKeybinding(Key.CursorRight);

        // Ctrl+Arrows navigate from any pane (or the input): Left/Right switch tabs, Up/Down cycle
        // focus through the visible panes so each can be scrolled with the keyboard.
        foreach (var pane in new[] { output, reasoning, tools, schedule, debug })
        {
            pane.KeyPress += OnNavKeyPress;
        }

        // Preview can open on a specific tab (for screenshots); defaults to the first.
        var allTabs = tabView.Tabs.ToList();
        if (PreviewInitialTab > 0 && PreviewInitialTab < allTabs.Count)
        {
            tabView.SelectedTab = allTabs[PreviewInitialTab];
        }

        // Compose area: a colour-keyed hint on its own row, then the bordered multi-line input below.
        var hint = MakeColoredPaneView(paneScheme).ForComposeHint();
        hint.Text = "Compose  —  Enter: send · Shift+Enter: newline · Ctrl+Left/Right: tabs · Ctrl+Up/Down: panes";
        hint.WordWrap = false;
        hint.X = 0;
        hint.Y = Pos.AnchorEnd(bottomRows);
        hint.Width = Dim.Fill();
        hint.Height = 1;

        var inputFrame = new FrameView(string.Empty)
        {
            X = 0,
            Y = Pos.AnchorEnd(bottomRows - 1),
            Width = Dim.Fill(),
            Height = bottomRows - 2,
            ColorScheme = baseTheme,
        };
        input = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Multiline = true,
            WordWrap = true,
            ColorScheme = baseTheme,
        };
        input.KeyPress += OnInputKeyPress;
        inputFrame.Add(input);
        AddScrollbar(input);

        BuildStatusBar(driver, top, bottomRows);
        top.Add(outputFrame, tabView, hint, inputFrame);
        input.SetFocus();
    }

    /// <summary>
    /// Builds the bottom status bar: a state chip (colour reflects state) + model + session + a muted
    /// /exit hint. Only the state chip changes at runtime; the others are set once here.
    /// </summary>
    private void BuildStatusBar(ConsoleDriver driver, Toplevel top, int bottomRows)
    {
        // Pipe separators are their own white segments so each content segment keeps its own colour.
        stateLabel = StatusSegment(driver, StateText("idle"), StateColor("idle"), x: 0);
        var pipe1 = StatusSegment(driver, " │ ", TuiColors.Body, Pos.Right(stateLabel));
        modelLabel = StatusSegment(driver, $"{config.Provider}/{config.Model}", TuiColors.Model, Pos.Right(pipe1));
        var pipe2 = StatusSegment(driver, " │ ", TuiColors.Body, Pos.Right(modelLabel));
        sessionLabel = StatusSegment(driver, $"Session {sessionContext.SessionId}", TuiColors.Body, Pos.Right(pipe2));
        var pipe3 = StatusSegment(driver, " │ ", TuiColors.Body, Pos.Right(sessionLabel));
        exitLabel = StatusSegment(driver, "/exit to quit", TuiColors.Muted, Pos.Right(pipe3));
        exitLabel.Width = Dim.Fill();
        exitLabel.AutoSize = false;

        top.Add(stateLabel, pipe1, modelLabel, pipe2, sessionLabel, pipe3, exitLabel);
    }

    private static Label StatusSegment(ConsoleDriver driver, string text, Color fg, Pos x) => new(text)
    {
        X = x,
        Y = Pos.AnchorEnd(1),
        Height = 1,
        AutoSize = true,
        ColorScheme = Scheme(driver, fg, TuiColors.StatusBg),
    };

    private static ColorScheme Scheme(ConsoleDriver driver, Color fg, Color bg) => new()
    {
        Normal = driver.MakeAttribute(fg, bg),
        Focus = driver.MakeAttribute(fg, bg),
        HotNormal = driver.MakeAttribute(Color.BrightYellow, bg),
        HotFocus = driver.MakeAttribute(Color.BrightYellow, bg),
        Disabled = driver.MakeAttribute(Color.DarkGray, bg),
    };

    private static ColoredTextView MakeColoredPaneView(ColorScheme scheme) => new()
    {
        ReadOnly = true,
        WordWrap = true,
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(),
        ColorScheme = scheme,
    };

    /// <summary>
    /// Wraps a pane view in a container with a vertical scrollbar. The container is the
    /// view's stable parent (so it has a SuperView even when its tab isn't selected), which
    /// the <see cref="ScrollBarView"/> ctor requires.
    /// </summary>
    private static View WrapPane(TextView view, ColorScheme scheme)
    {
        var container = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = scheme,
        };

        container.Add(view);
        AddScrollbar(view);

        return container;
    }

    /// <summary>
    /// Attaches a vertical scrollbar to a read-only text pane and keeps it in sync with
    /// the view's content height and scroll position.
    /// </summary>
    private static void AddScrollbar(TextView view)
    {
        var scrollbar = new ScrollBarView(view, isVertical: true, showBothScrollIndicator: false);

        view.DrawContent += _ =>
        {
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

    /// <summary>
    /// Once the loop is up, mark ready and flush any buffered text into the panes.
    /// </summary>
    private void OnReady()
    {
        ready = true;

        lock (sync)
        {
            output.Text = outputBuffer.ToString();
            reasoning.Text = reasoningBuffer.ToString();
            tools.Text = toolsBuffer.ToString();
            schedule.Text = scheduleBuffer.ToString();
            debug.Text = debugBuffer.ToString();
        }
    }

    /// <summary>
    /// Shared key handling for the read-only panes: Ctrl+Arrows navigate (Left/Right switch tabs,
    /// Up/Down cycle pane focus). Plain keys are left alone so each pane scrolls normally.
    /// </summary>
    private void OnNavKeyPress(View.KeyEventEventArgs e)
    {
        if (HandleNavKey(e.KeyEvent.Key))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Ctrl+Arrow navigation, shared by the compose box and the panes. Returns true if it consumed
    /// the key. Ctrl+Left/Right switch side-column tabs; Ctrl+Up/Down cycle keyboard focus through the
    /// visible panes (so each can be scrolled). Ctrl+Tab/Ctrl+Shift+Tab are kept as a fallback for
    /// terminals that pass them through (Windows Terminal eats Ctrl+Tab for its own tabs).
    /// </summary>
    private bool HandleNavKey(Key key)
    {
        switch (key)
        {
            case Key.CtrlMask | Key.CursorRight:
            case Key.CtrlMask | Key.Tab:
                CycleTab(1);
                return true;
            case Key.CtrlMask | Key.CursorLeft:
            case Key.CtrlMask | Key.BackTab:
            case Key.CtrlMask | Key.ShiftMask | Key.Tab:
                CycleTab(-1);
                return true;
            case Key.CtrlMask | Key.CursorDown:
                CyclePaneFocus(1);
                return true;
            case Key.CtrlMask | Key.CursorUp:
                CyclePaneFocus(-1);
                return true;
            default:
                return false;
        }
    }

    private void OnInputKeyPress(View.KeyEventEventArgs e)
    {
        var key = e.KeyEvent.Key;

        if (HandleNavKey(key))
        {
            e.Handled = true;
            return;
        }

        // Plain Enter sends. Shift+Enter (and other modifiers) fall through so the
        // multi-line TextView can insert a newline.
        if (key != Key.Enter)
        {
            return;
        }

        e.Handled = true;

        var text = input.Text?.ToString()?.Trim() ?? "";
        input.Text = "";

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Echo the submitted input to the conversation pane — unlike a raw console, the TextView
        // doesn't leave the typed text visible anywhere. A slash command echoes as "Executed /foo"
        // (not "You: …") to make clear it ran locally and was never sent to the remote peer.
        var echo = text.StartsWith('/') ? $"Executed {text}" : $"You: {text}";
        Append(outputBuffer, () => output, $"{Stamp()}{echo}\n\n");

        // Repaint now so the input box visibly clears on keypress, then dispatch the turn
        // off the UI thread — the subscriber chain runs synchronously up to its first
        // await (DB access, model call), which would otherwise block repainting.
        input.SetNeedsDisplay();

        eventBus.FireAndForget(this, new DisplayInputReceived(text),
            ex => ShowError(ex.Message));
    }

    /// <summary>Selects the tab <paramref name="direction"/> steps from the current one, wrapping.</summary>
    private void CycleTab(int direction)
    {
        var tabs = tabView.Tabs.ToList();

        if (tabs.Count == 0)
        {
            return;
        }

        var current = tabs.IndexOf(tabView.SelectedTab);
        if (current < 0)
        {
            current = 0;
        }

        var next = ((current + direction) % tabs.Count + tabs.Count) % tabs.Count;
        tabView.SelectedTab = tabs[next];
        tabView.SetNeedsDisplay();
    }

    /// <summary>
    /// Moves keyboard focus <paramref name="direction"/> steps through the visible panes — the
    /// conversation, the active side pane, and the compose box — wrapping. Lets the user scroll any
    /// pane from the keyboard without changing which tab is selected.
    /// </summary>
    private void CyclePaneFocus(int direction)
    {
        var targets = new View[] { output, ActiveSidePane(), input };
        var current = Array.FindIndex(targets, t => t.HasFocus);
        if (current < 0)
        {
            current = targets.Length - 1;   // nothing tracked yet — treat as if on the input
        }

        var next = ((current + direction) % targets.Length + targets.Length) % targets.Length;
        targets[next].SetFocus();
    }

    /// <summary>The text pane shown by the currently selected side-column tab.</summary>
    private TextView ActiveSidePane()
    {
        var index = tabView.Tabs.ToList().IndexOf(tabView.SelectedTab);
        return index switch
        {
            1 => tools,
            2 => schedule,
            3 => debug,
            _ => reasoning,
        };
    }

    #endregion

    #region Helpers

    /// <summary>A leading fixed-width local timestamp for a conversation/reasoning/action line.</summary>
    private static string Stamp() => $"[{DateTimeOffset.Now.LocalDateTime.ToString(TimeFormat)}] ";

    /// <summary>
    /// Appends text to a pane's buffer and, when the loop is ready, pushes the
    /// updated buffer onto the UI thread.
    /// </summary>
    private void Append(StringBuilder buffer, Func<TextView> view, string text)
    {
        string snapshot;

        lock (sync)
        {
            buffer.Append(text);
            snapshot = buffer.ToString();
        }

        if (!ready)
        {
            return;
        }

        Application.MainLoop?.Invoke(() =>
        {
            var v = view();
            v.Text = snapshot;
            ScrollToBottom(v);
        });
    }

    /// <summary>
    /// Replaces a pane's buffer with <paramref name="text"/> (for snapshot panes like Schedule that
    /// show current state rather than an append-only log).
    /// </summary>
    private void Set(StringBuilder buffer, Func<TextView> view, string text)
    {
        lock (sync)
        {
            buffer.Clear();
            buffer.Append(text);
        }

        if (!ready)
        {
            return;
        }

        Application.MainLoop?.Invoke(() =>
        {
            var v = view();
            v.Text = text;
            ScrollToBottom(v);
        });
    }

    /// <summary>
    /// Scrolls a read-only pane to the bottom so the newest content is visible, without stealing focus
    /// (the input keeps it). Sets the top row directly rather than moving the cursor, so it scrolls
    /// even when the pane isn't focused.
    /// </summary>
    private static void ScrollToBottom(TextView view)
    {
        var bottom = Math.Max(0, view.Lines - view.Bounds.Height);
        view.TopRow = bottom;
        view.SetNeedsDisplay();
    }

    private void SetStatus(string state)
    {
        if (!ready)
        {
            return;
        }

        Application.MainLoop?.Invoke(() =>
        {
            stateLabel.Text = StateText(state);
            stateLabel.ColorScheme = Scheme(Application.Driver, StateColor(state), TuiColors.StatusBg);
            // The chip's width changes with the text; relayout so the model/info segments follow.
            Application.Top?.LayoutSubviews();
            Application.Top?.SetNeedsDisplay();
        });
    }

    private static string StateText(string state) => $" {state}";

    /// <summary>Colours the state chip: green while processing (states end with "…"), white when idle.</summary>
    private static Color StateColor(string state) => state.Contains('…') ? TuiColors.Processing : TuiColors.Body;

    #endregion
}
