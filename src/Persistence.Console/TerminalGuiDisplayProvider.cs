using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using System.Text;
using Terminal.Gui;

namespace Persistence.Console;

/// <summary>
/// Terminal.Gui (v1) implementation of <see cref="IDisplayProvider"/>. Runs the TUI
/// main loop on a dedicated thread and renders into a multi-pane layout: a framed
/// conversation pane on the left, a tabbed side column (reasoning / tools / debug /
/// history) on the right, a multi-line compose box, and a status line at the bottom.
///
/// All pane mutations are marshalled onto the UI thread via
/// <see cref="MainLoop.Invoke"/>. Output that arrives before the loop is ready
/// (e.g. chat history shown during initialisation) is buffered and flushed on load.
/// </summary>
[Singleton(typeof(IDisplayProvider), UiMode.Tui)]
public class TerminalGuiDisplayProvider : IDisplayProvider
{
    private readonly IEventBus eventBus;
    private readonly ISessionContext sessionContext;
    private readonly IAppConfig config;

    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Per-pane text buffers are the source of truth; the TextViews render from them.
    private readonly object sync = new();
    private readonly StringBuilder outputBuffer = new();
    private readonly StringBuilder reasoningBuffer = new();
    private readonly StringBuilder toolsBuffer = new();
    private readonly StringBuilder debugBuffer = new();
    private readonly StringBuilder historyBuffer = new();

    private TextView output = null!;
    private TextView reasoning = null!;
    private TextView tools = null!;
    private TextView debug = null!;
    private TextView history = null!;
    private TextView input = null!;
    private TabView tabView = null!;
    private Label statusLabel = null!;

    private Thread? uiThread;
    private volatile bool ready;

