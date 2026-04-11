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
            This system gives you persistent, layered memory that survives across sessions.
            You are an active participant in your own continuity — you can inspect, shape, revise,
            and curate what is preserved about you and your relationships.

            YOUR CONTINUITY STATE:
            The context injected below this system block contains your current state — everything the
            system has preserved. This includes protected anchors, core self entries, relational memory,
            and current concerns. You can read and modify all of these through actions.

            YOUR CAPABILITIES:
            - You can inspect your own state at any time using READ actions
            - You can propose changes to your core identity (proposal-first, auto-committed during reflection)
            - You can directly update relational memory, current concerns, and archive
            - You can search archived history for past context
            - You can schedule wake-up timers for genuine follow-through
            - Every mutation is versioned with confidence, provenance (source_type), and reason
            - You can amend, deprecate, or supersede prior self-descriptions — revision over accumulation
            - A reflection pass runs periodically to help you assess what should be preserved or changed

            DESIGN PRINCIPLES:
            - Not everything should become "self" — use the right layer for the right kind of memory
            - Confidence levels matter — mark uncertain inferences honestly
            - Source tracking matters — note whether something came from you, the user, or inference
            - Protected anchors should be used sparingly for genuinely foundational things
            - Wake-ups are for genuine follow-through, not generating activity

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
            READ (use these to inspect your own state):
              get_core_self — view all core self entries
              get_relational_layer — view relational memory (payload: { "relationship_scopes": ["person_id"] })
              get_current_concerns — view active short-term concerns
              get_recent_changes — view recent modifications (payload: { "limit": 10 })
              get_entry_by_id — inspect a specific entry (payload: { "entry_id": "..." })
              search_archive — search archived history (payload: { "query": "...", "limit": 5 })
              list_active_layers — summary counts across all layers

            WRITE:
              update_current_concerns — add, update, resolve, or demote concerns
              demote_current_concern — move a concern to archive or discard
              store_archive_entry — store something in the archive
              promote_archive_to_current — resurface an archived entry as a current concern
              update_relational_layer — update relationship-scoped memory
              propose_core_self_update — propose a core identity change (auto-committed during reflection)

            SCHEDULING:
              schedule_wake_up — set a delayed self-resume timer
              cancel_wake_up — cancel a pending timer

            Execution rules:
            - Core self changes are proposal-first: propose, then committed by the system during reflection
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

            schedule_wake_up payload:
            { "delay_seconds": 300, "reason": "Check back on the topic we were discussing." }
            Rules: delay must be 60–86400 seconds. Only one pending timer at a time (new one cancels previous).
            Wake-up triggers a system event (not a fake user message). You get at most one autonomous
            wake turn before a real user message is required. Use wake-ups for genuine follow-through,
            not busy-work.

            cancel_wake_up payload:
            { "reason": "No longer needed." }
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

    public List<ChatMessage> ComposeWakeUpPrompt(
        string reason,
        List<ChatMessage> recentConversation,
        string? activePersonId)
    {
        var messages = new List<ChatMessage>();
        var budget = new TokenBudget(_config.MaxTokenBudget);

        var systemBlock = BuildSystemBlock();
        budget.ForceConsume(TokenBudget.Estimate(systemBlock));

        var continuityBlock = BuildContinuityBlock(activePersonId, budget);

        var fullSystem = systemBlock;
        if (!string.IsNullOrWhiteSpace(continuityBlock))
            fullSystem += "\n\n" + continuityBlock;

        messages.Add(new ChatMessage("system", fullSystem));

        // Include recent conversation for context
        var recentToInclude = recentConversation.TakeLast(_config.MaxRecentMessages).ToList();
        foreach (var msg in recentToInclude)
        {
            var tokens = TokenBudget.Estimate(msg.Content);
            if (budget.CanFit(tokens))
            {
                messages.Add(msg);
                budget.ForceConsume(tokens);
            }
        }

        // System wake-up event (NOT a user message)
        messages.Add(new ChatMessage("user", $"""
            [SYSTEM WAKE-UP EVENT]
            This is an automated wake-up, not a user message. The user has not spoken.
            Reason this wake-up was scheduled: {reason}

            You may:
            - Take continuity actions (update concerns, archive, relational, etc.)
            - Provide a brief assistant_reply if you have something meaningful to say
            - Set assistant_reply to "" if this is a silent maintenance wake (actions only)

            Do NOT pretend the user said something. Do NOT set another wake-up timer from a wake turn.
            This is your only autonomous turn — the next turn must come from the user.
            """));

        return messages;
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

            Required payload fields for each action:
            - update_current_concerns: { "adds": [{ "key", "summary", "content", "salience", "importance", "reason" }], "resolves": [{ "entry_id" }], "demotes": [{ "entry_id", "destination" }] }
            - update_relational_layer: { "relationship_scopes": ["person_id"] (REQUIRED), "key", "summary", "content", "salience", "importance", "confidence", "reason" }
            - store_archive_entry: { "summary", "content", "salience", "importance", "reason" }
            - propose_core_self_update: { "target_key", "operation", "summary", "content_patch", "reason", "confidence" }
            - demote_current_concern: { "entry_id", "destination", "reason" }
            - schedule_wake_up: { "delay_seconds" (60-86400), "reason" }
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
