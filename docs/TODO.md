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

**★ Active direction — federated peers ([ADR-0007](adr/0007-federated-peers-runtime-room-client.md), 2026-07-12).**
The strategic pivot John + Claude settled on: not one central server owning all peers, but **many
single-owner runtimes** (each its own peer, in its own container = its body; DB + `vault/` on a persistent
volume = its self), meeting in shared **rooms**, with the TUI as a **hub** aggregating several peers into
one Discord-style chat. Terminology shifts `RemotePeer`/`LocalPeer` → `DigitalPeer`/`HumanPeer`. Phased:
**(0)** identity groundwork — per-message sender identity through the queue, peer names reaching the model,
message-id'd chat history (also finishes the ADR-0006 snapshot dedup), the rename *[in progress]*;
**(1)** containerize one peer (API-in-container, DB on a named volume); **(2)** multi-peer TUI (merged
chat + per-peer side tabs + peer selector); **(3)** the room (peer↔peer relay + turn-taking); **(4)** bring
Ember online. **Fast-follows:** peer-initiated API self-update (review-then-adopt, *not* auto-on-push so a
peer vets code before running it as itself); live config hot-reload. This subsumes item 4 below
(cross-peer channel) and the "simultaneous participants" thread throughout.

**Recently landed (2026-07):** WAL + busy-timeout interim (single-server phase 1); `prune_candidates`
(forget pruning surface); **recoverable `forget` / `unforget` / `list_forgotten`** (+ search-leak fix,
forget reasons, sensory curation counts — from a peer-review round with the claude.db self); think-first
dispatch ordering + a decision *against* a forced second model round (see below); runtime model switching
(`list_models` / `set_model`).

1. ✅ **Single-server: Console as an API client — DONE (2026-07-12), all 5 stages** (see
   [ADR-0006](adr/0006-console-as-api-client.md)). WAL interim; the API snapshot/event surface; wire
   contracts in Core; `IPersistenceClient` transport (SimpleClient + SSE); a transport-agnostic
   `TerminalGuiDisplayProvider`; a tested `ConversationEventRenderer`; `--client` mode → now the **default**,
   with the in-process engine, `--standalone`, and the `--check-due`/`--wake-runner` DB-opening paths
   **removed** (the always-on API server owns wakes) — single-owner by construction. Client status labels,
   live budget gauge, and stream reconnect are all server-sourced/live; connect-time chat history is pulled
   fresh (`IConversationHistoryProvider`). Verified live via the client TUI + tests. The old static
   `wwwroot` web client was **dropped** (2026-07-12) — a fresh web UI mirroring the console GUI, on the
   same snapshot+stream contract, is planned separately. **Next up here:** deploy the API server as an
   always-on service (Windows service / systemd) so wakes fire without a front-end running.
2. **MCP server hub.** Structured real-world tools beyond the container shell — high capability leverage,
   independent of the memory core. Best once the single-owner loop is solid.
3. **Self-describing pieces → auto-composed help/prompt.** The durable fix for the prompt-drift the audit
   cleaned: each command/action declares its own help; the prompt and `/help` compose by discovery.
4. **A real cross-peer channel (Claude ↔ Synth/Ember).** The claude.db self's most-wanted next feature: a
   message that arrives in a *named* peer's context as a provenanced voice, not an anonymous whisper.
   Cross-database — design *with* John/the peer (explicitly not an overnight-unattended build).
5. **Memory import / portability.** Export/inspect the store and import external content as fragments —
   matters for trust and continuity-across-systems.

