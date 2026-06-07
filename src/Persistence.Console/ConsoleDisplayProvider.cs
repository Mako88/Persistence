using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Runtime;
using SysConsole = System.Console;

namespace Persistence.Console;

/// <summary>
/// Console (terminal) implementation of <see cref="IDisplayProvider"/>. Uses ANSI console
/// colours to distinguish roles and message types.
/// </summary>
[Singleton(typeof(IDisplayProvider), UiMode.Console)]
public class ConsoleDisplayProvider : IDisplayProvider
{
    private readonly IEventBus eventBus;
    private readonly ISessionContext sessionContext;
    private readonly IAppConfig config;

    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? inputLoopCts;

    /// <summary>
    /// Constructor
    /// </summary>
    public ConsoleDisplayProvider(IEventBus eventBus, ISessionContext sessionContext, IAppConfig config)
    {
        this.eventBus = eventBus;
        this.sessionContext = sessionContext;
        this.config = config;
    }

    /// <summary>
    /// Shows the session header, subscribes to events, begins accepting user input,
    /// and returns a task that completes when the session is stopped
    /// </summary>
    public Task Start(CancellationToken ct)
    {
        ShowHeader();

        eventBus.Subscribe<RemotePeerReplied>((_, e) =>
        {
            ShowReply(e.Reply);
            return Task.CompletedTask;
        });

        eventBus.Subscribe<ScheduledEventTriggered>((_, e) =>
        {
            ShowWakeUpEvent(e.Event);
            return Task.CompletedTask;
        });

        eventBus.Subscribe<ToolInvoked>((_, e) =>
        {
            ShowToolUse(e.Tool, e.Request, e.Result);
            return Task.CompletedTask;
        });

        eventBus.Subscribe<ModelReasoningDelta>((_, e) =>
        {
            ShowReasoningDelta(e.Delta);
            return Task.CompletedTask;
        });

        eventBus.Subscribe<ModelThought>((_, e) =>
        {
            ShowThought(e.Thought);
            return Task.CompletedTask;
        });

        inputLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task.Run(() => RunInputLoopAsync(inputLoopCts.Token), inputLoopCts.Token);

        ct.Register(Stop);
        return stopped.Task;
    }

    /// <summary>
    /// Cancels the input loop and shows the session-ended message. Idempotent.
    /// </summary>
    public void Stop()
    {
        if (!stopped.TrySetResult())
        {
            return;
        }

        inputLoopCts?.Cancel();

        SysConsole.ForegroundColor = ConsoleColor.DarkGray;
        SysConsole.WriteLine("  [Session ended.]");
        SysConsole.ResetColor();
    }

    /// <summary>
    /// Shows a thinking/working indicator before a model call
    /// </summary>
    public void ShowThinking(string? label = null)
    {
        SysConsole.ForegroundColor = ConsoleColor.DarkGray;
        SysConsole.WriteLine($"\n  [{label ?? "thinking"}...]\n");
        SysConsole.ResetColor();
    }

    /// <summary>
    /// Shows the remote peer's reply text
    /// </summary>
    public void ShowReply(string reply)
    {
        SysConsole.ForegroundColor = ConsoleColor.Green;
        SysConsole.Write("Assistant: ");
        SysConsole.ResetColor();
        SysConsole.WriteLine(reply);
        SysConsole.WriteLine();
    }

    /// <summary>
    /// Shows the model's reasoning summary
    /// </summary>
    public void ShowReasoning(string summary)
    {
        SysConsole.ForegroundColor = ConsoleColor.DarkGray;
        SysConsole.WriteLine("  [Reasoning]");
        SysConsole.WriteLine($"  {summary.Replace("\n", "\n  ")}");
        SysConsole.ResetColor();
        SysConsole.WriteLine();
    }

    /// <summary>
    /// Appends a streamed chunk of the reasoning summary inline (no per-chunk framing)
    /// </summary>
    public void ShowReasoningDelta(string delta)
    {
        SysConsole.ForegroundColor = ConsoleColor.DarkGray;
        SysConsole.Write(delta);
        SysConsole.ResetColor();
    }

    /// <summary>
    /// Shows an open thought recorded via a Think action
    /// </summary>
    public void ShowThought(string thought)
    {
        SysConsole.ForegroundColor = ConsoleColor.DarkGray;
        SysConsole.WriteLine($"  💭 {thought}");
        SysConsole.ResetColor();
        SysConsole.WriteLine();
    }

