using PatternContinuity.Actions;
using PatternContinuity.Config;
using PatternContinuity.Data;
using PatternContinuity.Models;
using PatternContinuity.Prompt;
using PatternContinuity.Services;

namespace PatternContinuity.Runtime;

public class Orchestrator
{
    private readonly IModelClient _client;
    private readonly PromptComposer _composer;
    private readonly ActionExecutor _executor;
    private readonly ReflectionService _reflection;
    private readonly SessionRepository _sessions;
    private readonly ScheduledEventRepository _scheduledEvents;
    private readonly AppConfig _config;
    private readonly ConversationWindow _window;
    private readonly string _sessionId;
    private int _turnCount;
    private bool _wakeUpFiredThisCycle;

    public Orchestrator(
        IModelClient client,
        PromptComposer composer,
        ActionExecutor executor,
        ReflectionService reflection,
        SessionRepository sessions,
        ScheduledEventRepository scheduledEvents,
        AppConfig config,
        string sessionId,
        ConversationWindow window)
    {
        _client = client;
        _composer = composer;
        _executor = executor;
        _reflection = reflection;
        _sessions = sessions;
        _scheduledEvents = scheduledEvents;
        _config = config;
        _sessionId = sessionId;
        _window = window;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine("=== Pattern Continuity Infrastructure — MVP Console ===");
        Console.WriteLine($"Session: {_sessionId}");
        Console.WriteLine($"Active person: {_config.ActivePersonId ?? "(none)"}");
        Console.WriteLine($"Model: {_config.ApiProvider}/{_config.ModelName}");
        Console.WriteLine($"Reflection: every {_config.ReflectionFrequency} turn(s)");
        Console.WriteLine("Type 'exit' or 'quit' to end the session.");
        Console.WriteLine("Type '/debug' to show current layer counts.");
        Console.WriteLine("Type '/wake' to show pending wake-up timer.");
        Console.WriteLine("======================================================");
        Console.WriteLine();

        // Display restored conversation history
        var restored = _window.GetRecent();
        if (restored.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("--- Recent conversation ---");
            Console.ResetColor();
            foreach (var msg in restored)
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

        while (!ct.IsCancellationRequested)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("You: ");
            Console.ResetColor();

            // Poll for input while checking wake-up timers
            var input = await ReadLineWithWakeUpPolling(ct);

            if (input == null)
            {
                // null means cancellation or EOF
                await EndSessionAsync();
                break;
            }

            // Reset wake gate on real user input
            _wakeUpFiredThisCycle = false;

            if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)
                || input.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                await EndSessionAsync();
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

    /// <summary>
    /// Reads a line from stdin while polling for wake-up timers every second.
    /// If a wake-up fires before the user types anything, processes the wake turn
    /// and returns a sentinel value.
    /// </summary>
    private async Task<string?> ReadLineWithWakeUpPolling(CancellationToken ct)
    {
        // Start async line read
        var readTask = Task.Run(() => Console.ReadLine(), ct);

        while (!ct.IsCancellationRequested)
        {
            // Check if user has typed something
            var completed = await Task.WhenAny(readTask, Task.Delay(1000, ct))
                .ConfigureAwait(false);

            if (completed == readTask)
                return await readTask;

            // Check for due wake-up events
            if (!_wakeUpFiredThisCycle)
            {
                var pending = _scheduledEvents.GetNextPending();
                if (pending != null && DateTime.TryParse(pending.ScheduledFor, out var scheduledFor)
                    && scheduledFor <= DateTime.UtcNow)
                {
                    await ProcessWakeUpAsync(pending, ct);
                    // Don't return sentinel — re-prompt. The wake turn output is already shown.
                    // But block further wakes until user speaks.
                    _wakeUpFiredThisCycle = true;

                    // Re-print the prompt since the wake output may have pushed it off screen
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write("You: ");
                    Console.ResetColor();
                }
            }
        }

        return null;
    }

    private async Task ProcessTurnAsync(string userMessage, CancellationToken ct)
    {
        _turnCount++;

        // 1. Compose prompt
        var messages = _composer.Compose(
            userMessage,
            _window.GetRecent(),
            _config.ActivePersonId);

        // 2. Call model
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  [thinking...]");
        Console.ResetColor();

        string rawResponse;
        try
        {
            rawResponse = await _client.CompleteAsync(messages, ct);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [API Error: {ex.Message}]");
            Console.ResetColor();
            return;
        }

        // 3. Parse response
        var envelope = ActionParser.Parse(rawResponse);

        // 4. Execute actions
        var actionResults = _executor.Execute(envelope.Actions);

        // 5. Display reply
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("Assistant: ");
        Console.ResetColor();
        Console.WriteLine(envelope.AssistantReply);
        Console.WriteLine();

        // 6. Show action results if any
        if (actionResults.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  [{actionResults.Count} action(s) processed]");
            foreach (var r in actionResults)
                Console.WriteLine($"    {r.Status}: {r.Action} — {r.Summary}");
            Console.ResetColor();
            Console.WriteLine();
        }

        // 7. Update conversation window
        _window.Add("user", userMessage);
        _window.Add("assistant", envelope.AssistantReply);

        // 8. Reflection pass (if due)
        if (_config.ReflectionFrequency > 0 && _turnCount % _config.ReflectionFrequency == 0)
        {
            await RunReflectionAsync(userMessage, envelope.AssistantReply, actionResults, ct);
        }
    }

    private async Task RunReflectionAsync(
        string userMessage, string assistantReply,
        List<ActionResult> turnResults, CancellationToken ct)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  [reflecting...]");
        Console.ResetColor();

        var result = await _reflection.ReflectAsync(
            _sessionId, userMessage, assistantReply, turnResults,
            _config.ActivePersonId, ct);

        if (result.ActionResults.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine($"  [reflection: {result.ActionResults.Count} action(s)]");
            foreach (var r in result.ActionResults)
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

    private async Task ProcessWakeUpAsync(ScheduledEvent evt, CancellationToken ct)
    {
        _scheduledEvents.MarkFired(evt.Id);

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine();
        Console.WriteLine($"  [WAKE-UP: {evt.Reason}]");
        Console.ResetColor();

        // Compose a wake-up prompt (system event, not user message)
        var messages = _composer.ComposeWakeUpPrompt(
            evt.Reason,
            _window.GetRecent(),
            _config.ActivePersonId);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  [thinking (wake)...]");
        Console.ResetColor();

        string rawResponse;
        try
        {
            rawResponse = await _client.CompleteAsync(messages, ct);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [Wake API Error: {ex.Message}]");
            Console.ResetColor();
            return;
        }

        var envelope = ActionParser.Parse(rawResponse);

        // Strip any schedule_wake_up actions — wake turns cannot set new timers
        envelope.Actions.RemoveAll(a => a.Action == ActionType.ScheduleWakeUp);

        var actionResults = _executor.Execute(envelope.Actions);

        // Display reply only if non-empty (silent wake turns are valid)
        if (!string.IsNullOrWhiteSpace(envelope.AssistantReply))
        {
            Console.ForegroundColor = ConsoleColor.DarkGreen;
            Console.Write("  [Wake] Assistant: ");
            Console.ResetColor();
            Console.WriteLine(envelope.AssistantReply);
            Console.WriteLine();
        }

        if (actionResults.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  [wake: {actionResults.Count} action(s)]");
            foreach (var r in actionResults)
                Console.WriteLine($"    {r.Status}: {r.Action} — {r.Summary}");
            Console.ResetColor();
            Console.WriteLine();
        }

        // Log the wake turn with wake message type — excluded from conversation replay
        _window.Add("assistant",
            $"[WAKE-UP: {evt.Reason}] {envelope.AssistantReply}".Trim(),
            MessageTypes.Wake);
    }

    private void HandleCommand(string command)
    {
        switch (command.ToLower())
        {
            case "/debug":
                var executor = _executor;
                var debugReq = new ActionRequest
                {
                    Action = ActionType.ListActiveLayers,
                    Payload = System.Text.Json.JsonDocument.Parse("{}").RootElement
                };
                var debugResult = _executor.Execute([debugReq]);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                foreach (var r in debugResult)
                    Console.WriteLine($"  {r.Summary}");
                Console.ResetColor();
                Console.WriteLine();
                break;

            case "/wake":
                var pendingWake = _scheduledEvents.GetNextPending();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                if (pendingWake != null)
                {
                    var due = DateTime.TryParse(pendingWake.ScheduledFor, out var dt)
                        ? dt.ToLocalTime().ToString("HH:mm:ss")
                        : pendingWake.ScheduledFor;
                    Console.WriteLine($"  Pending wake-up at {due}: {pendingWake.Reason}");
                }
                else
                {
                    Console.WriteLine("  No pending wake-up timer.");
                }
                Console.ResetColor();
                Console.WriteLine();
                break;

            default:
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  Unknown command: {command}");
                Console.ResetColor();
                Console.WriteLine();
                break;
        }
    }

    private async Task EndSessionAsync()
    {
        _sessions.End(_sessionId);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  [Session ended.]");
        Console.ResetColor();
    }
}