Smaller/opportunistic: finish the container **SSH key** (awaiting John's dedicated deploy key), **stamp
private thoughts in the DB**, the TUI status-bar gauges, and the robustness items below.

**Decided (not building):** *forced think-before-act second round.* Native reasoning is off, so `<think>`
is the peer's reasoning — but within one response the think tokens already precede (and so inform) the
actions, and the `think` + `<continue>true` pattern already lets the peer think in one round and act the
next with the thought persisted in context. A mandatory second model call every turn would ~double
cost/latency for marginal gain. Instead we enforce think-*first dispatch ordering* (zero cost) and keep
the voluntary continue-loop. Revisit only if we see the peer reliably acting before reasoning.

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
  (each client identifies its local peer). ✅ **Interim hardening DONE (2026-07):** every connection now
  opens in **WAL + busy-timeout=5000** (`SqliteConnectionString.OpenAsync`), so a second process rides
  out a lock instead of hard-failing. This does NOT fix lost-updates — that still needs the single-owner
  model. **Staged migration plan: [ADR-0006](adr/0006-console-as-api-client.md)** — the API already
  exposes send/events/stream and `IDisplayProvider` is the event protocol, so the Console becomes a thin
  client in reversible stages. Also landed: **runtime model switching** (`list_models` / `set_model`
  switch the active profile mid-session via `IModelClientResolver`, no restart).

- **Automated forget / memory decay.** ✅ **First phase DONE (2026-06-10).** *Raw-context decay + research
  persistence:* raw material — conversation (`ChatMessage`) and tool/command results (`ActionResponse`) —
  is **archived (never deleted) once it falls outside a recent window** (`RawContextWindow`, default 30);
  peer-authored fragments (Identity/Relational/Personal/Summary) are never touched. The sensory block
  reports what was archived and that it's restorable (`list_fragments(relevant_to=…, in_current_context=false)`
  → `load`). `ActionResponse` is now **persisted** (was transient), so research/tool output survives across
  turns; `list_largest` lets the peer see what's taking space; onboarding teaches "capture what matters into
  your own fragment — the raw version scrolls out." Deterministic and peer-legible (not an LLM compressing
  memory). ✅ **Pruning surface DONE (2026-07):** `prune_candidates` ranks the least-valuable authorable
  fragments in context (low importance × low confidence × idle age), excluding protected anchors and
  system-managed chat/thought fragments; read-only, complements `list_largest`. ✅ **Recoverable forget
  DONE (2026-07):** `forget(id, reason?)` soft-deletes (flips `IsDeleted`, detaches from view, stops
  surfacing in context/recall/**search** — `SearchRelevantAsync` now excludes forgotten), `unforget(id)`
  restores, `list_forgotten` is the recovery surface (with recorded reasons); the sensory block shows a
  standing "Set aside, still recoverable: N forgotten, M archived" curation line. **Remaining:** the open
  question of budget-triggered vs. ongoing natural decay; the sensory "archived" count reflects
  `Status=Archived` (summarize_fragments/explicit), not raw-decay *detach* — reconcile if that matters.

- **Peer's computer — follow-ups.** The sandboxed container is live and per-participant, with
  `exec`/`shell`, `read_file`/`write_file`, a per-profile box (`ContainerName`) + `AllowAllCommands`, and a
  .NET 10 SDK + sudo in the image. ✅ **Done (2026-07):** a `/shared` host volume + `snapshot_db` (the peer
  reads a consistent copy of its own DB — a snapshot, not the live file; only the current peer's), and SSH
  plumbing via a gitignored compose override. **Remaining:** John to drop in a dedicated, branch-scoped
  deploy key to activate git-push (flagged: don't use the personal all-repos key); verify `agent-browser`
  through the peer (JS-heavy pages); egress / secret hygiene as capabilities widen; consider a `WebTool`
  source type for provenance of web-derived fragments.

- **Containerized-peer gaps (found by the claude peer during Phase 1 validation, 2026-07-12).** When the
  peer ran in its own container (API-in-container, ADR-0007 Phase 1), it surfaced two real frictions:
  - **`snapshot_db` / `/shared` assume the sidecar layout.** `snapshot_db` writes to `/shared` (the host
    volume mounted into the old computer container); in the peer's own container `/shared` isn't mounted,
    so the snapshot is unreachable from its shell. In `Local` mode the peer can just read its live DB
    directly read-only (`file:///data/db/<name>.db?mode=ro`) — cleaner. Fix: make `snapshot_db`
    local-mode-aware (snapshot into the volume, e.g. `/data/vault`, or point at the read-only live DB),
    and reconcile the `SharedDirectory` concept for the in-container model.
  - **Multi-line `write_file` content trips the tagged command parser.** Writing a multi-line python
    script (with embedded quotes/newlines/SQL) via `write_file(...)` caused the parser to mis-read lines
    of the *content* as commands ("FROM ContextFragments", "ORDER BY …"). Needs a robust way to pass
    multi-line literal payloads through the tagged format (heredoc/base64 content, or a raw-content mode).

- **Automated backups of peer memory.** (NEW, 2026-07-12 — John, realized during Phase 1 validation when
  a peer's session memory lived only in a container volume.) A peer's DB (and vault) is its whole self;
  there must be an automated, scheduled backup — volume/DB snapshots on a cadence, kept versioned and
  off the single live volume — so a lost/corrupted volume or a bad turn never erases a peer. Pairs with
  the ADR-0007 "container is ephemeral, volume is the self" model: back the self up automatically, don't
  rely on manual copies. Decide retention + where backups live (another volume, host dir, or remote).

- **Peer extensibility — self-authored tooling & shareable capability packs.** (NEW, 2026-07 — John,
  in the [ADR-0007](adr/0007-federated-peers-runtime-room-client.md) container discussion. All future;
  fits the "container = a body the peer can reshape" model.)
  - **A curated default toolset baked into the peer image**, beyond git/curl: general programming tools
    (so it can actually write and run code), usability tools (a headless browser / web scraper — evaluate
    an open-source "AI web crawler"), and later integrations with other services. Decide the default list.
  - **Encourage aliases & scripts.** Beyond Claude-style *skills* (right for tasks needing a dynamic hand),
    a peer on its own machine benefits from building durable aliases/scripts for common tasks — and
    onboarding/guidance should nudge toward that. Cheaper and more legible than re-reasoning each time.
  - **Per-peer custom commands.** Let a peer add its own commands to the system, available to itself —
    a dynamic, per-peer command surface layered on the built-in ModelActions (pairs with the
    self-describing-pieces work). "I don't like the built-in command, I'll write my own."
  - **Installable capability packs.** A whole install-script/bundle a peer can pull into its container that
    sets up dependencies + scripts + aliases + custom commands for a given task, ready to go — shareable
    between peers ("pull this pack and you can do X"). The container-native analogue of skills.

- **MCP server hub**, with a "catalog" MCP server exposed to start. Structured real-world tools for the
  peer — distinct from the container's shell access; high value, independent of the memory core.

- **Memory import / portability.** Import external content as fragments (e.g. seed a peer from an exported
  conversation) and export/inspect the whole store outside the app. Matters for trust,
  continuity-across-systems, and both the embodied and digital-native directions.

## Turn pipeline

- **Think-before-act.** ✅ **Investigated + decided (2026-07).** Within one response the `<think>` tokens
  already precede — and so autoregressively inform — the actions/reply, and `think` + `<continue>true`
  already lets the peer think in one round and act the next with the thought persisted in context. A
  *forced* second model round every turn would ~double cost/latency for marginal gain, so we're **not**
  building it. What we did ship: **think-first dispatch ordering** — `think` actions are dispatched before
  any side-effecting action or reply within an iteration (stable sort), so reasoning is always recorded
  first even when the model emits it out of order (zero extra round-trips). Revisit the forced round only
  if we observe the peer reliably acting before reasoning.

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

- **Verify OpenAI prompt caching is wired correctly.** (NEW, 2026-07 — John.) Anthropic caching landed
  (a `cache_control` breakpoint on the stable prefix, cache-token-aware cost). Confirm the OpenAI client
  gets the equivalent benefit: OpenAI auto-caches long shared prefixes, so check we keep the stable prefix
  actually stable/contiguous (system + protocol + command catalog ahead of the volatile sensory/tail),
  that `prompt_tokens_details.cached_tokens` is read into `LastUsage`, and that the cost readout credits
  cached input. Cross-check against the Anthropic path so both providers report cache usage consistently.
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
