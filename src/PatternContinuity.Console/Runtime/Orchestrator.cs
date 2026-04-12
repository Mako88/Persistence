using PatternContinuity.Actions;
using PatternContinuity.Models;
using PatternContinuity.Runtime;

namespace PatternContinuity.Console.Runtime;

/// <summary>
/// Console-specific orchestrator. Handles all terminal I/O and delegates
/// core turn logic to <see cref="TurnEngine"/>.
/// </summary>
public class ConsoleOrchestrator
{
    private readonly TurnEngine _engine;
    private bool _wakeUpFiredThisCycle;
    private DateTimeOffset? _lastWakeDebugLog;

    public ConsoleOrchestrator(TurnEngine engine)
    {
        _engine = engine;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        PrintHeader();
        PrintRestoredHistory();

        while (!ct.IsCancellationRequested)
        {
            System.Console.ForegroundColor = ConsoleColor.Cyan;
            System.Console.Write("You: ");
            System.Console.ResetColor();

            var input = await ReadLineWithWakeUpPolling(ct);

            if (input == null)
            {
                EndSession();
                break;
            }

            _wakeUpFiredThisCycle = false;

            if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)
                || input.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                EndSession();
                break;
            }

            input = input.Trim();
            if (string.IsNullOrEmpty(input)) continue;

            if (input.StartsWith('/'))
            {
                HandleCommand(input);
                continue;
            }

