# TODO

One unified priority list (Claude's opinion — reorder freely). Rationale in parentheses.

Reframe from the 2026-06 discussion: the context-window problem is **not** a blocking
architectural keystone. The catastrophic failure mode is *silent* truncation — so once the
peer can **see** its budget and has **hands** to act (summarize / archive), it can curate
manually. Fully-automated forgetting becomes a convenience layered on later, not a prerequisite.

The "eyes + hands" tier (budget awareness, relevance, summarize/collapse, plain-language errors)
is **complete** — the memory core is self-sufficient. What remains rounds out self-curation, then
extends reach.

## Tier 1 — rounds out self-curation

1. ✅ **Browsing + swapping working contexts** — DONE. `list_contexts` (lightweight summaries —
   name, fragment count, last-accessed, current marked — no fragment hydration), `create_context`
   (create-only, doesn't auto-switch), and `switch_context` (validates against non-deleted summaries,
   repoints the session). A mid-turn switch is honored at the iteration boundary: TurnHandler saves the
   current context and reloads the target, so `continue`-ing after a switch operates on the new context.
   Per-context *filtering* of the list was skipped — trivial with a handful of contexts; revisit if many.

2. ✅ **Implement proposals** — DONE.
   Proposals are now a first-class entity (`Proposals` table, migration `002`), no longer a fragment
   type. A proposal carries an *executable* self-change (add/modify/remove a fragment); accepting it
   applies the change, **including to protected fragments** — the only way they change. `ProposalService`
   does the mechanics, shared by both surfaces:
   - **Remote peer:** `propose` / `list_proposals` / `accept_proposal` / `reject_proposal` commands.
     Self-accept gated by a **deliberation gap** (can't accept in the turn it was proposed — enforced
     via `ISessionContext.TurnStartedUtc`). `update`/`remove` on a protected fragment point at `propose`.
   - **Local peer:** `/proposals` / `/accept <id>` / `/reject <id>` slash commands in the Orchestrator,
     under the turn lock so they can't race a turn.
   - `ProposalApproval` config (Self / Participant / Both) decides who may accept.

   Follow-up idea (not scheduled): resolved proposals aren't surfaced back to the remote peer
   (`list_proposals` shows only open ones) — when the local peer accepts/rejects, the peer only sees
   the *effect*. Could inject a short note into context so the peer learns its proposal was acted on.

3. **Automated forget / memory decay** (the down-prioritized context item, reborn).
   (Triggered under budget pressure to start: deterministic, peer-legible rule —
   low-relevance/low-importance/old fragments get **archived, never deleted** (per PRINCIPLE.md),
   `IsProtected` immune, and the sensory block reports what was archived + that it's restorable.
   NOT an LLM compressing memory for the peer — the peer must understand and predict its own
   forgetting. Open question: budget-triggered only vs. ongoing natural decay.)

## Tier 2 — reach & real-world

4. **AnthropicModelClient.** (Real Anthropic provider alongside OpenAI; `LocalClaudeModelClient`
   already in. Enables the Claude-as-remote-peer direction natively.)

5. **Memory import / portability.**
   (Import external content as fragments — e.g. seed a peer from an exported conversation; and
   export/inspect the whole store outside the app. Matters for trust, continuity-across-systems,
   and the embodied-AI direction. See "seed a peer from this collaboration's history" idea.)

6. **MCP server hub**, with a "catalog" MCP server exposed to start.
   (Big surface expansion — real-world tools for the peer. High value, independent of the
   memory core, so it comes after the core is solid.)

## Code health & docs (queued 2026-06-08, do after proposals Stage B)

- ✅ **Verify test stack hygiene (xUnit/Moq)** — DONE. Moq is pinned at 4.20.72, which is past the
  4.20.0/4.20.1 SponsorLink builds (removed in 4.20.2) — verified clean, no SponsorLink in the restore
  graph. Kept Moq (familiar API; the only fork, NexusKrop.Moq, was deprecated once upstream cleaned up)
  with a csproj comment flooring it at >= 4.20.2. xUnit 2.9.3 / runner 3.1.4 are current.
- ✅ **Test-coverage + unit-vs-integration review** — DONE. Coverage raised from **55%→79% line /
  42%→62% branch** (Persistence.Tests), 139→200 unit/integration tests (+25 API). Added unit tests
  (mocked repos) for the pure logic — `ExecuteActionsHandler`, `ProposalService`, the in-memory
  `ManageContextHandler` commands, `RespondToUserHandler`, `LocalPromptBuilder`, `WakeUpMonitor`
  (made `CheckAndFireAsync` internal + `InternalsVisibleTo` to dodge the 30s timer),
  `TaggedProtocolInstructions`, more `TurnHandler` branches, AppConfig env-override — and integration
  tests (temp SQLite) only where it tests the real thing: the DB-coupled `ManageContextHandler`
  commands (tags/sources/fetch/load) and the repository query SQL (scheduled-event/action-log/audit).
  Type-fit verdict: the codebase was already mostly right (pure→unit, persistence/API→integration); no
  "unit test that should be integration" found. Remaining uncovered is intentional — TUI rendering,
  the local testing client, and DI bootstrap (`Program`/`Initializer`/`IoC`).
- ✅ **Replace `design.md` with a real architecture doc** — DONE. Wrote a focused doc set under
  `docs/architecture/` (overview, turn pipeline, prompt & model providers, memory model, data layer,
  extensibility, remote-peer & surfaces) with Mermaid diagrams, aimed at someone new to the codebase
  and matching the code as built. `design.md` is now a thin index pointing into it; README updated to
  point at `docs/architecture/`.
- ✅ **Codebase drift sweep** — DONE (2026-06-08). Fixed directly: a **critical infinite-loop bug**
  (TurnHandler parse-retry never incremented `iteration` → unbounded model calls on repeated parse
  failure; now counted + regression-tested); removed dead code (`AuditEventType.Deleted`,
  `ColoredTextView.ColorLinesContaining`); fixed stale docs (README "ANSI console UI", ADR-0004
  superseded marker); deduped `ParseProposalApproval` → `IAppConfig.ResolvedProposalApproval()`.
  Carry-forward items below.

### Drift-sweep carry-forward

- ✅ **Proposal accept is now atomic** — DONE. `ProposalService.AcceptAsync` opens one transaction
  spanning the proposal-status save and the carried change (via a shared `SqliteConnectionString`
  helper now used by EntityRepository/DatabaseManager too). Accept-and-apply commit together or
  neither does. (ProposalService's accept path is now DB-coupled, so its coverage moved to the
  integration `ProposalTests`, which now exercise add/modify/remove through the real transaction.)
- ✅ **Unique index on junction `(WorkingContextId, [Order])`** — DONE (migration `003`), guarding the
  silent-fragment-drop-on-load invariant.
- **Proposal origin-context** — mostly a non-issue (John's correction): a *modify* edits the shared
  fragment row, so every context referencing it reflects the change on next hydration — no
  per-context bookkeeping. The only residue is the *add* case landing in the active context, which
  folds into the proposal-model question below (do we even want add-proposals?).
- ✅ **Fragment-id loop helper** — DONE. set_summary/toggle/summarize now share
  `ApplyToContextFragments` (id → in-context lookup → skip-tracking); the unit tests cover the skip
  messaging. (`load` was left out — it fetches from the DB, a different shape.)
- **Split `ExecuteListFragmentsAsync`** into a source-then-filter two-phase (John: yes). Deferred:
  it's the one item with thin test coverage (one list_fragments test), so it should get a few more
  tests *first*, then refactor — not worth rushing a 88-line method with little safety net.
- Optionally share the open-proposals list formatting between the remote command and the local
  `/proposals` command (low value; the surfaces differ slightly).

### Feature backlog from the sweep (John reviewed — directions noted)

- ✅ **Wake-ups now drive a turn** — DONE. The Orchestrator subscribes to `ScheduledEventTriggered`,
  waits on the turn lock (no race with user input / proposal commands), and runs an autonomous turn.
  The wake is framed to the peer as a transient note ("you woke on your own…") that includes its own
  **`wake_prompt`** note-to-self if it left one when scheduling (new optional field on `schedule`,
  migration `004`). New tests cover the fire→turn flow, the wake-note injection, the schedule field,
  and persistence. Remaining nuance: `WakeUpMonitor.MarkTriggeredAsync` still runs off the turn lock
  (marking-triggered isn't context-mutating, so low risk) — revisit if it matters.
- ✅ **Generic / polymorphic tagging** — DONE (data layer + peer-facing commands).
  ✅ The three per-entity junctions were consolidated into one polymorphic `EntityTags`
  `(TagId, EntityType, EntityId)` table (migration `005`, existing fragment links carried over, old
  tables dropped). A single `EntityTagRepository` (`SetTags` / `RemoveTags` / `GetTagsFor` /
  `GetEntityIdsWithTag`) backs it; `ContextFragmentRepository`, `WorkingContextRepository`,
  `ScheduledEventRepository`, and `TagRepository` are all rewired through it. Fragment tagging behaves
  identically (all existing tests pass); **events are now taggable at the data layer** (the dead
  `ScheduledEventEntity.Tags` scaffolding is wired up — write + hydrate + entity-scoped query, tested).
  Adding a new taggable type now needs zero schema change.
  ✅ **Phase 2 (peer-facing) — DONE.** `tag` / `untag` / `fetch` now take an `entity_type` arg
  (default `fragment`); `context` and `event` are also taggable/searchable. A shared `TagTarget`
  resolves the entity (fragment in context / current working context / event by id) so the apply
  loops stay single-path. `WorkingContextEntity` gained a `Tags` collection, hydrated + persisted via
  `EntityTags` on the end-of-turn context save (the current context is the one in memory); events
  persist immediately through `IScheduledEventRepository` (they're not part of the context save).
  `fetch` returns fragments / working-context summaries / scheduled events by tag (current-context
  tags merged in for the not-yet-saved-this-turn case). Integration-tested end to end.
  - **Deliberately scoped:** tagging a context acts on the *current* one — to tag another, the peer
    `switch_context`s first (avoids the dual in-memory/by-id persistence hazard). Revisit if
    tag-by-id across contexts proves needed.
- ✅ **Protect / unprotect via proposal** — DONE. Two new `ProposalKind`s (`ProtectFragment` /
  `UnprotectFragment`, no migration — enums store by value) + `propose kind=protect|unprotect`. Accept
  flips `IsProtected` atomically. An end-to-end test shows unprotect-then-edit-directly working.
- ✅ **Surface proposal resolutions back to the peer** — DONE. When the local peer `/accept`s or
  `/reject`s, the Orchestrator queues a system note (`ITurnHandler.EnqueueSystemNote`) that surfaces to
  the peer as a transient fragment at the start of its next turn ("Your peer reviewed and
  accepted/declined your proposal #N").
- ✅ **Recent-changes-to-self digest in the sensory block** — DONE. `AuditLogRepository
  .GetRecentSelfChangesAsync` returns the last N audit entries (newest first), excluding ChatMessage/
  System fragment noise; `TurnHandler` fetches once per turn and passes them to `PromptFormatter`,
  which renders a humanized "Recent changes to your memory" section ("[2m ago] fragment #42 modified").
  (Action logging was already in place — `TurnHandler.DispatchActionAsync` logs every action's
  type/payload/result.)

  ⚠️ **Enum storage finding (investigated, resolved as won't-fix).** Enums are stored by **integer
  value**, not the string name the `EnumTypeHandler` doc implied. Root cause: Dapper's `LookupDbType`
  converts enums to their underlying integer *before* checking type handlers, and InterpolatedSql.Dapper
  binds parameters without going through Dapper's type system at all — so the handler's `SetValue`
  never runs for enum params (neither `AddTypeHandler` nor `AddTypeMap(DbType.String)` changes it,
  verified). The only fixes were hacky (reconstruct FormattableStrings to rewrite enum args, or
  `.ToString()` at ~15 sites), so per John we left it: integer storage works (round-trips + comparisons
  are internally consistent). **Documented the one real gotcha** in `EnumTypeHandler`'s remarks: raw-SQL
  comparisons against an enum column must interpolate the enum *value*, never a hardcoded string
  literal (that's how the digest query broke first).
- ✅ **Round out multi-context** — DONE. `rename_context` / `set_context_summary` added (a peer can
  now re-describe a context as it evolves). Context tags still come via generic tagging above.
- **Reconsider proposal kinds.** John's model: proposals are for *serious changes to existing
  (identity) fragments* — primarily modify (and protection changes, and maybe archive). Adding a
  fragment isn't destructive (just `add`), so `AddFragment`-as-proposal may not be needed. Revisit the
  `ProposalKind` set when doing protect-via-proposal.
- **Self-describing pieces → auto-composed info text.** Let each action/command/handler (and similar
  extensible pieces) declare its own info/help text via an attribute or method, then compose the
  prompt/help automatically by discovery — so adding a new piece never means editing a central prompt
  string or list. The remote `[Command]`/`[CommandField]` + `list()` system already works this way;
  extend the spirit to: the top-level ModelActions, the protocol/format instructions in
  `TaggedProtocolInstructions`, and the local `/help` (currently a hand-maintained block). Aligns with
  the single-location/auto-discoverable extensibility preference — avoid "remember to update N places."

## TUI / front-end (in progress 2026-06-09)

Done this pass: history folded into the main conversation pane with timestamps (History tab removed);
`You`/`Remote Peer` role labels; per-kind colour palette tuned for legibility (no dark gray; cyan/blue
kept non-adjacent); timestamps in conversation/reasoning/actions; `Tools`→`Actions`; `Reasoning`→
`Thoughts`; new `Schedule` tab (order Thoughts → Actions → Schedule → Debug, padded titles) backed by
an Orchestrator push of pending events via `IDisplayProvider.ShowScheduledEvents`; black status bar
with state/model/session segments; the selected tab title stays highlighted even when unfocused
(`HighlightedTabView`); Ctrl+Left/Right pane switching (Ctrl+Tab fallback) + hint; a `--preview` mode
for visual iteration.

Carry-forward:
- **Collapsible Actions pane.** Rebuild the Actions tab as a tree: each entry `[time] command`
  collapsed; expand shows request + response in two distinct colours. (Currently a timestamped text
  log.) Terminal.Gui v1 `TreeView` is the likely widget.
- **Context-budget gauge in the status bar.** Needs the budget value plumbed to the display (e.g. a
  `ModelBudgetUpdated` event from `PromptFormatter`/`TurnHandler`); the display doesn't compute it.
- **Open-proposals indicator in the status bar.** Orchestrator push of the open-proposal count
  (mirrors the `ShowScheduledEvents` wiring) → a status segment.
- **Dynamic history load on scroll-up.** The conversation shows the startup history batch; load older
  messages when the user scrolls to the top.
- **Schedule tab name.** Went with "Schedule" (alternatives: Agenda, Upcoming, Wake-ups, Reminders) —
  revisit if a better fit emerges.

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
  the hook for the planned forget/undo (Tier 1 #3). Add a recoverable "forget" command when ready.
  - **Surfacing deleted items:** when forget exists, the list/browse commands need an opt-in flag
    (e.g. `include_deleted=true` on `list_fragments` and the working-context list) so a peer can see
    what it soft-deleted, and `load` should be able to bring a deleted fragment back (un-delete on
    load). Otherwise a soft-deleted fragment is invisible and unrecoverable through the command
    surface — defeating the "recoverable" point. (John's idea, 2026-06.)

## Open observations (noted, not scheduled)

- **Self-recorded lessons can encode mistakes.** (Inherent — noted, not fixed.) When coached to "save
  what you learned," Qwen first saved the *wrong* usage. Persisted lessons aid continuity but aren't
  self-correcting; relevant if/when we lean on them for "learning over time."

## Standing concerns (revisit, not scheduled)

- **Broker concurrency.** One in-flight completion today (fine — turns serialize). Revisit if
  multiple sessions/contexts ever run at once.
- **Self-modification safety.** If/when the remote peer (or Claude) gets file/web/self-editing
  tools to work on the system from inside: sandboxing, review gates, and the audit trail are
  prerequisites, not afterthoughts. Same care as everywhere else.
