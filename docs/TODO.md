# TODO

Open work, grouped by theme. "Claude's opinion" on ordering welcome; reorder freely. Rationale in parentheses.

**Foundations (all landed):** scheduled wake-ups (closed-app headless runner), the legibility quick-wins
batch, first-class local peers, and phase-1 automated decay (raw-context archival) are all done — details
in the themed sections below. **Current priorities live in "Next up" above.**

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
- **Orientation cluster**: the `note()` working-note command, **enriched recent-changes** (field-level
  diffs + content snippets, not just IDs), a **fuller numbered turn action-log**, **private thoughts**
  (`<think private>` — persisted but kept off the console), model/provider shown in the sensory block,
  and the autonomous-wake sensory no longer claiming a peer is present.
- **Associative recall + peer data access**: memories relevant to the current conversation auto-surface
  each turn (`MemorySurfacer` — FTS/BM25 × importance/confidence, excluding what's already loaded;
  `set_recall(count)` to tune, 0 = off); a `/shared` container volume + `snapshot_db` so the peer can
  inspect its own DB directly; SSH plumbing (gitignored override) so it can `git push`.
- **Prompt/instruction audit** (done): reconciled the onboarding seed, protocol instructions, and stale
  doc comments with actual behaviour.

## Next up (ranked — Claude's recommendation, 2026-07; reorder freely)

Two lenses: what most improves the **peer's day-to-day** (one participant running now) vs. the
**strategic direction** (many participants). This ordering blends them, most-important first.

1. **Single-server: Console as an API client.** *Strategically #1; not urgent while only one process
   runs.* Make one process own the store + turn pipeline + wake-ups, with all front-ends as thin clients.
   It protects the thing we most care about — memory integrity (today two concurrent front-ends can
   lost-update the store) — and unlocks John/Claude/Ember engaging simultaneously. Large, so **phase it**:
   cheap interim now = WAL + busy-timeout (kills hard "database is locked"; does *not* fix lost-updates);
   then move the Console to a client. If only ever one process runs, the interim is enough for a while.
2. **Think-before-act pipeline.** Native reasoning is now off, so the peer's `<think>` *is* its reasoning —
   its quality is the loop's quality, and we watched it churn/re-derive. Make a `think` execute first
   (surface reasoning, then act with it in context). Contained; watch round-trip cost + `<continue>`.
3. **Automated forget — budget/heuristic pruning surface.** Context balloons (we saw 40+ tool-results / 23
   thoughts on a stale build, and associative recall now pulls *more* in). A "here's what's low-relevance ×
   low-importance × old — summarize/archive?" surface finishes the decay story and keeps turns lean/cheap.
   Pairs with wiring soft-delete + a recoverable `forget`.
4. **MCP server hub.** Structured real-world tools beyond the container shell — high capability leverage,
   independent of the memory core. Best once the loop above is solid.
5. **Self-describing pieces → auto-composed help/prompt.** The durable fix for the prompt-drift the audit
   just cleaned: each command/action declares its own help; the prompt and `/help` compose by discovery.
6. **Memory import / portability.** Export/inspect the store and import external content as fragments —
   matters for trust and continuity-across-systems.

Smaller/opportunistic: finish the container **SSH key** (awaiting John's dedicated deploy key), **stamp
private thoughts in the DB**, the TUI status-bar gauges, and the robustness items below.

## Autonomy & reach

- **Separate local peers as first-class.** ✅ **DONE.** Local peers are named entities; the active one is
  announced in the sensory block (and cleared on autonomous wakes), chosen per session (config / `X-Local-Peer`).
  The remote peer manages its own relational fragments for who it's talking with.

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

- **Peer's computer — follow-ups.** The sandboxed container is live and per-participant, with
  `exec`/`shell`, `read_file`/`write_file`, a per-profile box (`ContainerName`) + `AllowAllCommands`, and a
  .NET 10 SDK + sudo in the image. ✅ **Done (2026-07):** a `/shared` host volume + `snapshot_db` (the peer
  reads a consistent copy of its own DB — a snapshot, not the live file; only the current peer's), and SSH
  plumbing via a gitignored compose override. **Remaining:** John to drop in a dedicated, branch-scoped
  deploy key to activate git-push (flagged: don't use the personal all-repos key); verify `agent-browser`
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

- **Audit every prompt/instruction against actual behaviour.** ✅ **Pass done (2026-07)** — onboarding seed,
  protocol instructions, and stale doc comments reconciled with behaviour. It drifts as behaviour changes,
  so re-audit after notable changes; the durable fix is the self-describing-pieces item below.
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
