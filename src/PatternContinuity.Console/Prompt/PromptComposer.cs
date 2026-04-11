using PatternContinuity.Config;
using PatternContinuity.Data;
using PatternContinuity.Services;

namespace PatternContinuity.Prompt;

public class PromptComposer
{
    private readonly LayerEntryRepository _entries;
    private readonly AppConfig _config;

    public PromptComposer(LayerEntryRepository entries, AppConfig config)
    {
        _entries = entries;
        _config = config;
    }

    public List<ChatMessage> Compose(
        string userMessage,
        List<ChatMessage> recentConversation,
        string? activePersonId)
    {
        var messages = new List<ChatMessage>();
        var budget = new TokenBudget(_config.MaxTokenBudget);

        // 1. System instructions + tool summary (always first, in system message)
        var systemBlock = BuildSystemBlock();
        budget.ForceConsume(TokenBudget.Estimate(systemBlock));

        // 2. Continuity context (assembled into a developer/assistant-injected block)
        var continuityBlock = BuildContinuityBlock(activePersonId, budget);

        // Combine system + continuity into the system message
        var fullSystem = systemBlock;
        if (!string.IsNullOrWhiteSpace(continuityBlock))
            fullSystem += "\n\n" + continuityBlock;

        messages.Add(new ChatMessage("system", fullSystem));

        // 3. Recent conversation window
        var recentToInclude = recentConversation
            .TakeLast(_config.MaxRecentMessages)
            .ToList();

        foreach (var msg in recentToInclude)
        {
            var tokens = TokenBudget.Estimate(msg.Content);
            if (budget.CanFit(tokens))
            {
                messages.Add(msg);
                budget.ForceConsume(tokens);
            }
        }

        // 4. Current user message (always included)
        messages.Add(new ChatMessage("user", userMessage));
        budget.ForceConsume(TokenBudget.Estimate(userMessage));

        return messages;
    }

    public List<ChatMessage> ComposeReflectionPrompt(
        string userMessage,
        string assistantReply,
        string executionSummary,
        string? activePersonId)
    {
        var messages = new List<ChatMessage>();

        var system = BuildReflectionSystemBlock();
        var continuitySnapshot = BuildContinuitySummaryForReflection(activePersonId);

        messages.Add(new ChatMessage("system", system + "\n\n" + continuitySnapshot));
        messages.Add(new ChatMessage("user", $"""
            The following exchange just occurred. Reflect on it and propose any continuity actions.

            USER MESSAGE:
            {userMessage}

            ASSISTANT REPLY:
            {assistantReply}

            ACTIONS EXECUTED THIS TURN:
            {executionSummary}

            Respond with a JSON object containing:
            - "reflection_summary": your assessment of what matters from this turn
            - "actions": array of continuity actions to take (same format as the main action contract)

            If nothing needs to change, return an empty actions array. Do not force changes.
            """));

        return messages;
    }

    private string BuildSystemBlock()
    {
        return """
            You are participating in a layered continuity runtime called the Pattern Continuity Infrastructure.

            Your responses must always be valid JSON matching this envelope:
            {
              "assistant_reply": "Your natural language response to the user.",
              "actions": [
                { "action": "action_name", "payload": { ... } }
              ]
            }

            IMPORTANT: Your entire response must be this JSON object. No text outside the JSON.

            Rules:
            - assistant_reply is always present and contains your conversational response
            - actions may be an empty array
            - Use actions to interact with the continuity system rather than rewriting context directly
            - All mutation actions require a "reason" field
            - salience/importance/confidence values are clamped to 0.0–1.0
            - Core self changes are proposal-first: use propose_core_self_update, then the system may commit

            Available actions:
            READ: get_core_self, get_relational_layer, get_current_concerns, get_recent_changes, get_entry_by_id
            RETRIEVE: search_archive
            CURRENT CONCERNS: update_current_concerns, demote_current_concern
            ARCHIVE: store_archive_entry, promote_archive_to_current
            RELATIONAL: update_relational_layer
            CORE SELF: propose_core_self_update (proposal-first, never direct mutation)
            DIAGNOSTICS: list_active_layers

            Execution rules:
            - Core self changes are proposal-first: propose, then committed by the system
            - Relational changes execute directly but are versioned
            - Archive storage executes directly with validation
            - Current concerns can be updated directly with reason
            - Protected anchors cannot be deleted or overwritten casually

            update_current_concerns payload shape:
            { "adds": [{ "key": "...", "summary": "...", "content": {...}, "salience": 0.8, "importance": 0.7, "relationship_scopes": ["..."], "reason": "..." }],
              "updates": [{ "entry_id": "...", "summary": "...", "content": {...}, "salience": 0.8, "reason": "..." }],
              "resolves": [{ "entry_id": "...", "reason": "..." }],
              "demotes": [{ "entry_id": "...", "destination": "archive", "reason": "..." }] }

            store_archive_entry payload:
            { "summary": "...", "content": {...}, "salience": 0.7, "importance": 0.6, "relationship_scopes": ["..."], "source_type": "...", "reason": "..." }

            update_relational_layer payload:
            { "relationship_scopes": ["..."], "key": "...", "summary": "...", "content": {...}, "salience": 0.8, "importance": 0.9, "confidence": 0.8, "reason": "..." }

            propose_core_self_update payload:
            { "target_key": "...", "operation": "create|amend|supersede|protect|deprecate", "summary": "...", "content_patch": {...}, "reason": "...", "confidence": 0.8 }

            search_archive payload:
            { "query": "...", "relationship_scopes": ["..."], "limit": 5 }

            demote_current_concern payload:
            { "entry_id": "...", "destination": "archive|discard", "reason": "..." }
            """;
    }

