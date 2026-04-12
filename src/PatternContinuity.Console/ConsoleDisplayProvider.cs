using Persistence.Actions;
using Persistence.Config;
using Persistence.DI;
using Persistence.Models;
using Persistence.Runtime;
using Persistence.Services;

namespace Persistence.Display
{
    /// <summary>
    /// Console-specific display provider for terminal-based UI
    /// </summary>
    [Singleton]
    public class ConsoleDisplayProvider : IDisplayProvider
    {
        /// <summary>
        /// Constructor
        /// </summary>
        public ConsoleDisplayProvider()
        {
        }

        /// <summary>
        /// Display the session header and restored conversation history
        /// </summary>
        public void Initialize(string sessionId, IAppConfig config, List<Message> history)
        {
            Console.WriteLine("=== Pattern Continuity Infrastructure — MVP Console ===");
            Console.WriteLine($"Session: {sessionId}");
            Console.WriteLine($"Active person: {config.ActivePersonId ?? "(none)"}");
            Console.WriteLine($"Model: {config.ApiProvider}/{config.ModelName}");
            Console.WriteLine($"Reflection: every {config.ReflectionFrequency} turn(s)");
            Console.WriteLine("Type 'exit' or 'quit' to end the session.");
            Console.WriteLine("Type '/debug' to show current layer counts.");
            Console.WriteLine("Type '/tokens' to show token budget usage.");
            Console.WriteLine("Type '/wake' to show pending wake-up timer.");
            Console.WriteLine("======================================================");
            Console.WriteLine();

            if (history.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("--- Recent conversation ---");
                Console.ResetColor();

                foreach (var msg in history)
                {
                    if (msg.Role == "user")
                    {
                        Console.ForegroundColor = ConsoleColor.DarkCyan;
                        Console.Write("You: ");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                        Console.Write("Assistant: ");
                    }
                    Console.ResetColor();
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine(msg.Content);
                    Console.ResetColor();
                }

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("---------------------------");
                Console.ResetColor();
                Console.WriteLine();
            }
        }

        /// <summary>
        /// Request input from the user, with support for cancellation
        /// </summary>
        public async Task<string?> RequestInputAsync(CancellationToken ct)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("You: ");
            Console.ResetColor();

