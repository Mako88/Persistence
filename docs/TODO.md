# TODO

Open work, grouped by theme. "Claude's opinion" on ordering welcome; reorder freely. Rationale in parentheses.

**Agreed priority (2026-06-10 — John + Claude + Synth):** (1) ✅ **scheduled wake-ups when the app is
closed** — DONE (headless wake-runner + poll; verified — the peer woke on its own, self-audited, reflected).
✅ A batch of legibility **quick wins** — DONE (uptime in the sensory block, DB-durability framing for new
peers, singular/plural command & field spellings, README freshness). Now (2) **first-class local peers**
— DONE. Then (3) **automated forget/decay** — first phase DONE (raw-context decay + research persistence;
see below). Remaining: the importance/relevance heuristic pruning-candidate surface. Everything else follows.

The **"eyes + hands" memory core** is complete — budget awareness, relevance, summarize/collapse/remove,
plain-language errors, browsing/swapping working contexts, first-class proposals, generic/polymorphic
tagging, wake-ups-drive-a-turn, surfaced proposal resolutions, the recent-changes digest, and the
per-turn command catalog. Silent truncation is no longer the failure mode, so **automated forgetting is
now a convenience to layer on, not a prerequisite**. The peer also now has a sandboxed container
"computer" (`shell`: web search/fetch + scripting), verified end-to-end.

**Recent work (2026-07 — John + Claude via Claude Code):** landed since the above —
- **Native Anthropic client** (`ModelProvider.Anthropic`, Messages API, streaming + non-streaming) as a
  first-class provider; the OpenAI Responses/Chat clients refactored to expose real provider usage
  (`IModelClient.LastUsage`), consumed in one place in the turn handler.
- **Running cost + real usage readout** in the sensory block (data-driven `ModelPricingProvider` +
  `model_pricing.json`), and **Anthropic prompt caching** (a `cache_control` breakpoint on the stable
  prefix, with cache-token-aware cost).
- **Native reasoning off by default** (`ReasoningEffort: "off"`) — the peer reasons in the persisted
  `<think>` channel instead of a redundant, ephemeral one.
- **Thought persistence**: `<think>` is saved as a `Thought` fragment on a rolling window
  (`ThoughtContextWindow`, default 8, archived-not-deleted); **`=== THIS TURN ===`** delineation marker;
  the id-0 label "transient" → "new" (it misled the peer into thinking thoughts don't persist).
- **Per-participant containers**: `exec`/`read_file`/`write_file` commands, a per-profile `ContainerName`
  + `AllowAllCommands` override, and .NET 10 SDK + sudo baked into the image.
- **Orientation cluster** (this batch): the `note()` working-note command, **enriched recent-changes**
  (field-level diffs + content snippets, not just IDs), a **fuller numbered turn action-log**,
  **private thoughts** (`<think private>` — persisted but kept off the console), model/provider shown in
  the sensory block, and the autonomous-wake sensory no longer claiming a peer is present.

## Autonomy & reach

- **Separate local peers as first-class.** (NEW — the current build.) Model local peers (the human/agent at the keyboard) as
  named entities and **automatically surface who the remote peer is talking with** — inject the active
  local peer's identity into the sensory block (and a relational fragment) so the remote peer always
  knows. The active peer is chosen per session (config/API). Keep v1 lightweight: the remote peer already
  has the tools to manage relational fragments and load the right ones when the local peer shifts — so the
  system just needs to reliably *announce* the active local peer, not manage per-peer memory itself.

- **Single-server architecture: front-ends as API clients (not DB co-owners).** (NEW.) Today each
  front-end (Console TUI, API) is its own process that opens the SQLite store and runs its own turn
  pipeline + `WakeUpMonitor`, so running two at once risks **lost-update/desync of the peer's memory** —
  the turn lock is in-process only, there's no cross-process coordination (and `journal_mode=delete`,
  no WAL/busy-timeout → hard "database is locked"; double-fired wake-ups; single-slot model contention).
  Direction (John): make the **Console a client of the API** rather than a DB co-owner, so **one**
  process (the API/server, eventually hosted) owns the store + turn pipeline + wake-ups, and all
  front-ends are thin clients. This enables multiple participants (John, Claude, Ember) engaging
  **simultaneously** through one coherent backend, and pairs naturally with first-class local peers
  (each client identifies its local peer). Cheap interim hardening if multi-process lingers: WAL +
  busy-timeout (stops hard lock failures; does NOT fix lost-updates — that needs the single-owner model).