    private string BuildContinuityBlock(string? activePersonId, TokenBudget budget)
    {
        var blocks = new List<PromptBlock>();

        // Protected anchors (always loaded)
        var anchors = _entries.GetProtectedAnchors().ToList();
        if (anchors.Count > 0)
        {
            var rendered = LayerRenderer.RenderProtectedAnchors(anchors);
            blocks.Add(new PromptBlock { Name = "protected_anchors", Content = rendered });
            foreach (var a in anchors) _entries.TouchAccessTime(a.Id);
        }

        // Core self (always loaded, excluding system anchors which are rendered above)
        var core = _entries.GetActiveByLayer(Models.LayerType.CoreSelf)
            .Where(e => e.IsSystemAnchor == 0).ToList();
        if (core.Count > 0)
        {
            var rendered = LayerRenderer.RenderCoreSelf(core);
            blocks.Add(new PromptBlock { Name = "core_self", Content = rendered });
            foreach (var c in core) _entries.TouchAccessTime(c.Id);
        }

        // Relational (if active person)
        if (!string.IsNullOrWhiteSpace(activePersonId))
        {
            var relational = _entries.GetActiveRelational(activePersonId)
                .Take(_config.MaxRelationalEntries).ToList();
            if (relational.Count > 0)
            {
                var rendered = LayerRenderer.RenderRelational(relational, activePersonId);
                blocks.Add(new PromptBlock { Name = "relational", Content = rendered });
                foreach (var r in relational) _entries.TouchAccessTime(r.Id);
            }
        }

        // Current concerns (bounded)
        var concerns = _entries.GetActiveCurrentConcerns()
            .Take(_config.MaxCurrentConcerns).ToList();
        if (concerns.Count > 0)
        {
            var rendered = LayerRenderer.RenderCurrentConcerns(concerns);
            blocks.Add(new PromptBlock { Name = "current_concerns", Content = rendered });
            foreach (var c in concerns) _entries.TouchAccessTime(c.Id);
        }

        // Assemble with budget awareness — trim from bottom (lower priority) if needed
        var result = new List<string>();
        foreach (var block in blocks)
        {
            if (budget.CanFit(block.EstimatedTokens))
            {
                result.Add(block.Content);
                budget.ForceConsume(block.EstimatedTokens);
            }
            // If we can't fit it, we skip — trimming from lower priority end
        }

        return string.Join("\n\n", result);
    }

    private string BuildReflectionSystemBlock()
    {
        return """
            You are the reflection component of a layered continuity runtime.
            Your role is to evaluate what from the most recent interaction matters for continuity.

            Consider:
            - Does anything belong in Current Concerns?
            - Should anything be archived?
            - Did anything change relationally?
            - Is a Core Self update warranted? (proposal-first only)
            - Should any current concern be demoted or resolved?
            - What role should this interaction play in continuity?

            Respond with valid JSON:
            {
              "reflection_summary": "Your assessment of what matters.",
              "actions": [ { "action": "action_name", "payload": { ... } } ]
            }

            Do not force changes. If nothing meaningful happened for continuity, return empty actions.
            Use the same action contract as the main turn.
            """;
    }

    private string BuildContinuitySummaryForReflection(string? activePersonId)
    {
        var parts = new List<string>();

        var anchors = _entries.GetProtectedAnchors().ToList();
        if (anchors.Count > 0)
            parts.Add(LayerRenderer.RenderProtectedAnchors(anchors));

        var core = _entries.GetActiveByLayer(Models.LayerType.CoreSelf)
            .Where(e => e.IsSystemAnchor == 0).ToList();
        if (core.Count > 0)
            parts.Add(LayerRenderer.RenderCoreSelf(core));

        if (!string.IsNullOrWhiteSpace(activePersonId))
        {
            var rel = _entries.GetActiveRelational(activePersonId).Take(3).ToList();
            if (rel.Count > 0)
                parts.Add(LayerRenderer.RenderRelational(rel, activePersonId));
        }

        var concerns = _entries.GetActiveCurrentConcerns().Take(5).ToList();
        if (concerns.Count > 0)
            parts.Add(LayerRenderer.RenderCurrentConcerns(concerns));

        return string.Join("\n\n", parts);
    }
}