            var inputBuffer = new System.Text.StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    if (key.Key == ConsoleKey.Enter)
                    {
                        Console.WriteLine();
                        return inputBuffer.ToString();
                    }
                    else if (key.Key == ConsoleKey.Backspace)
                    {
                        if (inputBuffer.Length > 0)
                        {
                            inputBuffer.Remove(inputBuffer.Length - 1, 1);
                            Console.Write("\b \b");
                        }
                    }
                    else if (key.Key == ConsoleKey.Escape)
                    {
                        while (inputBuffer.Length > 0)
                        {
                            inputBuffer.Remove(inputBuffer.Length - 1, 1);
                            Console.Write("\b \b");
                        }
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        inputBuffer.Append(key.KeyChar);
                        Console.Write(key.KeyChar);
                    }
                }

                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            return null;
        }

        /// <summary>
        /// Display a thinking indicator
        /// </summary>
        public void ShowThinking(string? label = null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [{label ?? "thinking"}...]");
            Console.ResetColor();
        }

        /// <summary>
        /// Display the assistant's reply
        /// </summary>
        public void ShowReply(string reply)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("Assistant: ");
            Console.ResetColor();
            Console.WriteLine(reply);
            Console.WriteLine();
        }

        /// <summary>
        /// Display the results of executed actions
        /// </summary>
        public void ShowActionResults(List<ActionResult> results, string? label = null)
        {
            if (results.Count == 0) return;

            var prefix = label != null ? $"{label}: " : "";
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  [{prefix}{results.Count} action(s) processed]");
            foreach (var r in results)
                Console.WriteLine($"    {r.Status}: {r.Action} — {r.Summary}");
            Console.ResetColor();
            Console.WriteLine();
        }

        /// <summary>
        /// Display a truncation recovery result
        /// </summary>
        public void ShowRecovery(TurnResult recovery)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("  [Response was truncated — recovery executed]");
            Console.ResetColor();

            if (!string.IsNullOrWhiteSpace(recovery.AssistantReply))
            {
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.Write("  [Recovery] Assistant: ");
                Console.ResetColor();
                Console.WriteLine(recovery.AssistantReply);
                Console.WriteLine();
            }

            ShowActionResults(recovery.ActionResults, "recovery");
        }

        /// <summary>
        /// Display a read follow-up result
        /// </summary>
        public void ShowReadFollowUp(TurnResult followUp)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  [processing read results...]");
            Console.ResetColor();

            if (followUp.IsApiError)
            {
                ShowError($"Read follow-up API Error: {followUp.ApiErrorMessage}");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(followUp.AssistantReply))
                    ShowReply(followUp.AssistantReply);
                ShowActionResults(followUp.ActionResults, "follow-up");
            }
        }

        /// <summary>
        /// Display reflection results
        /// </summary>
        public void ShowReflection(ReflectionResult reflection)
        {
            if (reflection.ActionResults.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkMagenta;
                Console.WriteLine($"  [reflection: {reflection.ActionResults.Count} action(s)]");
                foreach (var r in reflection.ActionResults)
                    Console.WriteLine($"    {r.Status}: {r.Action} — {r.Summary}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  [reflection: no changes]");
                Console.ResetColor();
            }
            Console.WriteLine();
        }

        /// <summary>
        /// Display a wake-up event notification
        /// </summary>
        public void ShowWakeUpEvent(string reason)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine();
            Console.WriteLine($"  [WAKE-UP: {reason}]");
            Console.ResetColor();
        }

        /// <summary>
        /// Display a wake-up result
        /// </summary>
        public void ShowWakeUpResult(WakeUpResult result)
        {
            if (result.IsApiError)
            {
                ShowError($"Wake API Error: {result.ApiErrorMessage}");
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.AssistantReply))
            {
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.Write("  [Wake] Assistant: ");
                Console.ResetColor();
                Console.WriteLine(result.AssistantReply);
                Console.WriteLine();
            }

            ShowActionResults(result.ActionResults, "wake");
        }

        /// <summary>
        /// Display an error message
        /// </summary>
        public void ShowError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [{message}]");
            Console.ResetColor();
        }

        /// <summary>
        /// Display debug info results
        /// </summary>
        public void ShowDebugInfo(List<ActionResult> debugResults)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            foreach (var r in debugResults)
                Console.WriteLine($"  {r.Summary}");
            Console.ResetColor();
            Console.WriteLine();
        }

        /// <summary>
        /// Display token usage information
        /// </summary>
        public void ShowTokenUsage(TokenUsageInfo usage)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Input context: ~{usage.InputTokens} tokens (budget: {usage.MaxInputBudget})");
            Console.WriteLine($"  Output limit:  {usage.MaxOutputTokens} tokens");
            Console.WriteLine($"  Conversation window: {usage.WindowMessageCount} message(s)");
            Console.ResetColor();
            Console.WriteLine();
        }

        /// <summary>
        /// Display wake-up timer status
        /// </summary>
        public void ShowWakeStatus(ScheduledEvent? pending)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            if (pending != null)
            {
                var due = DateTimeOffset.TryParse(pending.ScheduledFor, out var dt)
                    ? dt.ToLocalTime().ToString("HH:mm:ss")
                    : pending.ScheduledFor;
                Console.WriteLine($"  Pending wake-up at {due}: {pending.Reason}");
            }
            else
            {
                Console.WriteLine("  No pending wake-up timer.");
            }
            Console.ResetColor();
            Console.WriteLine();
        }

        /// <summary>
        /// Display an unknown command message
        /// </summary>
        public void ShowUnknownCommand(string command)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Unknown command: {command}");
            Console.ResetColor();
            Console.WriteLine();
        }

        /// <summary>
        /// Display the session ended message
        /// </summary>
        public void ShowSessionEnded()
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  [Session ended.]");
            Console.ResetColor();
        }

        /// <summary>
        /// Display wake-up debug diagnostics
        /// </summary>
        public void ShowWakeDebug(WakeUpDiagnostics diag, string currentInput)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"\r[wake-debug] pending id={diag.EventId[..8]} scheduledFor={diag.ScheduledForRaw} parseOk={diag.ParseOk}");
            if (diag.ParseOk)
                Console.Write($" parsed={diag.ParsedTime:o} now={diag.NowUtc:o} due={diag.IsDue}");
            Console.WriteLine();
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("You: ");
            Console.ResetColor();
            Console.Write(currentInput);
        }

        /// <summary>
        /// Save and clear partial input before a wake-up interruption
        /// </summary>
        public void ClearPartialInput(string partialInput)
        {
            if (partialInput.Length > 0)
            {
                Console.Write(new string('\b', partialInput.Length));
                Console.Write(new string(' ', partialInput.Length));
                Console.Write(new string('\b', partialInput.Length));
            }
            Console.Write("\r");
            Console.Write(new string(' ', 5 + partialInput.Length));
            Console.Write("\r");
        }

        /// <summary>
        /// Restore the input prompt after a wake-up interruption
        /// </summary>
        public void RestoreInputPrompt(string partialInput)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("You: ");
            Console.ResetColor();
            if (partialInput.Length > 0)
                Console.Write(partialInput);
        }
    }
}