- **Automated forget / memory decay.** ✅ **First phase DONE (2026-06-10).** *Raw-context decay + research
  persistence:* raw material — conversation (`ChatMessage`) and tool/command results (`ActionResponse`) —
  is **archived (never deleted) once it falls outside a recent window** (`RawContextWindow`, default 30);
  peer-authored fragments (Identity/Relational/Personal/Summary) are never touched. The sensory block
  reports what was archived and that it's restorable (`list_fragments(relevant_to=…, in_current_context=false)`
  → `load`). `ActionResponse` is now **persisted** (was transient), so research/tool output survives across
  turns; `list_largest` lets the peer see what's taking space; onboarding teaches "capture what matters into
  your own fragment — the raw version scrolls out." Deterministic and peer-legible (not an LLM compressing
  memory). **Remaining:** the *budget/heuristic* layer — a low-relevance × low-importance × age
  pruning-candidate surface (a shortcut command that proposes what to summarize/archive when the budget
  tightens), and the open question of budget-triggered only vs. ongoing natural decay. (Pairs with wiring
  soft-delete + include-deleted surfacing, under "Possible future".)

- **Peer's computer — follow-ups.** The sandboxed container is live and now per-participant, with
  `exec`/`shell`, `read_file`/`write_file`, a per-profile box (`ContainerName`) + `AllowAllCommands`, and a
  .NET 10 SDK + sudo in the image. **In progress (2026-07):** a **`/shared` host volume** for file exchange,
  a **periodic snapshot of the active peer's DB** into it (read-only data access for the peer — snapshot,
  not the live WAL file; only the current peer's DB, not the whole `dbs/`), and an **SSH deploy key** so the
  peer can `git push` (John to create a dedicated, branch-scoped key). Remaining: verify `agent-browser`
  through the peer (JS-heavy pages); egress / secret hygiene as capabilities widen; consider a `WebTool`
  source type for provenance of web-derived fragments.

- **MCP server hub**, with a "catalog" MCP server exposed to start. Structured real-world tools for the
  peer — distinct from the container's shell access; high value, independent of the memory core.

- **Memory import / portability.** Import external content as fragments (e.g. seed a peer from an exported
  conversation) and export/inspect the whole store outside the app. Matters for trust,
  continuity-across-systems, and both the embodied and digital-native directions.

## Turn pipeline

- **Think-before-act: `think` executes *before* the rest of the turn.** (Sharpened from the scratch note
  "force proper think usage.") Models currently emit `<think>` in the same response as their actions and
  reply, so the thinking is surfaced *alongside* — not before — the commands it should inform. Make a
  `think` execute and return first (a pass that surfaces reasoning), then run the actions/reply with that
  reasoning in context. Care needed: extra model round-trips, and how it interacts with `<continue>` and
  the iteration budget. Investigate before committing to an approach.

## System prompt & legibility

- **Audit every prompt/instruction against actual behaviour.** (NEW, 2026-07.) The system prompt, protocol
  instructions, command descriptions, and onboarding text drift as behaviour changes — the `<think>` "not
  saved" text was stale for a while before it was caught. Do a full pass and reconcile them with what the
  code actually does; ideally pair with the self-describing-pieces item below so this stops recurring.
- **Stamp private thoughts in the DB.** (NEW, 2026-07.) `<think private>` currently just skips the console
  event; the fragment isn't marked private. Add a `private` flag on the Thought fragment so privacy can be
  enforced beyond the live console (e.g. hidden from other viewers / exports) down the road.
