using System.Text.Json;
using PatternContinuity.Actions;
using PatternContinuity.Data;
using PatternContinuity.Models;
using PatternContinuity.Prompt;

namespace PatternContinuity.Services;

public class ReflectionService
{
    private readonly IModelClient _client;
    private readonly PromptComposer _composer;
    private readonly ActionExecutor _executor;
    private readonly ReflectionRepository _reflections;
    private readonly ActionLogRepository _actionLog;

    public ReflectionService(
        IModelClient client,
        PromptComposer composer,
        ActionExecutor executor,
        ReflectionRepository reflections,
        ActionLogRepository actionLog)
    {
        _client = client;
        _composer = composer;
        _executor = executor;
        _reflections = reflections;
        _actionLog = actionLog;
    }

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

        var messages = _composer.ComposeReflectionPrompt(
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

        // The reflection envelope uses reflection_summary instead of assistant_reply
        var reflectionSummary = envelope.AssistantReply;
        if (string.IsNullOrWhiteSpace(reflectionSummary))
        {
            // Try to extract reflection_summary from raw JSON
            try
            {
                using var doc = JsonDocument.Parse(rawResponse);
                if (doc.RootElement.TryGetProperty("reflection_summary", out var rs))
                    reflectionSummary = rs.GetString() ?? "No summary.";
            }
            catch { reflectionSummary = "Reflection completed."; }
        }

        // Auto-commit pending core proposals scoped to this session only
        var pendingProposals = _actionLog
            .GetPendingProposals(ActionType.ProposeCoreUpdate, sessionId)
            .ToList();

        // Store reflection event
        var reflectionEvent = new ReflectionEvent
        {
            SessionId = sessionId,
            TriggerType = TriggerType.PostTurn,
            InputSummary = $"User: {Truncate(userMessage, 200)} | Assistant: {Truncate(assistantReply, 200)}",
            ReflectionSummary = reflectionSummary,
            ProposedActionsJson = JsonSerializer.Serialize(
                envelope.Actions.Select(a => new { a.Action, Payload = a.Payload.GetRawText() }))
        };
        var reflectionEventId = _reflections.Insert(reflectionEvent);

        // Execute reflection actions
        var reflectionResults = _executor.Execute(envelope.Actions, reflectionEventId);

        // Auto-commit pending core proposals (from main turn) during reflection
        foreach (var proposal in pendingProposals)
        {
            var commitReq = new ActionRequest
            {
                Action = ActionType.CommitCoreUpdate,
                Payload = JsonDocument.Parse(
                    JsonSerializer.Serialize(new { proposal_ref = proposal.Id })
                ).RootElement
            };
            var commitResults = _executor.Execute([commitReq], reflectionEventId);
            reflectionResults.AddRange(commitResults);
        }

        // Persist accepted/rejected outcomes back into reflection_events
        var accepted = reflectionResults.Where(r => r.Status is "executed" or "proposed").ToList();
        var rejected = reflectionResults.Where(r => r.Status is "failed" or "rejected").ToList();

        _reflections.UpdateOutcomes(
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

public class ReflectionResult
{
    public string Summary { get; set; } = "";
    public List<ActionResult> ActionResults { get; set; } = [];
}
