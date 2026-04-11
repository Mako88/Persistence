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
    private readonly AppConfig _config;
    private readonly ConversationWindow _window;
    private readonly string _sessionId;
    private int _turnCount;

    public Orchestrator(
        IModelClient client,
        PromptComposer composer,
        ActionExecutor executor,
        ReflectionService reflection,
        SessionRepository sessions,
        AppConfig config,
        string sessionId,
        ConversationWindow window)
    {
        _client = client;
        _composer = composer;
        _executor = executor;
        _reflection = reflection;
        _sessions = sessions;
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
        Console.WriteLine("======================================================");
        Console.WriteLine();

        while (!ct.IsCancellationRequested)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("You: ");
            Console.ResetColor();

            var input = Console.ReadLine();
            if (input == null || input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)
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
