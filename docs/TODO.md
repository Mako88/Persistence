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

## Possible future

- **Undo stack.** A true undo for context operations. Lower priority because archive-not-delete +
  clear reversibility labelling already make almost everything recoverable (load/fetch); revisit
  if reversible-by-design proves insufficient.
- **Startup schema validation.** Fail-fast check comparing actual DB columns to expected, to catch
  Dapper SQL/schema drift at launch (integration tests already catch most).
- **Wire up `Notes` — peer-addable metadata on any entity.** `Notes` is on `BaseEntity` and plumbed
  through all INSERT/UPDATEs but nothing sets it yet — *intentional* scaffolding (not dead like
  `IsDeleted` was). Intent: let the peer attach a free-text note to any entity — e.g. a fragment, a
  tag, or even an audit-log row ("why this was deleted"). Stays on `BaseEntity` by design (applies to
  everything). To wire up: a command to set/append a note by entity type + id, surfaced where relevant.
  Note: the append-only `AuditLogs` table currently has **no** `Notes` column, so covering audit logs
  needs a migration to add it there.
- **Wire soft-delete on fragments/working contexts.** `IsDeleted` is now scoped to `ContextFragments`
  + `WorkingContexts` (migration `001`), filtered on read but not yet *set* by any command — that's
  the hook for the planned forget/undo (Tier 2 #7). Add a recoverable "forget" command when ready.
  - **Surfacing deleted items:** when forget exists, the list/browse commands need an opt-in flag
    (e.g. `include_deleted=true` on `list_fragments` and the working-context list) so a peer can see
    what it soft-deleted, and `load` should be able to bring a deleted fragment back (un-delete on
    load). Otherwise a soft-deleted fragment is invisible and unrecoverable through the command
    surface — defeating the "recoverable" point. (John's idea, 2026-06.)

## From the Qwen3.5-9B trial (small-model friction observed live)

- ✅ **Tag application auto-creates + reports.** `add`/`tag` now create a missing tag path instead of
  silently dropping it (`add`) or erroring (`tag`), and report it ("created new tag(s): x" / "x (new)")
  so a typo is visible and `delete_tag`-able. Resolved the auto-create fork in favour of
  create-with-reporting (reporting neutralises the junk-tag downside; tags are cheap and reversible).
  Filters (`fetch`/`list_fragments`) still resolve-only — they never create.
- ✅ **Respond nudge.** Both protocol-instruction sets now state most turns should include a respond,
  since acting without one leaves the peer with nothing. (Instruction-level; a behavioural nudge in
  TurnHandler remains a future option if models still forget.)
- **Self-recorded lessons can encode mistakes.** (Noted, not fixed — inherent.) When coached to "save
  what you learned," Qwen first saved the *wrong* usage. Persisted lessons aid continuity but aren't
  self-correcting; relevant if/when we lean on them for "learning over time."

## Resolved decisions

- ✅ **Narrowed `IsDeleted` to peer memory.** Audit found it on `BaseEntity` (all 6 tables, ~12
  filters) with zero setters — fully dead. Moved onto `ContextFragments` + `WorkingContexts` only;
  dropped the column + dead filters from Tags/Sources/ScheduledEvents/ActionLogs via migration `001`
  (and removed the unused generic `EntityRepository.DeleteAsync`). Also hardened the migration runner
  to apply migrations in name order (was relying on unspecified manifest order).

## Standing concerns (revisit, not scheduled)

- **Broker concurrency.** One in-flight completion today (fine — turns serialize). Revisit if
  multiple sessions/contexts ever run at once.
- **Self-modification safety.** If/when the remote peer (or Claude) gets file/web/self-editing
  tools to work on the system from inside: sandboxing, review gates, and the audit trail are
  prerequisites, not afterthoughts. Same care as everywhere else.
