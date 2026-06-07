# TODO

One unified priority list (Claude's opinion — reorder freely). Rationale in parentheses.

Reframe from the 2026-06 discussion: the context-window problem is **not** a blocking
architectural keystone. The catastrophic failure mode is *silent* truncation — so once the
peer can **see** its budget and has **hands** to act (summarize / archive), it can curate
manually. Fully-automated forgetting becomes a convenience layered on later, not a prerequisite.

## Tier 1 — eyes + hands (makes the memory core self-sufficient)

1. ✅ **Context/budget awareness in the sensory block** — DONE. Calibrated estimate vs.
   model-aware effective budget, with escalating nudges. (The "eyes.")

2. **Switch Weight → Relevance.**
   (Clarifies the model: relevance-to-now is what should drive what's loaded under pressure.
   Small, and it sharpens everything below it.)

3. ✅ **summarize_fragments / set_summary commands** — DONE. `summarize_fragments` folds a list
   into a new Summary fragment and archives the originals from context (recoverable via
   load/fetch — archive, not delete); `set_summary` attaches a précis without removing. The
   peer writes the summary text itself (no black-box summarizer). (The "hands.")

4. **toggle_summary_display command** (take a list of fragments).
   (Lighter lever: collapse known fragments to summaries to reclaim room without losing detail.
   `set_summary` now provides the summaries this would display.)

5. **Plain-language command errors.**
   (Type-mismatch errors still leak CLR type names — "System.String cannot be converted to
   System.Int64". Translate to the peer's vocabulary, e.g. "id must be a whole number." Cheap,
   and every model hits these. Found during the clarity walkthroughs.)

## Tier 2 — rounds out self-curation

6. **Implement proposals.**
   (The `Proposal` fragment type exists but is inert — it's how the peer reasons about changes
   to itself before committing. Central to the "self-curated" thesis.)

7. **Automated forget / memory decay** (the down-prioritized context item, reborn).
   (Triggered under budget pressure to start: deterministic, peer-legible rule —
   low-relevance/low-importance/old fragments get **archived, never deleted** (per PRINCIPLE.md),
   `IsProtected` immune, and the sensory block reports what was archived + that it's restorable.
   NOT an LLM compressing memory for the peer — the peer must understand and predict its own
   forgetting. Open question: budget-triggered only vs. ongoing natural decay.)

8. **Allow browsing, filtering, swapping working contexts.**
   (Multiple contexts = different modes/relationships; the peer needs to see and move between them.)

9. **Tag management surface.**
   (Can create/apply tags but not rename/delete/list-all/browse-the-tree. Minor until tags pile up.)

## Tier 3 — reach & real-world

10. **SSE streaming on the API** (in progress next). Live push of reply/reasoning/tool/thought
    events instead of polling; the event-log model is the precursor.
    (Sequenced first among Tier 3 because we're already halfway there.)

11. **AnthropicModelClient.** (Real Anthropic provider alongside OpenAI; `LocalClaudeModelClient`
    already in. Enables the Claude-as-remote-peer direction natively.)

12. **Real-model A/B of tagged vs JSON response format.**
    (The reason the experiment branch exists — decide merge-vs-keep-both. Only validated with
    Claude-as-peer so far, a biased sample; needs a clean model run.)

13. **Memory import / portability.**
    (Import external content as fragments — e.g. seed a peer from an exported conversation; and
    export/inspect the whole store outside the app. Matters for trust, continuity-across-systems,
    and the embodied-AI direction. See "seed a peer from this collaboration's history" idea.)

14. **MCP server hub**, with a "catalog" MCP server exposed to start.
    (Big surface expansion — real-world tools for the peer. High value, independent of the
    memory core, so it comes after the core is solid.)

## Later / first-experience

15. **"First wake" / onboarding experience.**
    (A new peer gets the system prompt and a bare context — staring at an empty room. Consider a
    gentle guided first turn or a couple of seed example fragments. Felt directly in the walkthroughs.)

## Standing concerns (revisit, not scheduled)

- **Broker concurrency.** One in-flight completion today (fine — turns serialize). Revisit if
  multiple sessions/contexts ever run at once.
- **Self-modification safety.** If/when the remote peer (or Claude) gets file/web/self-editing
  tools to work on the system from inside: sandboxing, review gates, and the audit trail are
  prerequisites, not afterthoughts. Same care as everywhere else.