- **Self-describing pieces → auto-composed info/help text.** Let each action/command/handler declare its
  own help text and compose the prompt / local `/help` by discovery, so adding a piece never means editing
  a central string. (Partly advanced: the per-turn command catalog now auto-composes the command list;
  extend the spirit to the top-level ModelActions, the protocol/format instructions, and `/help`.)

## Robustness & smaller items

- **Graceful state flush on close.** (Scratch — "save session information on close.") Ensure in-flight
  context/state is reliably persisted on shutdown so nothing is lost.
- **Right-click dialog position (TUI).** (Scratch.) The right-click context menu displays in the wrong
  location — fix the positioning.
- **Input slowness — investigate.** (Scratch.) Confirm whether input lag is the host being overloaded
  (e.g. the GPU busy with the local model) or something in our input path.
- **Split `ExecuteListFragmentsAsync`** into a source-then-filter two-phase — deferred until it has a few
  more tests (it's the one thin-coverage spot; add tests first, then refactor the ~88-line method).
- **Reconsider proposal kinds.** Proposals are for serious changes to existing (identity) fragments —
  primarily modify + protection changes (+ maybe archive). `AddFragment`-as-proposal may not be needed.
- Optionally share the open-proposals list formatting between the remote command and the local
  `/proposals` command (low value; the surfaces differ slightly).

## TUI / front-end (carry-forward)

- **Collapsible Actions pane** — rebuild the Actions tab as a tree: `[time] command` collapsed; expand
  shows request + response in two colours (Terminal.Gui v1 `TreeView`).
- **Context-budget gauge in the status bar** — the `ContextBudgetUpdated` event already fires from
  `PromptFormatter`; the display just needs to consume it.
- **Open-proposals indicator in the status bar** — an Orchestrator push of the open-proposal count
  (mirrors the `ShowScheduledEvents` wiring).
- **Dynamic history load on scroll-up** — load older messages when the user scrolls to the top.
- **Schedule tab name** — revisit if a better fit than "Schedule" emerges.

## Possible future

- **Enum display names via `[Description]`** — render a description for UI-facing enums instead of
  `value.ToString()` (e.g. `Triggered` shown as "Complete"), via a small `enum.GetDescription()` helper.
- **Undo stack** for context operations — lower priority while archive-not-delete + clear reversibility
  labelling already make almost everything recoverable; revisit if reversible-by-design proves insufficient.
- **Startup schema validation** — fail-fast check comparing actual DB columns to expected, to catch Dapper
  SQL/schema drift at launch.
- **Wire up `Notes`** — peer-addable free-text note on any entity (on `BaseEntity`, plumbed through
  INSERT/UPDATE but unset). Intent: attach a note to a fragment, tag, or audit-log row ("why this was
  deleted"). Note: `AuditLogs` has no `Notes` column yet — covering audit rows needs a migration.
- **Wire soft-delete on fragments/working contexts.** `IsDeleted` is filtered on read but not yet *set* by
  any command — the hook for the planned forget/undo. Add a recoverable "forget" command, plus
  `include_deleted` on list/browse and un-delete on `load`, so soft-deleted items stay visible and
  recoverable through the command surface.

## Open observations

- **Self-recorded lessons can encode mistakes.** When coached to "save what you learned," a peer can save
  the *wrong* thing first; persisted lessons aid continuity but aren't self-correcting. (Inherent — noted,
  relevant if/when we lean on them for "learning over time.")

## Standing concerns

- **Broker concurrency.** One in-flight completion today (fine — turns serialize). Revisit if multiple
  sessions/contexts ever run at once.
- **Self-modification & sandbox safety.** Now real: the peer has a container "computer" and will get more
  tools/actuators over time. Container isolation (the real boundary), the allowlist, review gates, and the
  audit trail are prerequisites as capabilities widen — not afterthoughts.
