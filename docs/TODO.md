# TODO

Open work, grouped by theme. "Claude's opinion" on ordering welcome; reorder freely. Rationale in parentheses.

**Agreed priority (2026-06-10 — John + Claude + Synth):** (1) ✅ **scheduled wake-ups when the app is
closed** — DONE (headless wake-runner + poll; verified — the peer woke on its own, self-audited, reflected).
✅ A batch of legibility **quick wins** — DONE (uptime in the sensory block, DB-durability framing for new
peers, singular/plural command & field spellings, README freshness). Now (2) **first-class local peers**
(next, in progress). Then (3) **automated forget/decay** (Synth leans here, though it can already prune by
hand — may drop in priority). Everything else follows.

The **"eyes + hands" memory core** is complete — budget awareness, relevance, summarize/collapse/remove,
plain-language errors, browsing/swapping working contexts, first-class proposals, generic/polymorphic
tagging, wake-ups-drive-a-turn, surfaced proposal resolutions, the recent-changes digest, and the
per-turn command catalog. Silent truncation is no longer the failure mode, so **automated forgetting is
now a convenience to layer on, not a prerequisite**. The peer also now has a sandboxed container
"computer" (`shell`: web search/fetch + scripting), verified end-to-end.

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

- **Automated forget / memory decay.** (The remaining self-curation item.) Budget-triggered to start: a
  deterministic, peer-legible rule — low-relevance/low-importance/old fragments get **archived, never
  deleted** (PRINCIPLE.md), `IsProtected` immune, and the sensory block reports what was archived + that
  it's restorable. NOT an LLM compressing memory — the peer must understand and predict its own
  forgetting. Open question: budget-triggered only vs. ongoing natural decay. (Pairs with wiring
  soft-delete + include-deleted surfacing, under "Possible future".)

- **Peer's computer — follow-ups.** (NEW, from this session's container work.) The sandboxed container +
  `shell` is live (web_search / fetch_url / scripting verified end-to-end with the peer). Follow-ups:
  verify `agent-browser` through the peer (JS-heavy pages); widen the allowlist as the peer's needs grow;
  egress / secret hygiene as capabilities expand; consider a `WebTool` source type for provenance of
  web-derived fragments.

- **MCP server hub**, with a "catalog" MCP server exposed to start. Structured real-world tools for the
  peer — distinct from the container's shell access; high value, independent of the memory core.

- **AnthropicModelClient.** Real Anthropic provider alongside OpenAI / OpenAiChat / LocalClaude — enables
  the Claude-as-remote-peer direction natively.

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