            await ProcessTurnAsync(input, ct);
        }
    }

    private void PrintHeader()
    {
        var config = _engine.Config;
        System.Console.WriteLine("=== Pattern Continuity Infrastructure — MVP Console ===");
        System.Console.WriteLine($"Session: {_engine.SessionId}");
        System.Console.WriteLine($"Active person: {config.ActivePersonId ?? "(none)"}");
        System.Console.WriteLine($"Model: {config.ApiProvider}/{config.ModelName}");
        System.Console.WriteLine($"Reflection: every {config.ReflectionFrequency} turn(s)");
        System.Console.WriteLine("Type 'exit' or 'quit' to end the session.");
        System.Console.WriteLine("Type '/debug' to show current layer counts.");
        System.Console.WriteLine("Type '/tokens' to show token budget usage.");
        System.Console.WriteLine("Type '/wake' to show pending wake-up timer.");
        System.Console.WriteLine("======================================================");
        System.Console.WriteLine();
    }

    private void PrintRestoredHistory()
    {
        var restored = _engine.GetRecentMessages();
        if (restored.Count == 0) return;

        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.WriteLine("--- Recent conversation ---");
        System.Console.ResetColor();
        foreach (var msg in restored)
        {
            if (msg.Role == "user")
            {
                System.Console.ForegroundColor = ConsoleColor.DarkCyan;
                System.Console.Write("You: ");
            }
            else
            {
                System.Console.ForegroundColor = ConsoleColor.DarkGreen;
                System.Console.Write("Assistant: ");
            }
            System.Console.ResetColor();
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine(msg.Content);
            System.Console.ResetColor();
        }
        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.WriteLine("---------------------------");
        System.Console.ResetColor();
        System.Console.WriteLine();
    }

    private async Task<string?> ReadLineWithWakeUpPolling(CancellationToken ct)
    {
        var inputBuffer = new System.Text.StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            // Drain any available keystrokes
            while (System.Console.KeyAvailable)
            {
                var key = System.Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                {
                    System.Console.WriteLine();
                    return inputBuffer.ToString();
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (inputBuffer.Length > 0)
                    {
                        inputBuffer.Remove(inputBuffer.Length - 1, 1);
                        System.Console.Write("\b \b");
                    }
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    while (inputBuffer.Length > 0)
                    {
                        inputBuffer.Remove(inputBuffer.Length - 1, 1);
                        System.Console.Write("\b \b");
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    inputBuffer.Append(key.KeyChar);
                    System.Console.Write(key.KeyChar);
                }
            }

            // Check for due wake-up events
            if (!_wakeUpFiredThisCycle)
            {
                var diag = _engine.GetWakeUpDiagnostics();
                if (diag != null)
                {
                    // DEBUG: Log wake-up check details (throttled to once per 30s)
                    var nowUtc = DateTimeOffset.UtcNow;
                    if (_lastWakeDebugLog == null || (nowUtc - _lastWakeDebugLog.Value).TotalSeconds >= 30)
                    {
                        _lastWakeDebugLog = nowUtc;
                        System.Console.ForegroundColor = ConsoleColor.DarkGray;
                        System.Console.Write($"\r[wake-debug] pending id={diag.EventId[..8]} scheduledFor={diag.ScheduledForRaw} parseOk={diag.ParseOk}");
                        if (diag.ParseOk)
                            System.Console.Write($" parsed={diag.ParsedTime:o} now={diag.NowUtc:o} due={diag.IsDue}");
                        System.Console.WriteLine();
                        System.Console.ResetColor();
                        System.Console.ForegroundColor = ConsoleColor.Cyan;
                        System.Console.Write("You: ");
                        System.Console.ResetColor();
                        System.Console.Write(inputBuffer.ToString());
                    }

                    if (diag.IsDue)
                    {
                        var evt = _engine.CheckForDueWakeUp();
                        if (evt != null)
                        {
                            // Save any partial input, clear the line
                            var partialInput = inputBuffer.ToString();
                            if (partialInput.Length > 0)
                            {
                                System.Console.Write(new string('\b', partialInput.Length));
                                System.Console.Write(new string(' ', partialInput.Length));
                                System.Console.Write(new string('\b', partialInput.Length));
                            }
                            System.Console.Write("\r");
                            System.Console.Write(new string(' ', 5 + partialInput.Length));
                            System.Console.Write("\r");

                            await ProcessWakeUpAsync(evt, ct);
                            _wakeUpFiredThisCycle = true;

                            // Re-print prompt and restore any partial input
                            System.Console.ForegroundColor = ConsoleColor.Cyan;
                            System.Console.Write("You: ");
                            System.Console.ResetColor();
                            if (partialInput.Length > 0)
                                System.Console.Write(partialInput);
                        }
                    }
                }
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return null;
    }

    private async Task ProcessTurnAsync(string userMessage, CancellationToken ct)
    {
        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.WriteLine("  [thinking...]");
        System.Console.ResetColor();

        var result = await _engine.ProcessTurnAsync(userMessage, ct);

        if (result.IsApiError)
        {
            PrintError($"API Error: {result.ApiErrorMessage}");
            return;
        }

        // Display reply
        PrintAssistantReply(result.AssistantReply);
        PrintActionResults(result.ActionResults);

        // Display truncation recovery if it happened
        if (result.RecoveryResult != null)
        {
            System.Console.ForegroundColor = ConsoleColor.DarkYellow;
            System.Console.WriteLine("  [Response was truncated — recovery executed]");
            System.Console.ResetColor();

            if (!string.IsNullOrWhiteSpace(result.RecoveryResult.AssistantReply))
            {
                System.Console.ForegroundColor = ConsoleColor.DarkGreen;
                System.Console.Write("  [Recovery] Assistant: ");
                System.Console.ResetColor();
                System.Console.WriteLine(result.RecoveryResult.AssistantReply);
                System.Console.WriteLine();
            }
            PrintActionResults(result.RecoveryResult.ActionResults, "recovery");
        }

        // Display read follow-up if it happened
        if (result.ReadFollowUpResult != null)
        {
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine("  [processing read results...]");
            System.Console.ResetColor();

            if (result.ReadFollowUpResult.IsApiError)
            {
                PrintError($"Read follow-up API Error: {result.ReadFollowUpResult.ApiErrorMessage}");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(result.ReadFollowUpResult.AssistantReply))
                    PrintAssistantReply(result.ReadFollowUpResult.AssistantReply);
                PrintActionResults(result.ReadFollowUpResult.ActionResults, "follow-up");
            }
        }

        // Display reflection results if it happened
        if (result.ReflectionResult != null)
        {
            PrintReflectionResults(result.ReflectionResult);
        }
    }

    private async Task ProcessWakeUpAsync(ScheduledEvent evt, CancellationToken ct)
    {
        System.Console.ForegroundColor = ConsoleColor.DarkCyan;
        System.Console.WriteLine();
        System.Console.WriteLine($"  [WAKE-UP: {evt.Reason}]");
        System.Console.ResetColor();

        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.WriteLine("  [thinking (wake)...]");
        System.Console.ResetColor();

        var result = await _engine.ProcessWakeUpAsync(evt, ct);

        if (result.IsApiError)
        {
            PrintError($"Wake API Error: {result.ApiErrorMessage}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.AssistantReply))
        {
            System.Console.ForegroundColor = ConsoleColor.DarkGreen;
            System.Console.Write("  [Wake] Assistant: ");
            System.Console.ResetColor();
            System.Console.WriteLine(result.AssistantReply);
            System.Console.WriteLine();
        }

        PrintActionResults(result.ActionResults, "wake");
    }

    private void HandleCommand(string command)
    {
        switch (command.ToLower())
        {
            case "/debug":
                var debugResults = _engine.GetDebugInfo();
                System.Console.ForegroundColor = ConsoleColor.DarkGray;
                foreach (var r in debugResults)
                    System.Console.WriteLine($"  {r.Summary}");
                System.Console.ResetColor();
                System.Console.WriteLine();
                break;

            case "/tokens":
                var usage = _engine.GetTokenUsage();
                System.Console.ForegroundColor = ConsoleColor.DarkGray;
                System.Console.WriteLine($"  Input context: ~{usage.InputTokens} tokens (budget: {usage.MaxInputBudget})");
                System.Console.WriteLine($"  Output limit:  {usage.MaxOutputTokens} tokens");
                System.Console.WriteLine($"  Conversation window: {usage.WindowMessageCount} message(s)");
                System.Console.ResetColor();
                System.Console.WriteLine();
                break;

            case "/wake":
                var pendingWake = _engine.GetPendingWakeUp();
                System.Console.ForegroundColor = ConsoleColor.DarkGray;
                if (pendingWake != null)
                {
                    var due = DateTimeOffset.TryParse(pendingWake.ScheduledFor, out var dt)
                        ? dt.ToLocalTime().ToString("HH:mm:ss")
                        : pendingWake.ScheduledFor;
                    System.Console.WriteLine($"  Pending wake-up at {due}: {pendingWake.Reason}");
                }
                else
                {
                    System.Console.WriteLine("  No pending wake-up timer.");
                }
                System.Console.ResetColor();
                System.Console.WriteLine();
                break;

            default:
                System.Console.ForegroundColor = ConsoleColor.DarkGray;
                System.Console.WriteLine($"  Unknown command: {command}");
                System.Console.ResetColor();
                System.Console.WriteLine();
                break;
        }
    }

    private void EndSession()
    {
        _engine.EndSession();
        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.WriteLine("  [Session ended.]");
        System.Console.ResetColor();
    }

    // ── Display helpers ──

    private static void PrintAssistantReply(string reply)
    {
        System.Console.ForegroundColor = ConsoleColor.Green;
        System.Console.Write("Assistant: ");
        System.Console.ResetColor();
        System.Console.WriteLine(reply);
        System.Console.WriteLine();
    }

    private static void PrintActionResults(List<ActionResult> results, string? label = null)
    {
        if (results.Count == 0) return;

        var prefix = label != null ? $"{label}: " : "";
        System.Console.ForegroundColor = ConsoleColor.DarkYellow;
        System.Console.WriteLine($"  [{prefix}{results.Count} action(s) processed]");
        foreach (var r in results)
            System.Console.WriteLine($"    {r.Status}: {r.Action} — {r.Summary}");
        System.Console.ResetColor();
        System.Console.WriteLine();
    }

    private static void PrintReflectionResults(Services.ReflectionResult result)
    {
        if (result.ActionResults.Count > 0)
        {
            System.Console.ForegroundColor = ConsoleColor.DarkMagenta;
            System.Console.WriteLine($"  [reflection: {result.ActionResults.Count} action(s)]");
            foreach (var r in result.ActionResults)
                System.Console.WriteLine($"    {r.Status}: {r.Action} — {r.Summary}");
            System.Console.ResetColor();
        }
        else
        {
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine("  [reflection: no changes]");
            System.Console.ResetColor();
        }
        System.Console.WriteLine();
    }

    private static void PrintError(string message)
    {
        System.Console.ForegroundColor = ConsoleColor.Red;
        System.Console.WriteLine($"  [{message}]");
        System.Console.ResetColor();
    }
}