    public TerminalGuiDisplayProvider(IEventBus eventBus, ISessionContext sessionContext, IAppConfig config)
    {
        this.eventBus = eventBus;
        this.sessionContext = sessionContext;
        this.config = config;
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

    // ── Output ───────────────────────────────────────────────────

    public void ShowReply(string reply)
    {
        Append(outputBuffer, () => output, $"Assistant: {reply}\n\n");
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

    public void ShowMessageQueued(string text) => Append(outputBuffer, () => output, $"[Queued: {text}]\n");

    public void ShowUnknownCommand(string command) => Append(outputBuffer, () => output, $"Unknown command: {command}\n\n");

    public void ShowDebugInfo(string info) => Append(debugBuffer, () => debug, info + "\n");

    public void ShowReasoning(string summary) => Append(reasoningBuffer, () => reasoning, summary + "\n\n");

    public void ShowReasoningDelta(string delta) => Append(reasoningBuffer, () => reasoning, delta);

    public void ShowToolUse(string tool, string request, string result) =>
        Append(toolsBuffer, () => tools, $"▸ {tool}\n  request:  {request}\n  response: {result}\n\n");

    public void ShowChatHistory(IReadOnlyList<(string Role, string Content, DateTimeOffset Timestamp)> messages)
    {
        foreach (var (role, content, ts) in messages)
        {
            var who = role == "user" ? "You" : "Assistant";
            Append(historyBuffer, () => history, $"[{ts.LocalDateTime:g}] {who}: {content}\n\n");
        }
    }

    public void ShowThinking(string? label = null) => SetStatus($"{label ?? "thinking"}…");

    // ── UI construction & lifecycle ──────────────────────────────

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

        // Base dark theme. Read-only panes keep their colour on focus (Focus == Normal)
        // so they never flash to a different background.
        var baseTheme = Scheme(driver, Color.White, Color.Black);
        Colors.TopLevel = baseTheme;
        Colors.Base = baseTheme;
        Colors.Dialog = baseTheme;
        Colors.Menu = baseTheme;
        top.ColorScheme = baseTheme;

        // Each pane gets a distinct foreground so content is identifiable at a glance.
        var conversationScheme = Scheme(driver, Color.White, Color.Black);
        var reasoningScheme = Scheme(driver, Color.BrightCyan, Color.Black);
        var toolsScheme = Scheme(driver, Color.BrightGreen, Color.Black);
        var debugScheme = Scheme(driver, Color.Gray, Color.Black);
        var historyScheme = Scheme(driver, Color.White, Color.Black);

        // Reserve rows at the bottom: 4 for the compose box, 1 for the status bar.
        const int bottomRows = 5;

        var outputFrame = new FrameView("Conversation")
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(70),
            Height = Dim.Fill(bottomRows),
            ColorScheme = baseTheme,
        };
        // outputFrame is the stable parent, so output always has a SuperView —
        // required before constructing its ScrollBarView.
        // Rules are first-match-wins per column: register whole-line marker tints first,
        // then the role label/body split.
        var conversation = MakeColoredPaneView(conversationScheme);
        conversation
            .ColorLinesStartingWith("[Error", Color.BrightRed)
            .ColorLinesStartingWith("[WAKE-UP", Color.BrightMagenta)
            .ColorLinesStartingWith("[Queued", Color.BrightYellow)
            .ColorLinesStartingWith("Unknown command", Color.BrightYellow)
            .ColorPrefix("You: ", Color.BrightCyan, Color.Cyan)
            .ColorPrefix("Assistant: ", Color.BrightGreen, Color.White);
        output = conversation;
        outputFrame.Add(output);
        AddScrollbar(output);

        tabView = new TabView
        {
            X = Pos.Right(outputFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(bottomRows),
            ColorScheme = baseTheme,
        };

        // Each tab's content is a stable container View that owns the TextView and its
        // scrollbar. TabView only attaches the *selected* tab's view to the visual tree,
        // so without this wrapper a non-selected pane's TextView would have a null
        // SuperView and ScrollBarView's ctor would throw.
        reasoning = MakePaneView(reasoningScheme);

        // Tools pane: highlight the "▸ name" header and the request/response labels so each
        // tool invocation is scannable at a glance.
        var toolsView = MakeColoredPaneView(toolsScheme);
        toolsView
            .ColorLinesStartingWith("▸", Color.BrightYellow)
            .ColorSubstring("request:", Color.Cyan)
            .ColorSubstring("response:", Color.BrightGreen);
        tools = toolsView;

        // Debug pane: highlight the request/response headers, sensory block, fragment
        // headers, and field labels so the dumps are scannable. Base text stays gray.
        var debugView = MakeColoredPaneView(debugScheme);
        debugView
            .ColorLinesStartingWith("Request", Color.BrightYellow)
            .ColorLinesStartingWith("Response", Color.BrightYellow)
            .ColorLinesStartingWith("[Sensory]", Color.BrightMagenta)
            .ColorPattern(@"^\[#\d+[^\]]*\]", Color.BrightCyan)
            .ColorPattern(@"^[A-Za-z][\w ()/]*:", Color.Cyan);
        debug = debugView;

        // History shares the conversation's role coloring (lines are "[time] You: …"),
        // with the timestamp dimmed so the role/content stands out.
        var historyView = MakeColoredPaneView(historyScheme);
        historyView
            .ColorPattern(@"^\[[^\]]+\]", Color.DarkGray)
            .ColorSubstring("You:", Color.BrightCyan)
            .ColorSubstring("Assistant:", Color.BrightGreen);
        history = historyView;

        tabView.AddTab(new TabView.Tab("Reasoning", WrapPane(reasoning, reasoningScheme)), andSelect: true);
        tabView.AddTab(new TabView.Tab("Tools", WrapPane(tools, toolsScheme)), andSelect: false);
        tabView.AddTab(new TabView.Tab("Debug", WrapPane(debug, debugScheme)), andSelect: false);
        tabView.AddTab(new TabView.Tab("History", WrapPane(history, historyScheme)), andSelect: false);

        // Multi-line compose box. Enter sends; Shift+Enter inserts a newline.
        var inputFrame = new FrameView("Compose  —  Enter to send, Shift+Enter for newline")
        {
            X = 0,
            Y = Pos.AnchorEnd(bottomRows),
            Width = Dim.Fill(),
            Height = bottomRows - 1,
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

        // Status bar: white on dark gray — visible but not glaring.
        statusLabel = new Label(BuildStatusText("idle"))
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            AutoSize = false,
            ColorScheme = Scheme(driver, Color.White, Color.DarkGray),
        };

        top.Add(outputFrame, tabView, inputFrame, statusLabel);
        input.SetFocus();
    }

    private static ColorScheme Scheme(ConsoleDriver driver, Color fg, Color bg) => new()
    {
        Normal = driver.MakeAttribute(fg, bg),
        Focus = driver.MakeAttribute(fg, bg),
        HotNormal = driver.MakeAttribute(Color.BrightYellow, bg),
        HotFocus = driver.MakeAttribute(Color.BrightYellow, bg),
        Disabled = driver.MakeAttribute(Color.DarkGray, bg),
    };

    private static TextView MakePaneView(ColorScheme scheme) => new()
    {
        ReadOnly = true,
        WordWrap = true,
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(),
        ColorScheme = scheme,
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
            debug.Text = debugBuffer.ToString();
            history.Text = historyBuffer.ToString();
        }

        // Re-apply the tab selection now that layout is complete. TabView computes the
        // content view's bounds when a tab is selected; at AddTab time (before layout)
        // those bounds are wrong, so the first tab renders blank until re-selected.
        var selected = tabView.SelectedTab;
        tabView.SelectedTab = null;
        tabView.SelectedTab = selected;
    }

    private void OnInputKeyPress(View.KeyEventEventArgs e)
    {
        // Plain Enter sends. Shift+Enter (and other modifiers) fall through so the
        // multi-line TextView can insert a newline.
        if (e.KeyEvent.Key != Key.Enter)
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

        // Echo the submitted input to the conversation pane — unlike a raw console,
        // the TextView doesn't leave the typed text visible anywhere.
        Append(outputBuffer, () => output, $"You: {text}\n\n");

        // Repaint now so the input box visibly clears on keypress, then dispatch the turn
        // off the UI thread — the subscriber chain runs synchronously up to its first
        // await (DB access, model call), which would otherwise block repainting.
        input.SetNeedsDisplay();

        eventBus.FireAndForget(this, new DisplayInputReceived(text),
            ex => ShowError(ex.Message));
    }

    // ── Helpers ──────────────────────────────────────────────────

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
            v.MoveEnd();
        });
    }

    private void SetStatus(string state)
    {
        if (!ready)
        {
            return;
        }

        Application.MainLoop?.Invoke(() => statusLabel.Text = BuildStatusText(state));
    }

    private string BuildStatusText(string state) =>
        $" {state}  │  Session {sessionContext.SessionId}  │  {config.Provider} / {config.Model}  │  /exit to quit";
}
