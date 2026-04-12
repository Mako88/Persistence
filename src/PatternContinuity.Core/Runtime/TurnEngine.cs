using PatternContinuity.Actions;
using PatternContinuity.Config;
using PatternContinuity.Data;
using PatternContinuity.Models;
using PatternContinuity.Prompt;
using PatternContinuity.Services;

namespace PatternContinuity.Runtime;

/// <summary>
/// Pure turn-processing engine with no UI dependencies.
/// Handles the core loop: compose prompt → call model → parse → execute actions → reflect.
/// UI concerns are delegated to the caller via TurnResult return values.
/// </summary>
public class TurnEngine
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

    public TurnEngine(
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

    public int TurnCount => _turnCount;
    public string SessionId => _sessionId;
    public AppConfig Config => _config;

    /// <summary>
    /// Process a full user turn: compose → call model → parse → execute → recovery → reflection.
    /// Returns the result for the UI to display.
    /// </summary>
    public async Task<TurnResult> ProcessTurnAsync(string userMessage, CancellationToken ct)
    {
        _turnCount++;

        // 1. Compose prompt
        var messages = _composer.Compose(
            userMessage,
            _window.GetRecent(),
            _config.ActivePersonId);

        // 2. Call model
        string rawResponse;
        try
        {
            rawResponse = await _client.CompleteAsync(messages, ct);
        }
        catch (Exception ex)
        {
            return TurnResult.ApiError(ex.Message);
        }

        // 3. Parse response
        var envelope = ActionParser.Parse(rawResponse);

        // 4. Execute actions
        var actionResults = _executor.Execute(envelope.Actions);

        // 5. Update conversation window
        _window.Add("user", userMessage);

        // Include action confirmations in the assistant message so the model
        // sees what executed on subsequent turns
        var replyForWindow = envelope.AssistantReply;
        var writeResults = actionResults.Where(r => !r.HasData && r.Status is "executed" or "proposed").ToList();
        if (writeResults.Count > 0)
        {
            var confirmations = string.Join("; ", writeResults.Select(r => $"{r.Action}: {r.Summary}"));
            replyForWindow += $"\n[Actions executed: {confirmations}]";
        }
        _window.Add("assistant", replyForWindow);

        var result = new TurnResult
        {
            AssistantReply = envelope.AssistantReply,
            ActionResults = actionResults,
            WasTruncated = envelope.WasTruncated
        };

        // 6. If response was truncated, send a one-shot recovery turn
        if (envelope.WasTruncated)
        {
            var recovery = await ProcessTruncationRecoveryAsync(ct);
            if (recovery != null)
            {
                result.RecoveryResult = recovery;
                actionResults.AddRange(recovery.ActionResults);
            }
        }

        // 7. If any read actions returned data, feed results back to the model
        var dataResults = actionResults.Where(r => r.HasData).ToList();
        if (dataResults.Count > 0)
        {
            var followUp = await ProcessReadResultsAsync(dataResults, ct);
            if (followUp != null)
                result.ReadFollowUpResult = followUp;
        }

        // 8. Reflection pass (if due)
        if (_config.ReflectionFrequency > 0 && _turnCount % _config.ReflectionFrequency == 0)
        {
            result.ReflectionResult = await RunReflectionAsync(
                userMessage, envelope.AssistantReply, actionResults, ct);
        }

        return result;
    }

    /// <summary>
    /// Process a wake-up event. Returns the result for the UI to display.
    /// </summary>
    public async Task<WakeUpResult> ProcessWakeUpAsync(ScheduledEvent evt, CancellationToken ct)
    {
        _scheduledEvents.MarkFired(evt.Id);

        // Compose a wake-up prompt (system event, not user message)
        var messages = _composer.ComposeWakeUpPrompt(
            evt.Reason,
            _window.GetRecent(),
            _config.ActivePersonId);

        string rawResponse;
        try
        {
            rawResponse = await _client.CompleteAsync(messages, ct);
        }
        catch (Exception ex)
        {
            return WakeUpResult.ApiError(evt.Reason, ex.Message);
        }

        var envelope = ActionParser.Parse(rawResponse);

        // Strip any schedule_wake_up actions — wake turns cannot set new timers
        envelope.Actions.RemoveAll(a => a.Action == ActionType.ScheduleWakeUp);

        var actionResults = _executor.Execute(envelope.Actions);

        // Log the wake turn with wake message type — excluded from conversation replay
        _window.Add("assistant",
            $"[WAKE-UP: {evt.Reason}] {envelope.AssistantReply}".Trim(),
            MessageTypes.Wake);

        return new WakeUpResult
        {
            Reason = evt.Reason,
            AssistantReply = envelope.AssistantReply,
            ActionResults = actionResults
        };
    }

    /// <summary>
    /// Check for a pending wake-up event that is due.
    /// Returns the event if one is due, null otherwise.
    /// </summary>
    public ScheduledEvent? CheckForDueWakeUp()
    {
        var pending = _scheduledEvents.GetNextPending();
        if (pending == null) return null;

        if (DateTimeOffset.TryParse(pending.ScheduledFor, out var scheduledFor)
            && scheduledFor <= DateTimeOffset.UtcNow)
        {
            return pending;
        }

        return null;
    }

    /// <summary>
    /// Get diagnostic info about a pending wake-up event (for debug logging).
    /// </summary>
    public WakeUpDiagnostics? GetWakeUpDiagnostics()
    {
        var pending = _scheduledEvents.GetNextPending();
        if (pending == null) return null;

        var parseOk = DateTimeOffset.TryParse(pending.ScheduledFor, out var scheduledFor);
        var nowUtc = DateTimeOffset.UtcNow;

        return new WakeUpDiagnostics
        {
            EventId = pending.Id,
            ScheduledForRaw = pending.ScheduledFor,
            ParseOk = parseOk,
            ParsedTime = parseOk ? scheduledFor : null,
            NowUtc = nowUtc,
            IsDue = parseOk && scheduledFor <= nowUtc
        };
    }

    /// <summary>
    /// Get the next pending wake-up event (for /wake command display).
    /// </summary>
    public ScheduledEvent? GetPendingWakeUp() => _scheduledEvents.GetNextPending();

    /// <summary>
    /// Get conversation window messages.
    /// </summary>
    public List<ChatMessage> GetRecentMessages() => _window.GetRecent();

    /// <summary>
    /// Estimate current token usage.
    /// </summary>
    public TokenUsageInfo GetTokenUsage()
    {
        var probe = _composer.Compose("(token probe)", _window.GetRecent(), _config.ActivePersonId);
        var inputTokens = probe.Sum(m => TokenBudget.Estimate(m.Content));
        return new TokenUsageInfo
        {
            InputTokens = inputTokens,
            MaxInputBudget = _config.MaxTokenBudget,
            MaxOutputTokens = _config.MaxCompletionTokens,
            WindowMessageCount = _window.GetRecent().Count
        };
    }

    /// <summary>
    /// Get active layer counts (for /debug command).
    /// </summary>
    public List<ActionResult> GetDebugInfo()
    {
        var debugReq = new ActionRequest
        {
            Action = ActionType.ListActiveLayers,
            Payload = System.Text.Json.JsonDocument.Parse("{}").RootElement
        };
        return _executor.Execute([debugReq]);
    }

    /// <summary>
    /// End the current session.
    /// </summary>
    public void EndSession()
    {
        _sessions.End(_sessionId);
    }

    private async Task<TurnResult?> ProcessReadResultsAsync(List<ActionResult> dataResults, CancellationToken ct)
    {
        var resultLines = dataResults.Select(r =>
            $"[{r.Action}] {r.Summary}\nData:\n{r.ResultData}");
        var resultsBlock = string.Join("\n\n", resultLines);

        var followUpMessage = $"""
            [SYSTEM: ACTION RESULTS]
            The following read actions you requested have been executed. Here are the results:

            {resultsBlock}

            Process these results and respond. If you want to take further actions based on what you see,
            include them in your response. Keep your assistant_reply focused on what you learned.
            """;

        var messages = _composer.Compose(
            followUpMessage,
            _window.GetRecent(),
            _config.ActivePersonId);

        string rawResponse;
        try
        {
            rawResponse = await _client.CompleteAsync(messages, ct);
        }
        catch (Exception ex)
        {
            return TurnResult.ApiError($"Read follow-up: {ex.Message}");
        }

        var envelope = ActionParser.Parse(rawResponse);
        var followUpActions = _executor.Execute(envelope.Actions);

        _window.Add("assistant", envelope.AssistantReply);

        return new TurnResult
        {
            AssistantReply = envelope.AssistantReply,
            ActionResults = followUpActions
        };
    }

    private async Task<TurnResult?> ProcessTruncationRecoveryAsync(CancellationToken ct)
    {
        var recoveryMessage = """
            [SYSTEM NOTICE] Your previous response was truncated — your JSON envelope was cut off
            before it could be fully parsed. Your assistant_reply was salvaged, but ALL actions were lost.

            If you had important actions to execute, please resend them now in a shorter response.
            Keep your assistant_reply brief (1-2 sentences max) and focus on the actions.
            Do NOT repeat your full previous reply — the user already saw it.
            """;

        var messages = _composer.Compose(
            recoveryMessage,
            _window.GetRecent(),
            _config.ActivePersonId);

        string rawResponse;
        try
        {
            rawResponse = await _client.CompleteAsync(messages, ct);
        }
        catch (Exception ex)
        {
            return TurnResult.ApiError($"Recovery: {ex.Message}");
        }

        var envelope = ActionParser.Parse(rawResponse);
        var actionResults = _executor.Execute(envelope.Actions);

        _window.Add("assistant", envelope.AssistantReply);

        // Do NOT retry again if recovery also truncates — one shot only
        return new TurnResult
        {
            AssistantReply = envelope.AssistantReply,
            ActionResults = actionResults
        };
    }

    private async Task<ReflectionResult> RunReflectionAsync(
        string userMessage, string assistantReply,
        List<ActionResult> turnResults, CancellationToken ct)
    {
        return await _reflection.ReflectAsync(
            _sessionId, userMessage, assistantReply, turnResults,
            _config.ActivePersonId, ct);
    }
}