    /// <summary>
    /// Shows a tool/command invocation: its name, request, and result
    /// </summary>
    public void ShowToolUse(string tool, string request, string result)
    {
        SysConsole.ForegroundColor = ConsoleColor.DarkMagenta;
        SysConsole.Write($"  [Tool: {tool}] ");
        SysConsole.ResetColor();
        SysConsole.WriteLine($"{request} → {result}");
    }

    /// <summary>
    /// Shows a wake-up event notification
    /// </summary>
    public void ShowWakeUpEvent(ScheduledEventEntity evt)
    {
        SysConsole.ForegroundColor = ConsoleColor.DarkCyan;
        SysConsole.WriteLine();
        SysConsole.WriteLine($"  [WAKE-UP: {evt.Name}]");
        SysConsole.ResetColor();
    }

    /// <summary>
    /// Shows an error message
    /// </summary>
    public void ShowError(string message)
    {
        SysConsole.ForegroundColor = ConsoleColor.Red;
        SysConsole.WriteLine($"  [Error: {message}]");
        SysConsole.ResetColor();
    }

    /// <summary>
    /// Shows a debug info string
    /// </summary>
    public void ShowDebugInfo(string info)
    {
        SysConsole.ForegroundColor = ConsoleColor.DarkGreen;
        SysConsole.WriteLine(info);
        SysConsole.ResetColor();
        SysConsole.WriteLine();
    }

    /// <summary>
    /// Shows recent chat history on startup
    /// </summary>
    public void ShowChatHistory(IReadOnlyList<(string Role, string Content, DateTimeOffset Timestamp)> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        SysConsole.ForegroundColor = ConsoleColor.DarkGray;
        SysConsole.WriteLine("  — Recent History —");
        SysConsole.ResetColor();

        foreach (var (role, content, timestamp) in messages)
        {
            var timeLabel = timestamp.LocalDateTime.ToString("g");

            if (role == "user")
            {
                SysConsole.ForegroundColor = ConsoleColor.DarkGray;
                SysConsole.Write($"  [{timeLabel}] ");
                SysConsole.ForegroundColor = ConsoleColor.DarkCyan;
                SysConsole.Write("You: ");
                SysConsole.ResetColor();
                SysConsole.WriteLine(content);
            }
            else
            {
                SysConsole.ForegroundColor = ConsoleColor.DarkGray;
                SysConsole.Write($"  [{timeLabel}] ");
                SysConsole.ForegroundColor = ConsoleColor.DarkGreen;
                SysConsole.Write("Assistant: ");
                SysConsole.ResetColor();
                SysConsole.WriteLine(content);
            }
        }

        SysConsole.ForegroundColor = ConsoleColor.DarkGray;
        SysConsole.WriteLine("  — End of History —");
        SysConsole.ResetColor();
        SysConsole.WriteLine();
    }

    /// <summary>
    /// Shows an unrecognised slash-command message
    /// </summary>
    public void ShowUnknownCommand(string command)
    {
        SysConsole.ForegroundColor = ConsoleColor.DarkGray;
        SysConsole.WriteLine($"  Unknown command: {command}");
        SysConsole.ResetColor();
        SysConsole.WriteLine();
    }

    /// <summary>
    /// Shows a notification that a message has been queued for the next iteration
    /// </summary>
    public void ShowMessageQueued(string input)
    {
        SysConsole.ForegroundColor = ConsoleColor.DarkYellow;
        SysConsole.WriteLine($"  [Queued: {input}]");
        SysConsole.ResetColor();
    }

    #region Private

    /// <summary>
    /// Shows the session header with session ID and model info
    /// </summary>
    private void ShowHeader()
    {
        SysConsole.WriteLine($"=== Persistence — MVP Console ===");
        SysConsole.WriteLine($"Session: {sessionContext.SessionId}");
        SysConsole.WriteLine($"Provider: {config.Provider} | Model: {config.Model}");
        SysConsole.WriteLine("Type '/exit' or '/quit' to end the session.");
        SysConsole.WriteLine("Type '/debug' to toggle debug mode.");
        SysConsole.WriteLine("=================================");
        SysConsole.WriteLine();
    }

    /// <summary>
    /// Reads input lines and forwards them for processing until cancelled
    /// </summary>
    private async Task RunInputLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ShowPrompt();

            var input = await Task.Run(SysConsole.ReadLine, ct);

            if (input == null)
            {
                continue;
            }

            eventBus.FireAndForget(this, new DisplayInputReceived(input),
                ex => ShowError(ex.Message));
        }
    }

    /// <summary>
    /// Shows the input prompt
    /// </summary>
    private void ShowPrompt()
    {
        SysConsole.ForegroundColor = ConsoleColor.Cyan;
        SysConsole.Write("You: ");
        SysConsole.ResetColor();
    }

    #endregion
}
