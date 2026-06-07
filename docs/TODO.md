# TODO

One unified priority list (Claude's opinion — reorder freely). Rationale in parentheses.

Reframe from the 2026-06 discussion: the context-window problem is **not** a blocking
architectural keystone. The catastrophic failure mode is *silent* truncation — so once the
peer can **see** its budget and has **hands** to act (summarize / archive), it can curate
manually. Fully-automated forgetting becomes a convenience layered on later, not a prerequisite.

## Tier 1 — eyes + hands (makes the memory core self-sufficient)

1. ✅ **Context/budget awareness in the sensory block** — DONE. Calibrated estimate vs.
   model-aware effective budget, with escalating nudges. (The "eyes.")

2. ✅ **Switch Weight → Relevance** — DONE. Renamed the junction property/column/command field
   and the prompt header (`w:` → `r:`). The peer now sees/sets "relevance to the current prompt."

3. ✅ **summarize_fragments / set_summary commands** — DONE. `summarize_fragments` folds a list
   into a new Summary fragment and archives the originals from context (recoverable via
   load/fetch — archive, not delete); `set_summary` attaches a précis without removing. The
   peer writes the summary text itself (no black-box summarizer). (The "hands.")

4. ✅ **toggle_summary_display command** — DONE. Collapses a list of fragments to their summaries
   (header marks `| collapsed`) to reclaim room without losing detail; fully reversible.

5. ✅ **Plain-language command errors** — DONE. `CommandHandler.Humanize` translates JSON
   type-mismatch messages (e.g. "System.String … System.Int64") into the peer's vocabulary
   ("a value of type text can't be used where a whole number is expected").
   ✅ **Extended (small-model hardening pass):** malformed calls are now recoverable (a bad call
   becomes an `__error__` with a plain reason + snippet; sibling calls and other tags still run,
   instead of one slip nuking the turn); errors are format-neutral (no JSON syntax shown to a
   tagged peer); typo'd fields/commands get Levenshtein "did you mean" hints instead of being
   silently dropped.

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

9. ✅ **Tag management surface** — DONE. `list_tags` (tree view) and `delete_tag` (permanent for
   the label only — fragments untouched) added; descriptions spell out reversibility. Rename still
   open if it's wanted later.

## Tier 3 — reach & real-world

10. ✅ **SSE streaming on the API** — DONE. `GET /api/conversation/stream` pushes
    reply/reasoning/tool/thought events live (backlog replay + live, deduped, `Last-Event-ID`
    resume), alongside the existing poll endpoint.

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

15. ✅ **"First wake" / onboarding experience** — DONE. A new context gets a one-time, removable
    System guide that scaffolds the *process* (discover commands, choose who to be) without
    authoring the peer's identity. Plus reversibility guidance in the seed so the peer manages
    memory without fear.

## Open decisions (need John's call)

- **Narrow or drop `IsDeleted`?** Finding from the 2026-06 audit: `IsDeleted` lives on `BaseEntity`,
  so all six tables carry the column and ~12 read queries filter `IsDeleted = 0` — **but the only
  thing that ever sets it (`EntityRepository.DeleteAsync`) has zero callers.** It is currently 100%
  dead/speculative. The real deletion models are already different per entity: Tags hard-delete
  (`DeleteTreeAsync`), fragments use `Status=Archived` + junction removal, events use `Status`,
  audit/action logs are append-only.
  - **My recommendation: narrow to `ContextFragments` (+ `WorkingContexts`).** Soft-delete = recoverable
    erasure, which only makes sense for *peer memory* — that's where the planned **forget/undo** (Tier 2
    #7, Possible-future undo) will land. Move `IsDeleted` off `BaseEntity` onto those two entities; drop
    the column + dead filters from Tags/Sources/ScheduledEvents/ActionLogs via a new `001_*` migration.
  - Alternatives: fragments-only (working contexts could use a Status/Archived concept instead), or
    remove entirely now and re-add deliberately when forget/undo is built (purest YAGNI, but throws
    away scaffolding for explicitly-planned features).
  - Low-risk either way (new migration; existing DBs unaffected by the InitialCreate edit). ~15 min to
    implement once you pick. Held for your decision since you flagged it as a question.

## Possible future

- **Undo stack.** A true undo for context operations. Lower priority because archive-not-delete +
  clear reversibility labelling already make almost everything recoverable (load/fetch); revisit
  if reversible-by-design proves insufficient.
- **Startup schema validation.** Fail-fast check comparing actual DB columns to expected, to catch
  Dapper SQL/schema drift at launch (integration tests already catch most).

## Standing concerns (revisit, not scheduled)

- **Broker concurrency.** One in-flight completion today (fine — turns serialize). Revisit if
  multiple sessions/contexts ever run at once.
- **Self-modification safety.** If/when the remote peer (or Claude) gets file/web/self-editing
  tools to work on the system from inside: sandboxing, review gates, and the audit trail are
  prerequisites, not afterthoughts. Same care as everywhere else.
