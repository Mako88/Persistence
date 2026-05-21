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
[Singleton]
public class ConsoleDisplayProvider : IDisplayProvider
{
    private readonly IEventBus eventBus;
    private readonly ISessionContext sessionContext;
    private readonly IAppConfig config;

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
    /// Shows the session header, subscribes to events, and begins accepting user input
    /// </summary>
    public void Start(CancellationToken ct)
    {
        ShowHeader();

        _ = eventBus.Subscribe<DigitalColleagueReplied>((_, e) =>
        {
            ShowReply(e.Reply);
            return Task.CompletedTask;
        });

        _ = eventBus.Subscribe<ScheduledEventTriggered>((_, e) =>
        {
            ShowWakeUpEvent(e.Event);
            return Task.CompletedTask;
        });

        inputLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => RunInputLoopAsync(inputLoopCts.Token), inputLoopCts.Token);
    }

    /// <summary>
    /// Cancels the input loop and shows the session-ended message
    /// </summary>
    public void Stop()
    {
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
    /// Shows the digital colleague's reply text
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
    /// Shows an unrecognised slash-command message
    /// </summary>
    public void ShowUnknownCommand(string command)
    {
        SysConsole.ForegroundColor = ConsoleColor.DarkGray;
        SysConsole.WriteLine($"  Unknown command: {command}");
        SysConsole.ResetColor();
        SysConsole.WriteLine();
    }

    // ── Private ──────────────────────────────────────────────────

    /// <summary>
    /// Shows the session header with session ID and model info
    /// </summary>
    private void ShowHeader()
    {
        SysConsole.WriteLine($"=== Persistence — MVP Console ===");
        SysConsole.WriteLine($"Session: {sessionContext.SessionId}");
        SysConsole.WriteLine($"Model: {config.ModelName}");
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

            await eventBus.PublishAsync(this, new DisplayInputReceived(input));
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
}
