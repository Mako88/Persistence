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

    // The Actions pane is a list of collapsible entries (rebuilt into toolsBuffer on change);
    // actionHeaderRows maps each entry to its header line, so a toggle can resolve a cursor/click row.
    private readonly List<ActionEntry> actionEntries = [];
    private List<int> actionHeaderRows = [];

    private TextView output = null!;
    private TextView reasoning = null!;
    private TextView tools = null!;
    private TextView schedule = null!;
    private TextView debug = null!;
    private TextView input = null!;
    private TabView tabView = null!;
    private Label stateLabel = null!;
    private Label modelLabel = null!;
    private Label budgetLabel = null!;
    private Label proposalsLabel = null!;
    private Label sessionLabel = null!;
    private Label exitLabel = null!;
    private View statusBar = null!;
    private readonly List<Label> statusSegments = [];

    private Thread? uiThread;
    private volatile bool ready;

    // Latest status-bar values, retained so a push that arrives before the loop is ready (e.g. the
    // initial proposal count / first budget calc) can be applied on load rather than lost.
    private int lastProposalCount;
    private (int Used, int Budget, int Percent)? lastBudget;

    /// <summary>
    /// Where submitted local-peer input goes. Defaults to publishing on the in-process event bus (the
    /// turn pipeline picks it up); client mode overrides it to send over HTTP. Kept as a delegate so the
    /// renderer itself is transport-agnostic. Set it before <see cref="LaunchUi"/>.
    /// </summary>
    public Func<string, Task> OnInput { get; set; }

    public TerminalGuiDisplayProvider(IEventBus eventBus, ISessionContext sessionContext, IAppConfig config)
    {
        this.eventBus = eventBus;
        this.sessionContext = sessionContext;
        this.config = config;

        // The TUI has a dedicated Debug pane, so request/response logging is always on here — there's
        // a place to show it and no console to clutter. (The API surface still honours config.DebugMode.)
        config.DebugMode = true;

        OnInput = text =>
        {
            eventBus.FireAndForget(this, new DisplayInputReceived(text), ex => ShowError(ex.Message));
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Subscribes to events, launches the TUI on its own thread, and returns a task
    /// that completes when the main loop exits.
    /// </summary>
    public Task Start(CancellationToken ct)
    {
        SubscribeToInProcessEvents();
        return LaunchUi(ct);
    }

    /// <summary>
    /// Wires the in-process event bus to the panes. In-process mode calls this via <see cref="Start"/>;
    /// client mode skips it and drives the panes from the API conversation stream instead.
    /// </summary>
    private void SubscribeToInProcessEvents()
    {
        eventBus.Subscribe<RemotePeerReplied>((_, e) => { ShowReply(e.Reply); return Task.CompletedTask; });
        eventBus.Subscribe<ScheduledEventTriggered>((_, e) => { ShowWakeUpEvent(e.Event); return Task.CompletedTask; });
        eventBus.Subscribe<ToolInvoked>((_, e) => { ShowToolUse(e.Tool, e.Request, e.Result); return Task.CompletedTask; });
        eventBus.Subscribe<ModelReasoningDelta>((_, e) => { ShowReasoningDelta(e.Delta); return Task.CompletedTask; });
        eventBus.Subscribe<ModelThought>((_, e) => { ShowThought(e.Thought); return Task.CompletedTask; });
        eventBus.Subscribe<ContextBudgetUpdated>((_, e) => { UpdateBudget(e.UsedTokens, e.BudgetTokens, e.PercentFull); return Task.CompletedTask; });
    }

    /// <summary>
    /// Launches the Terminal.Gui loop on its own thread and returns a task that completes when it exits.
    /// Transport-agnostic: the caller wires input (<see cref="OnInput"/>) and rendering (bus or stream).
    /// </summary>
    public Task LaunchUi(CancellationToken ct)
    {
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
        var entry = new ActionEntry(Stamp(), tool, FormatRequestParameters(request), Indent(result, 4));

        lock (sync)
        {
            actionEntries.Add(entry);
        }

        RenderActions(scrollToBottom: true);
    }

    /// <summary>
    /// One Actions-pane entry. Shown collapsed (just the header) by default; expanding reveals the
    /// request parameters and the response.
    /// </summary>
    private sealed class ActionEntry(string time, string command, string request, string response)
    {
        public string Time { get; } = time;
        public string Command { get; } = command;
        public string Request { get; } = request;     // pre-formatted, one parameter per line
        public string Response { get; } = response;    // pre-formatted (indented)
        public bool Collapsed { get; set; } = true;
    }

    /// <summary>
    /// Rebuilds the Actions pane from <see cref="actionEntries"/>: a collapse marker + timestamp +
    /// command per entry, with the request/response revealed when expanded. Records each entry's header
    /// line so a toggle can map a cursor/click row back to its entry.
    /// </summary>
    private void RenderActions(bool scrollToBottom)
    {
        var sb = new StringBuilder();
        var headerRows = new List<int>();
        string snapshot;

        lock (sync)
        {
            var row = 0;
            foreach (var e in actionEntries)
            {
                headerRows.Add(row);
                sb.AppendLine($"{(e.Collapsed ? "▶" : "▼")} {e.Time}{e.Command}");
                row++;

                if (!e.Collapsed)
                {
                    sb.AppendLine("Request:");
                    row++;
                    foreach (var line in e.Request.Split('\n'))
                    {
                        sb.AppendLine(line);
                        row++;
                    }

                    sb.AppendLine();
                    row++;
                    sb.AppendLine("Response:");
                    row++;
                    foreach (var line in e.Response.Split('\n'))
                    {
                        sb.AppendLine(line);
                        row++;
                    }
                }

                sb.AppendLine();
                row++;
            }

            actionHeaderRows = headerRows;
            toolsBuffer.Clear();
            toolsBuffer.Append(sb.ToString());
            snapshot = sb.ToString();
        }

        if (!ready)
        {
            return;
        }

        Application.MainLoop?.Invoke(() =>
        {
            tools.Text = snapshot;
            if (scrollToBottom)
            {
                ScrollToBottom(tools);
            }
            else
            {
                tools.SetNeedsDisplay();
            }
        });
    }

    /// <summary>
    /// Toggles the collapse state of the Actions entry whose header is at or above <paramref name="row"/>
    /// (the line the user activated), then re-renders and parks the cursor on that entry's header.
    /// </summary>
    private void ToggleActionAt(int row)
    {
        int index;

        lock (sync)
        {
            index = -1;
            for (var i = 0; i < actionHeaderRows.Count; i++)
            {
                if (actionHeaderRows[i] <= row)
                {
                    index = i;
                }
                else
                {
                    break;
                }
            }

            if (index < 0)
            {
                return;
            }

            actionEntries[index].Collapsed = !actionEntries[index].Collapsed;
        }

        RenderActions(scrollToBottom: false);

        // Keep the toggled entry's header under the cursor so the view doesn't jump.
        if (ready)
        {
            Application.MainLoop?.Invoke(() =>
            {
                int headerRow;
                lock (sync)
                {
                    headerRow = index < actionHeaderRows.Count ? actionHeaderRows[index] : 0;
                }

                tools.CursorPosition = new Point(0, headerRow);
            });
        }
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
                return string.Join("\n", obj.Select(kv => $"    {kv.Key}={FormatRequestValue(kv.Value)}"));
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the raw rendering.
        }

        return Indent(request, 4);
    }

    /// <summary>
    /// Renders a single request field value for display. A string is shown decoded (real quotes and
    /// line breaks, not <c>"</c>/<c>\n</c> escapes), with any internal line breaks indented to sit
    /// under the field. Numbers/booleans/arrays render as compact JSON with relaxed escaping.
    /// </summary>
    private static string FormatRequestValue(JsonNode? value)
    {
        if (value is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            return $"\"{s.Replace("\n", "\n        ")}\"";
        }

        return value?.ToJsonString(RelaxedJson) ?? "null";
    }

    private static readonly JsonSerializerOptions RelaxedJson =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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
            // Event name on its own line, then indented label/value rows; dates without leading
            // zeros; a blank line between entries.
            foreach (var e in events.OrderBy(e => e.ScheduledForUtc))
            {
                sb.AppendLine(e.Name);
                sb.AppendLine($"    Scheduled At: {e.CreatedUtc.LocalDateTime:M/d/yyyy h:mm tt}");
                sb.AppendLine($"    Scheduled For: {e.ScheduledForUtc.LocalDateTime:M/d/yyyy h:mm tt}");
                sb.AppendLine($"    Status: {e.Status}");

                if (!string.IsNullOrWhiteSpace(e.WakePrompt))
                {
                    sb.AppendLine($"    Note: {e.WakePrompt}");
                }

                sb.AppendLine();
            }
        }

        Set(scheduleBuffer, () => schedule, sb.ToString());
    }

    public void ShowOpenProposalCount(int count)
    {
        lastProposalCount = count;
        UpdateStatusSegment(() => proposalsLabel, ProposalsText(count), ProposalsColor(count));
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

        // Reserve rows at the bottom: 1 for the key-bindings hint, 6 for the compose box (a border
        // plus ~4 input lines, so its scrollbar is usable), and 1 for the status bar.
        const int bottomRows = 8;

        var outputFrame = new FocusTitleFrameView("Conversation")
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

        // The Schedule pane colours its event-name lines by their column-0 position; word-wrap would
        // drop a long Note's continuation to column 0 and mis-colour it as a name, so keep its lines
        // unwrapped (the detail rows are short; a long note scrolls rather than wraps).
        schedule.WordWrap = false;

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
        // focus through the visible panes so each can be scrolled with the keyboard. Plain typing on a
        // pane jumps to the compose box (carrying the character), since that's the only place input is
        // accepted.
        foreach (var pane in new[] { output, reasoning, tools, schedule, debug })
        {
            pane.KeyPress += OnNavKeyPress;

            if (pane is ColoredTextView coloured)
            {
                coloured.OnPrintableInput = RedirectTypingToInput;
            }
        }

        // Actions entries collapse/expand on Enter or click.
        if (tools is ColoredTextView toolsView)
        {
            toolsView.OnLineActivated = ToggleActionAt;
        }

        // Preview can open on a specific tab (for screenshots); defaults to the first.
        var allTabs = tabView.Tabs.ToList();
        if (PreviewInitialTab > 0 && PreviewInitialTab < allTabs.Count)
        {
            tabView.SelectedTab = allTabs[PreviewInitialTab];
        }

        // Compose area: a colour-keyed key-bindings hint on its own row, then the bordered multi-line
        // input below (titled "Compose", which highlights with focus like the Conversation frame).
        const string hintText = "Enter: Send · Shift+Enter: Newline · Ctrl+Left/Right: Tabs · Ctrl+Up/Down: Panes";
        var hint = MakeColoredPaneView(paneScheme).ForComposeHint();
        hint.Text = hintText;
        hint.WordWrap = false;
        hint.Y = Pos.AnchorEnd(bottomRows);
        hint.Height = 1;
        // Size to the text and centre it (rather than filling the width left-aligned).
        hint.Width = Dim.Sized(hintText.Length);
        hint.X = Pos.Center();

        var inputFrame = new FocusTitleFrameView("Compose")
        {
            X = 0,
            Y = Pos.AnchorEnd(bottomRows - 1),
            Width = Dim.Fill(),
            Height = bottomRows - 2,
            ColorScheme = baseTheme,
        };
        // A ColoredTextView (with no rules) so the compose box shares the pane behaviour: plain arrows
        // move the cursor without jumping focus, and the wheel scrolls at a usable speed.
        input = new ColoredTextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = false,
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
    /// Builds the bottom status bar: a state chip + model + context-budget gauge + open-proposal
    /// indicator + session + a muted /exit hint. The state, budget and proposal segments change at
    /// runtime; the others are set once here.
    /// </summary>
    private void BuildStatusBar(ConsoleDriver driver, Toplevel top, int bottomRows)
    {
        // The bar is a container centred horizontally; its segments chain left-to-right inside it, and
        // the container is re-sized to their total width whenever a segment's text changes so it stays
        // centred. Pipe separators are their own white segments so each content segment keeps its colour.
        statusBar = new View { X = Pos.Center(), Y = Pos.AnchorEnd(1), Height = 1, Width = Dim.Sized(1) };

        stateLabel = StatusSegment(driver, StateText("idle"), StateColor("idle"), x: 0);
        var pipe1 = StatusSegment(driver, " │ ", TuiColors.Body, Pos.Right(stateLabel));
        // Provider purple · "/" white · model yellow.
        var providerLabel = StatusSegment(driver, config.Provider, TuiColors.Purple, Pos.Right(pipe1));
        var slashLabel = StatusSegment(driver, "/", TuiColors.Body, Pos.Right(providerLabel));
        modelLabel = StatusSegment(driver, config.Model, TuiColors.Model, Pos.Right(slashLabel));
        var pipe2 = StatusSegment(driver, " │ ", TuiColors.Body, Pos.Right(modelLabel));
        budgetLabel = StatusSegment(driver, BudgetText(null), BudgetColor(0), Pos.Right(pipe2));
        var pipe3 = StatusSegment(driver, " │ ", TuiColors.Body, Pos.Right(budgetLabel));
        proposalsLabel = StatusSegment(driver, ProposalsText(0), ProposalsColor(0), Pos.Right(pipe3));
        var pipe4 = StatusSegment(driver, " │ ", TuiColors.Body, Pos.Right(proposalsLabel));
        sessionLabel = StatusSegment(driver, $"Session {sessionContext.SessionId}", TuiColors.Body, Pos.Right(pipe4));
        var pipe5 = StatusSegment(driver, " │ ", TuiColors.Body, Pos.Right(sessionLabel));
        exitLabel = StatusSegment(driver, "/exit to quit", TuiColors.Muted, Pos.Right(pipe5));

        Label[] segments = [stateLabel, pipe1, providerLabel, slashLabel, modelLabel, pipe2, budgetLabel, pipe3, proposalsLabel, pipe4, sessionLabel, pipe5, exitLabel];
        statusSegments.AddRange(segments);
        statusBar.Add(segments);
        top.Add(statusBar);
        RecenterStatusBar();
    }

    /// <summary>Re-sizes the status-bar container to the total width of its segments so Pos.Center keeps
    /// it centred as segment texts (state / budget / proposals) change width.</summary>
    private void RecenterStatusBar() =>
        statusBar.Width = Dim.Sized(statusSegments.Sum(s => s.Text.RuneCount));

    /// <summary>The context-budget gauge text: percent full, or a placeholder before the first turn.</summary>
    private static string BudgetText(int? percent) => percent is { } p ? $" Context: {p}%" : " Context: —";

    /// <summary>Gauge colour by fullness: dark green → light green → yellow → red as it fills.</summary>
    private static Color BudgetColor(int percent) => percent switch
    {
        >= 95 => TuiColors.Error,         // red
        >= 80 => TuiColors.Gold,          // yellow
        >= 50 => TuiColors.LightGreen,    // light green
        _ => TuiColors.TabUnfocused,      // dark green
    };

    private static string ProposalsText(int count) => $" Proposals: {count}";

    /// <summary>Gold when there are open proposals awaiting a decision, muted when none.</summary>
    private static Color ProposalsColor(int count) => count > 0 ? TuiColors.Gold : TuiColors.Muted;

    private static Label StatusSegment(ConsoleDriver driver, string text, Color fg, Pos x) => new(text)
    {
        X = x,
        Y = 0,   // relative to the status-bar container, which is itself anchored to the last row
        Height = 1,
        AutoSize = true,
        ColorScheme = Scheme(driver, fg, TuiColors.StatusBg),
    };

    private static ColorScheme Scheme(ConsoleDriver driver, Color fg, Color bg) => new()
    {
        Normal = driver.MakeAttribute(fg, bg),
        Focus = driver.MakeAttribute(fg, bg),
        HotNormal = driver.MakeAttribute(Color.BrightGreen, bg),
        HotFocus = driver.MakeAttribute(Color.BrightGreen, bg),
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
        var scrollbar = new ScrollBarView(view, isVertical: true, showBothScrollIndicator: false)
        {
            // Don't let the scrollbar take keyboard focus — it would interfere with Ctrl+Up/Down
            // cycling focus through the panes (and isn't keyboard-driven here anyway).
            CanFocus = false,
        };

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

        // Open each append-log pane at its newest content (e.g. the conversation shows the latest
        // message, not the oldest history line).
        ScrollToBottom(output);
        ScrollToBottom(reasoning);
        ScrollToBottom(tools);
        ScrollToBottom(debug);

        // Re-assert focus on the compose box now that the loop is up: Application.Run gives initial
        // focus to the first view, so the SetFocus in BuildLayout doesn't stick on its own.
        input.SetFocus();

        // Apply any status-bar values that arrived before the loop was ready.
        ShowOpenProposalCount(lastProposalCount);
        if (lastBudget is { } b)
        {
            UpdateBudget(b.Used, b.Budget, b.Percent);
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

        // Shift+Enter inserts a newline (TextView only binds plain Enter to that, which we repurpose
        // for "send" below, so we insert it ourselves).
        if (key == (Key.Enter | Key.ShiftMask))
        {
            input.InsertText("\n");
            e.Handled = true;
            return;
        }

        // Plain Enter sends. Other keys fall through to the multi-line TextView.
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

        // Repaint now so the input box visibly clears on keypress, then dispatch off the UI thread — the
        // sink runs synchronously up to its first await (DB access / HTTP), which would block repainting.
        input.SetNeedsDisplay();

        SubmitInput(text);
    }

    /// <summary>
    /// Hands submitted input to <see cref="OnInput"/> off the UI thread, surfacing any failure to the
    /// error pane rather than losing it (the in-process sink can't throw; the HTTP sink can).
    /// </summary>
    private void SubmitInput(string text) => _ = Task.Run(async () =>
    {
        try
        {
            await OnInput(text);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    });

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

    /// <summary>
    /// Moves focus to the compose box and inserts <paramref name="text"/> — used when the local peer
    /// starts typing while a (read-only) pane has focus, so the keystroke isn't lost.
    /// </summary>
    private void RedirectTypingToInput(string text)
    {
        input.SetFocus();
        input.InsertText(text);
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
            // The chip's width changes with the text; re-size + relayout so the bar stays centred.
            RecenterStatusBar();
            Application.Top?.LayoutSubviews();
            Application.Top?.SetNeedsDisplay();
        });
    }

    private static string StateText(string state) => $" {state}";

    /// <summary>Colours the state chip: bright green while thinking (states end with "…"), gray when idle.</summary>
    private static Color StateColor(string state) => state.Contains('…') ? TuiColors.Processing : TuiColors.Muted;

    /// <summary>Updates the context-budget gauge from a recalculated budget (marshalled to the UI thread).</summary>
    private void UpdateBudget(int used, int budget, int percent)
    {
        lastBudget = (used, budget, percent);
        UpdateStatusSegment(
            () => budgetLabel,
            budget > 0 ? BudgetText(percent) : $" Context: ~{used} tok",
            BudgetColor(percent));
    }

    /// <summary>Sets a status-bar segment's text + colour on the UI thread and relays out the bar so
    /// the following segments shift to fit the new width.</summary>
    private void UpdateStatusSegment(Func<Label> segment, string text, Color colour)
    {
        if (!ready)
        {
            return;
        }

        Application.MainLoop?.Invoke(() =>
        {
            var label = segment();
            label.Text = text;
            label.ColorScheme = Scheme(Application.Driver, colour, TuiColors.StatusBg);
            RecenterStatusBar();
            Application.Top?.LayoutSubviews();
            Application.Top?.SetNeedsDisplay();
        });
    }

    #endregion
}
