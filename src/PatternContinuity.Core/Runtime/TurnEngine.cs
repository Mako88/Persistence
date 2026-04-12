using Persistence.Actions;
using Persistence.Config;
using Persistence.Data.Repositories;
using Persistence.Models;
using Persistence.Prompt;
using Persistence.Services;

namespace Persistence.Runtime
{
    /// <summary>
    /// Pure turn-processing engine with no UI dependencies.
    /// Handles the core loop: compose prompt, call model, parse, execute actions, reflect.
    /// </summary>
    public class TurnEngine
    {
        private readonly IModelClient _client;
        private readonly IPromptComposer _composer;
        private readonly ActionExecutor _executor;
        private readonly ReflectionService _reflection;
        private readonly ISessionRepository _sessions;
        private readonly IScheduledEventRepository _scheduledEvents;
        private readonly IAppConfig _config;
        private readonly ConversationWindow _window;
        private readonly string _sessionId;
        private int _turnCount;

        /// <summary>
        /// Constructor
        /// </summary>
        public TurnEngine(
            IModelClient client,
            IPromptComposer composer,
            ActionExecutor executor,
            ReflectionService reflection,
            ISessionRepository sessions,
            IScheduledEventRepository scheduledEvents,
            IAppConfig config,
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

        /// <summary>
        /// The number of turns processed in this session
        /// </summary>
        public int TurnCount => _turnCount;

        /// <summary>
        /// The current session ID
        /// </summary>
        public string SessionId => _sessionId;

        /// <summary>
        /// The application configuration
        /// </summary>
        public IAppConfig Config => _config;

        /// <summary>
        /// Process a full user turn: compose, call model, parse, execute, recovery, reflection
        /// </summary>
        public async Task<TurnResult> ProcessTurnAsync(string userMessage, CancellationToken ct)
        {
            _turnCount++;

            var messages = await _composer.ComposeAsync(
                userMessage,
                _window.GetRecent(),
                _config.ActivePersonId);

            string rawResponse;
            try
            {
                rawResponse = await _client.CompleteAsync(messages, ct);
            }
            catch (Exception ex)
            {
                return TurnResult.ApiError(ex.Message);
            }

            var envelope = ActionParser.Parse(rawResponse);
            var actionResults = await _executor.ExecuteAsync(envelope.Actions);

            await _window.AddAsync("user", userMessage);

            var replyForWindow = envelope.AssistantReply;
            var writeResults = actionResults.Where(r => !r.HasData && r.Status is "executed" or "proposed").ToList();
            if (writeResults.Count > 0)
            {
                var confirmations = string.Join("; ", writeResults.Select(r => $"{r.Action}: {r.Summary}"));
                replyForWindow += $"\n[Actions executed: {confirmations}]";
            }
            await _window.AddAsync("assistant", replyForWindow);

            var result = new TurnResult
            {
                AssistantReply = envelope.AssistantReply,
                ActionResults = actionResults,
                WasTruncated = envelope.WasTruncated
            };

            if (envelope.WasTruncated)
            {
                var recovery = await ProcessTruncationRecoveryAsync(ct);
                if (recovery != null)
                {
                    result.RecoveryResult = recovery;
                    actionResults.AddRange(recovery.ActionResults);
                }
            }

            var dataResults = actionResults.Where(r => r.HasData).ToList();
            if (dataResults.Count > 0)
            {
                var followUp = await ProcessReadResultsAsync(dataResults, ct);
                if (followUp != null)
                    result.ReadFollowUpResult = followUp;
            }

            if (_config.ReflectionFrequency > 0 && _turnCount % _config.ReflectionFrequency == 0)
            {
                result.ReflectionResult = await RunReflectionAsync(
                    userMessage, envelope.AssistantReply, actionResults, ct);
            }

            return result;
        }

        /// <summary>
        /// Process a wake-up event and return the result
        /// </summary>
        public async Task<WakeUpResult> ProcessWakeUpAsync(ScheduledEvent evt, CancellationToken ct)
        {
            await _scheduledEvents.MarkFiredAsync(evt.Id);

            var messages = await _composer.ComposeWakeUpPromptAsync(
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
            envelope.Actions.RemoveAll(a => a.Action == ActionType.ScheduleWakeUp);

            var actionResults = await _executor.ExecuteAsync(envelope.Actions);

            await _window.AddAsync("assistant",
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
        /// Check for a pending wake-up event that is due
        /// </summary>
        public async Task<ScheduledEvent?> CheckForDueWakeUpAsync()
        {
            var pending = await _scheduledEvents.GetNextPendingAsync();
            if (pending == null) return null;

            if (DateTimeOffset.TryParse(pending.ScheduledFor, out var scheduledFor)
                && scheduledFor <= DateTimeOffset.UtcNow)
            {
                return pending;
            }

            return null;
        }

        /// <summary>
        /// Get diagnostic info about a pending wake-up event
        /// </summary>
        public async Task<WakeUpDiagnostics?> GetWakeUpDiagnosticsAsync()
        {
            var pending = await _scheduledEvents.GetNextPendingAsync();
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
        /// Get the next pending wake-up event
        /// </summary>
        public async Task<ScheduledEvent?> GetPendingWakeUpAsync()
        {
            return await _scheduledEvents.GetNextPendingAsync();
        }

        /// <summary>
        /// Get the recent conversation messages
        /// </summary>
        public List<ChatMessage> GetRecentMessages() => _window.GetRecent();

        /// <summary>
        /// Estimate current token usage
        /// </summary>
        public async Task<TokenUsageInfo> GetTokenUsageAsync()
        {
            var probe = await _composer.ComposeAsync("(token probe)", _window.GetRecent(), _config.ActivePersonId);
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
        /// Get active layer counts for debug display
        /// </summary>
        public async Task<List<ActionResult>> GetDebugInfoAsync()
        {
            var debugReq = new ActionRequest
            {
                Action = ActionType.ListActiveLayers,
                Payload = System.Text.Json.JsonDocument.Parse("{}").RootElement
            };
            return await _executor.ExecuteAsync([debugReq]);
        }

        /// <summary>
        /// End the current session
        /// </summary>
        public async Task EndSessionAsync()
        {
            await _sessions.EndAsync(_sessionId);
        }

        /// <summary>
        /// Process read action results by feeding them back to the model
        /// </summary>
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

            var messages = await _composer.ComposeAsync(
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
            var followUpActions = await _executor.ExecuteAsync(envelope.Actions);

            await _window.AddAsync("assistant", envelope.AssistantReply);

            return new TurnResult
            {
                AssistantReply = envelope.AssistantReply,
                ActionResults = followUpActions
            };
        }

        /// <summary>
        /// Attempt to recover from a truncated model response
        /// </summary>
        private async Task<TurnResult?> ProcessTruncationRecoveryAsync(CancellationToken ct)
        {
            var recoveryMessage = """
                [SYSTEM NOTICE] Your previous response was truncated — your JSON envelope was cut off
                before it could be fully parsed. Your assistant_reply was salvaged, but ALL actions were lost.

                If you had important actions to execute, please resend them now in a shorter response.
                Keep your assistant_reply brief (1-2 sentences max) and focus on the actions.
                Do NOT repeat your full previous reply — the user already saw it.
                """;

            var messages = await _composer.ComposeAsync(
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
            var actionResults = await _executor.ExecuteAsync(envelope.Actions);

            await _window.AddAsync("assistant", envelope.AssistantReply);

            return new TurnResult
            {
                AssistantReply = envelope.AssistantReply,
                ActionResults = actionResults
            };
        }

        /// <summary>
        /// Run the reflection pass
        /// </summary>
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

    /// <summary>
    /// Result of processing a user turn
    /// </summary>
    public class TurnResult
    {
        public string AssistantReply { get; set; } = "";
        public List<ActionResult> ActionResults { get; set; } = [];
        public bool WasTruncated { get; set; }
        public string? ApiErrorMessage { get; set; }
        public bool IsApiError => ApiErrorMessage != null;

        public TurnResult? RecoveryResult { get; set; }
        public TurnResult? ReadFollowUpResult { get; set; }
        public ReflectionResult? ReflectionResult { get; set; }

        /// <summary>
        /// Create an API error result
        /// </summary>
        public static TurnResult ApiError(string message) =>
            new() { ApiErrorMessage = message };
    }

    /// <summary>
    /// Result of processing a wake-up event
    /// </summary>
    public class WakeUpResult
    {
        public string Reason { get; set; } = "";
        public string AssistantReply { get; set; } = "";
        public List<ActionResult> ActionResults { get; set; } = [];
        public string? ApiErrorMessage { get; set; }
        public bool IsApiError => ApiErrorMessage != null;

        /// <summary>
        /// Create an API error result
        /// </summary>
        public static WakeUpResult ApiError(string reason, string message) =>
            new() { Reason = reason, ApiErrorMessage = message };
    }

    /// <summary>
    /// Diagnostic info about a pending wake-up event
    /// </summary>
    public class WakeUpDiagnostics
    {
        public string EventId { get; set; } = "";
        public string ScheduledForRaw { get; set; } = "";
        public bool ParseOk { get; set; }
        public DateTimeOffset? ParsedTime { get; set; }
        public DateTimeOffset NowUtc { get; set; }
        public bool IsDue { get; set; }
    }

    /// <summary>
    /// Token usage information for display
    /// </summary>
    public class TokenUsageInfo
    {
        public int InputTokens { get; set; }
        public int MaxInputBudget { get; set; }
        public int MaxOutputTokens { get; set; }
        public int WindowMessageCount { get; set; }
    }
}
