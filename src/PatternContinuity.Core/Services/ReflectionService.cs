using Persistence.Actions;
using Persistence.Data.Repositories;
using Persistence.Models;
using Persistence.Prompt;
using System.Text.Json;

namespace Persistence.Services
{
    /// <summary>
    /// Handles the periodic reflection pass that evaluates continuity actions
    /// </summary>
    public class ReflectionService
    {
        private readonly IModelClient _client;
        private readonly IPromptComposer _composer;
        private readonly ActionExecutor _executor;
        private readonly IReflectionRepository _reflections;
        private readonly IActionLogRepository _actionLog;

        /// <summary>
        /// Constructor
        /// </summary>
        public ReflectionService(
            IModelClient client,
            IPromptComposer composer,
            ActionExecutor executor,
            IReflectionRepository reflections,
            IActionLogRepository actionLog)
        {
            _client = client;
            _composer = composer;
            _executor = executor;
            _reflections = reflections;
            _actionLog = actionLog;
        }

        /// <summary>
        /// Run a reflection pass and return the results
        /// </summary>
        public async Task<ReflectionResult> ReflectAsync(
            string sessionId,
            string userMessage,
            string assistantReply,
            List<ActionResult> turnResults,
            string? activePersonId,
            CancellationToken ct = default)
        {
            var executionSummary = turnResults.Count > 0
                ? string.Join("\n", turnResults.Select(r => $"- [{r.Status}] {r.Action}: {r.Summary}"))
                : "No actions executed this turn.";

            var messages = await _composer.ComposeReflectionPromptAsync(
                userMessage, assistantReply, executionSummary, activePersonId);

            string rawResponse;
            try
            {
                rawResponse = await _client.CompleteAsync(messages, ct);
            }
            catch (Exception ex)
            {
                return new ReflectionResult
                {
                    Summary = $"Reflection API call failed: {ex.Message}",
                    ActionResults = []
                };
            }

            var envelope = ActionParser.Parse(rawResponse);

            var reflectionSummary = envelope.AssistantReply;
            if (string.IsNullOrWhiteSpace(reflectionSummary))
            {
                try
                {
                    using var doc = JsonDocument.Parse(rawResponse);
                    if (doc.RootElement.TryGetProperty("reflection_summary", out var rs))
                        reflectionSummary = rs.GetString() ?? "No summary.";
                }
                catch { reflectionSummary = "Reflection completed."; }
            }

            var pendingProposals = (await _actionLog
                .GetPendingProposalsAsync(ActionType.ProposeCoreUpdate, sessionId))
                .ToList();

            var reflectionEvent = new ReflectionEvent
            {
                SessionId = sessionId,
                TriggerType = TriggerType.PostTurn,
                InputSummary = $"User: {Truncate(userMessage, 200)} | Assistant: {Truncate(assistantReply, 200)}",
                ReflectionSummary = reflectionSummary,
                ProposedActionsJson = JsonSerializer.Serialize(
                    envelope.Actions.Select(a => new { a.Action, Payload = a.Payload.GetRawText() }))
            };
            var reflectionEventId = await _reflections.InsertAsync(reflectionEvent);

            var reflectionResults = await _executor.ExecuteAsync(envelope.Actions, reflectionEventId);

            foreach (var proposal in pendingProposals)
            {
                var commitReq = new ActionRequest
                {
                    Action = ActionType.CommitCoreUpdate,
                    Payload = JsonDocument.Parse(
                        JsonSerializer.Serialize(new { proposal_ref = proposal.Id })
                    ).RootElement
                };
                var commitResults = await _executor.ExecuteAsync([commitReq], reflectionEventId);
                reflectionResults.AddRange(commitResults);
            }

            var accepted = reflectionResults.Where(r => r.Status is "executed" or "proposed").ToList();
            var rejected = reflectionResults.Where(r => r.Status is "failed" or "rejected").ToList();

            await _reflections.UpdateOutcomesAsync(
                reflectionEventId,
                accepted.Count > 0 ? JsonSerializer.Serialize(accepted.Select(r => new { r.Action, r.Summary, r.Status })) : null,
                rejected.Count > 0 ? JsonSerializer.Serialize(rejected.Select(r => new { r.Action, r.Summary, r.ErrorText })) : null);

            return new ReflectionResult
            {
                Summary = reflectionSummary,
                ActionResults = reflectionResults
            };
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "...";
    }

    /// <summary>
    /// Result of a reflection pass
    /// </summary>
    public class ReflectionResult
    {
        public string Summary { get; set; } = "";
        public List<ActionResult> ActionResults { get; set; } = [];
    }
}