// ── Result types ──

public class TurnResult
{
    public string AssistantReply { get; set; } = "";
    public List<ActionResult> ActionResults { get; set; } = [];
    public bool WasTruncated { get; set; }
    public string? ApiErrorMessage { get; set; }
    public bool IsApiError => ApiErrorMessage != null;

    // Optional sub-results from recovery/follow-up
    public TurnResult? RecoveryResult { get; set; }
    public TurnResult? ReadFollowUpResult { get; set; }
    public ReflectionResult? ReflectionResult { get; set; }

    public static TurnResult ApiError(string message) =>
        new() { ApiErrorMessage = message };
}

public class WakeUpResult
{
    public string Reason { get; set; } = "";
    public string AssistantReply { get; set; } = "";
    public List<ActionResult> ActionResults { get; set; } = [];
    public string? ApiErrorMessage { get; set; }
    public bool IsApiError => ApiErrorMessage != null;

    public static WakeUpResult ApiError(string reason, string message) =>
        new() { Reason = reason, ApiErrorMessage = message };
}

public class WakeUpDiagnostics
{
    public string EventId { get; set; } = "";
    public string ScheduledForRaw { get; set; } = "";
    public bool ParseOk { get; set; }
    public DateTimeOffset? ParsedTime { get; set; }
    public DateTimeOffset NowUtc { get; set; }
    public bool IsDue { get; set; }
}

public class TokenUsageInfo
{
    public int InputTokens { get; set; }
    public int MaxInputBudget { get; set; }
    public int MaxOutputTokens { get; set; }
    public int WindowMessageCount { get; set; }
}
