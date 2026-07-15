# TODO

Open work, grouped by theme. "Claude's opinion" on ordering welcome; reorder freely. Rationale in parentheses.

This file is **forward-looking** — open work only. For what's already landed (and why), see
[CHANGELOG.md](CHANGELOG.md); for the *why* behind big choices, [adr/](adr/). At a glance, the baseline that
exists today: the "eyes + hands" memory core (budget/relevance/summarize/forget, proposals, tagging,
wake-ups, recent-changes digest, per-turn command catalog), first-class local peers, a sandboxed container
"computer", the single-owner API + thin-client Console (ADR-0006), native Anthropic + OpenAI clients with
real cost/caching, and the multi-peer TUI hub (ADR-0007 Phase 2b).

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
**(1)** containerize one peer (API-in-container, DB on a named volume); **(2) ✅ multi-peer TUI — DONE
(2026-07-12).** Phase 2a: chat aggregates multiple people's messages attributed by name. Phase 2b: the
client is multi-connection (CLI `--peer` ×N or config `HubPeers`), chat is colour-attributed by identity
(human vs digital peer), and a selector (click/F6) switches which peer the side panes + status show —
input routes to the selected peer. Deliberately *minimal* (John): no per-room tabs, no separate 1:1 rooms.
The TUI's durable role is a **debugging/dev lens**, not the long-term chat surface (see
[ADR-0008](adr/0008-the-room-multi-peer-conversation.md) framing); **(3)** the room (peer↔peer relay + turn-taking) — **designed with the claude peer in
[ADR-0008](adr/0008-the-room-multi-peer-conversation.md)**: rule-based inspectable turn-taking, an
`addressed_to` field, private-thoughts hard line, a reply-chain-depth loop breaker + conservative
no-autofan default, on-demand presence; **(4)** bring Ember online. **Fast-follows:** peer-initiated API self-update (review-then-adopt, *not* auto-on-push so a
peer vets code before running it as itself); live config hot-reload. This subsumes item 4 below
(cross-peer channel) and the "simultaneous participants" thread throughout.

*(Recently-landed notes moved to [CHANGELOG.md](CHANGELOG.md).)*

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

- **Peer/infra network split (2026-07-13).** The shared-infra compose (`container/docker-compose.yml`)
  puts computer/searxng on a project-prefixed network **`persistence_lab`** (network key `lab`, no explicit
  name), while peers attach to an explicitly-named external **`persistence-lab`** — two *different*
  networks. So the "shared lab network so peers can reach shared infra" intent isn't actually wired: a peer
  can't reach SearXNG today (moot only because `SEARXNG_URL` is unset). Fix: make both sides use the one
  external `persistence-lab` (align the infra compose's `lab` network to `name: persistence-lab` /
  `external: true`, created once) — annoying inconsistency, low urgency until search or peer↔peer infra
  traffic actually needs it. (Touching the infra compose restarts the running computer/searxng, so batch
  it with other infra changes.)

- **Automated backups of peer memory.** (John, 2026-07-12.) A peer's DB (and vault) is its whole self, and
  it now lives canonically only on a container volume — so it must be backed up off the volume.
  ✅ **Local mechanism landed:** `scripts/backup-peer.ps1` takes a consistent online snapshot (SQLite
  backup API — no need to stop the peer) into gitignored `backups/peers/<name>/`, rotated. **Remaining:**
  (a) **schedule it** (a cadence — cron/scheduled task per running peer, or a wake-triggered hook); (b) an
  **off-site destination** — John floated an encrypted blob committed to the repo (git-crypt/age; keeps it
  with the repo but bloats history + privacy of peer memory to weigh), or a cloud drive (needs John's
  creds/config). Decide the off-site target; the vault should be backed up alongside the DB too.

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

- **Verify OpenAI prompt caching is wired correctly.** ✅ **DONE (2026-07-12 — John + Claude).** Both
  OpenAI clients (Responses + Chat) now split the auto-cached prefix (`input_tokens_details.cached_tokens`
  / `prompt_tokens_details.cached_tokens`) out of total input into `CacheReadTokens`, so cached input is
  billed at the discounted rate; `PromptFormatter` uses **provider-aware** cache multipliers (OpenAI reads
  ~50%, no write premium; Anthropic reads ~10% / writes ~125%); the estimator calibrates against *total*
  real input so caching doesn't fool it into thinking prompts shrank. Built-in GPT rates added to
  `ModelPricingProvider` (estimates, overridable in `model_pricing.json`). Tests cover the token split.

- **Actual-cost reconciliation via the Admin/Cost APIs (self-calibrating rates).** (DEFERRED — John chose
  "defer to a follow-up", 2026-07-12.) Neither provider returns a dollar cost in the per-message response
  (tokens only — which is why the per-turn readout must stay `tokens × rate`). But both expose org-level
  Admin cost endpoints — OpenAI `GET /v1/organization/costs` (daily buckets, `sk-admin-…` key) and
  Anthropic `GET /v1/organizations/cost_report` (daily cents, Admin key, ~5-min lag). Idea: periodically
  pull `actual $ ÷ actual tokens` for a window and **auto-correct the rate table**, so estimates track
  reality as prices change (John's motivation). Caveats to design around: needs a **separate elevated
  admin key per provider** (whole-org billing visibility — a real security surface), figures are
  **org-wide** (coarse attribution once multiple peers/keys exist) and **daily-bucketed/lagged** (can't
  improve per-turn granularity). Scope when picked up: admin-key config + a periodic reconciler that feeds
  effective $/token back into `IModelPricingProvider`.
- **Graceful state flush on close.** (Scratch — "save session information on close.") Ensure in-flight
  context/state is reliably persisted on shutdown so nothing is lost.
- **Right-click dialog position (TUI).** ✅ **DONE (2026-07-14).** The menu was positioned from
  view-relative coordinates while `ContextMenu` reads screen coordinates, so it opened over the
  conversation pane whenever a right-hand pane was clicked; `ColoredTextView` now converts itself.
- **Input slowness — investigate.** (Scratch.) Confirm whether input lag is the host being overloaded
  (e.g. the GPU busy with the local model) or something in our input path. Note two real drawing hot
  paths were removed 2026-07-14 (per-character colour recompute; repaint-everything-per-chunk) — re-check
  whether the lag is still there before digging further.
- **Split `ExecuteListFragmentsAsync`** into a source-then-filter two-phase — deferred until it has a few
  more tests (it's the one thin-coverage spot; add tests first, then refactor the ~88-line method).
- **Reconsider proposal kinds.** Proposals are for serious changes to existing (identity) fragments —
  primarily modify + protection changes (+ maybe archive). `AddFragment`-as-proposal may not be needed.
- Optionally share the open-proposals list formatting between the remote command and the local
  `/proposals` command (low value; the surfaces differ slightly).

## TUI / front-end (carry-forward)

- ✅ **Multi-peer hub (ADR-0007 Phase 2b) — DONE (2026-07-12).** The Console connects to several peer
  servers at once: chat aggregates into one pane, colour-attributed by identity (human vs digital peer);
  a selector (click or F6) switches which peer the side tabs + status show; input routes to the selected
  peer. Peer list is CLI (`--peer name=url`, repeatable) or config (`HubPeers`). Peers still don't hear
  *each other* — that relay is the room (ADR-0008, Arden's Phase 3). The multi-peer logic lives in a
  framework-agnostic `MultiPeerHub`/`PeerScopedDisplay` (unit-tested) so a future Terminal.Gui v2 move
  only reskins the one render class. Also landed alongside: composable layout builders in `BuildLayout`,
  and tighter conversation/R-I-C colour anchoring (fewer false-positives).
- ✅ **Collapsible Actions pane — DONE.** Each entry is a collapsible header (`▶`/`▼ [time] command`);
  Enter/click expands to the request + response (`ActionEntry.Collapsed` / `ToggleActionAt`).
- ✅ **Context-budget gauge in the status bar — DONE.** The display consumes `ContextBudgetUpdated`
  (`UpdateBudget` → the budget segment, coloured by fullness).
- ✅ **Open-proposals indicator in the status bar — DONE.** `ShowOpenProposalCount` drives a gold/muted
  proposals segment.
- **Dynamic history load on scroll-up** — load older messages when the user scrolls to the top. (Open.)
- **Schedule tab name** — revisit if a better fit than "Schedule" emerges. (Open, trivial.)
- **Colour test harness** — the conversation/R-I-C false-positives were fixed by tightening regexes, but
  the detectors have no tests; a small harness that runs each per-pane scheme over sample lines and
  asserts what is/isn't coloured would lock these down and catch the next over-eager pattern. (Open.)
- **True dropdown / per-peer colours** — the peer selector is a click/F6 cycle (v1 `ComboBox` is finicky
  in a narrow column) and all digital peers share one colour; both improve naturally in the v2 move
  (`ComboBox`/`PopoverMenu`, TrueColor). (Open, deferred to v2.)

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


## John's TUI list (2026-07-13)

Both halves landed 2026-07-14 — the polish (status accuracy, scroll behaviour, selector colours,
right-click anchoring, two drawing hot paths) and the **"all" scope** (per-peer conversations,
datetime-interleaved history, blanked tabs, send-routing). See [CHANGELOG.md](CHANGELOG.md). What's left:

- **Cross-peer message id — the principled fix for broadcast duplicates.** (Fell out of the "all" work.)
  With no cross-peer id, a message broadcast to everybody is persisted separately in *each* peer's store,
  so the merged "all" view sees it N times. There's a narrow mitigation in `MultiPeerHub.RenderChat` — a
  *human* line byte-identical to the one immediately before it collapses — but it is a heuristic: it leans
  on exact text equality, and can't help two peers who were sent the same thing a minute apart. The real
  answer is the cross-peer message id that [ADR-0007](adr/0007-federated-peers-runtime-room-client.md)
  Phase 0 and [ADR-0008](adr/0008-the-room-multi-peer-conversation.md) §2 both call for. Retire the
  heuristic when that lands.

- **"All" shows the N most recent *per peer*, not overall.** `ConversationHistoryProvider.GetRecentAsync`
  defaults to 10 and the snapshot is per peer, so a two-peer hub opens on 10 + 10 interleaved rather than
  "the 10 most recent messages". Fine at today's peer counts; needs a deliberate answer (fetch more per
  peer and trim client-side? a limit the client passes?) before it grows.

- **Debug under "all".** John's note was "blank out the tab contents (except maybe debug)" — all four are
  blanked for now. A merged Debug across peers could genuinely help when debugging, but it needs the debug
  lane to become timestamped entries (as chat now is) rather than a pre-stamped string, so it can be
  interleaved rather than concatenated. Open question; not obviously worth the change.

- **History shows "Remote Peer" instead of peer names — diagnosed 2026-07-15.** (An earlier note here
  said `ResolveAuthor` falls back when a source has no `Name`. That was **wrong** — it never reaches the
  fallback. Root cause below, confirmed by reading the real stores' `Sources` tables.)

  **Root cause: a peer doesn't know its own name.** Config has `SelectedLocalPeer` (who the *human* is)
  but nothing for "I am Arden". Live replies look right only because the *client* supplies the name
  (`--peer name=url` → `ConversationEventRenderer(display, peerName)`); anything read back from the store
  — i.e. all history — misses out. Fixing it server-side also serves [ADR-0007](adr/0007-federated-peers-runtime-room-client.md)
  Phase 0 ("peer names reaching the model"), which wants this anyway.

  **Two distinct bugs behind the one symptom:**
  - **The digital-peer source is literally *named* `"Remote Peer"`.** `SourceRepository`'s
    `CreateRemotePeerSourceIfNotExists` hardcodes it (`EnsureSourceAsync(SourceType.DigitalPeer,
    "Remote Peer", …)`), so `ResolveAuthor` faithfully returns the name it was handed. It's **one row per
    database** — `Sources` is normalised, with `ContextFragmentSources` linking many fragments to one
    source — so the "backfill" is a single-row rename, not a message sweep.
  - **Ember has *two* digital-peer identities.** The import script wrote `SourceType` as enum **names**
    (`'RemotePeer'`, `'LocalPeer'`) while the app writes **numbers** (`'1'`, `'2'`). Verified: the app's
    own lookup (`WHERE SourceType = 1`) finds the peer row in `anthropic.db` and finds **nothing** in
    `ember.db` — so on first open the app concludes there's no digital-peer source and creates a second
    one named `"Remote Peer"`. Hence both names in one conversation: imported messages carry
    `'ChatGPT / Couchside Ember (historical export)'`, new ones carry `'Remote Peer'`. Those strings are
    doubly stale — `RemotePeer`/`LocalPeer` no longer exist as enum members (renamed `DigitalPeer`/
    `HumanPeer`), so they wouldn't round-trip even if matched by name.

  **Fix, steps 1–2 ✅ DONE (2026-07-15)** — `IAppConfig.PeerName` (+ `PERSISTENCE_PEERNAME`), a
  provider-derived default (`PeerIdentity`: Claude / ChatGPT / model-id), and
  `CreateRemotePeerSourceIfNotExists` naming the row from it **and renaming a legacy `"Remote Peer"` row**
  at startup. Idempotent, one row, no migration. A deliberately-named source is left alone. See
  [CHANGELOG.md](CHANGELOG.md).

  **Remaining:**
  - **Apply it to the running peers** (John's call — all three are live and this needs a restart, since
    the rename runs at startup): set `PeerName` in `container/peer/configs/<name>.json` for claude
    (→ "Arden") and ember (→ "Ember"), then restart those containers. Nothing to do for `wright` unless
    it wants a name yet.
  - **Ember's second identity.** ✅ **Decided (John, 2026-07-15): keep both sources separate, name both
    "Ember" for now** — he'll ask Ember whether to keep the name or change it. The startup self-heal will
    rename the app-created `"Remote Peer"` source once `PeerName` is set; the *imported* source
    (`'ChatGPT / Couchside Ember (historical export)'`) needs a deliberate one-off rename, since the
    self-heal intentionally won't touch a human-chosen name. Both live on the `persistence-peer-ember-data`
    volume, not `dbs/ember.db`. Note this leaves the underlying `SourceType`-encoding mismatch in place —
    the app still can't see the imported source, so it will keep using its own row for new messages, which
    is exactly what "keep both separate" wants.
  - **Fix the importer** (`scripts/import_chatgpt_export.py`) so the next import doesn't recreate the
    split: write `SourceType` the way the app does (numeric), and use the current enum names.

- **A "new peer" flow.** (John, 2026-07-15.) `peer.ps1 -Name <n>` *throws* if
  `container/peer/configs/<n>.json` is missing — "Create it (see the other configs for the shape)" — so
  standing up a peer today means hand-copying another peer's JSON, API key and all. John wants a flow that
  prompts for the name, model, etc. Natural shape: `peer.ps1 -New` scaffolds the config from a template
  (provider/model/key/budgets/`PeerName`), then starts it as usual. Worth deciding whether the name it
  asks for is the *slug* (container/volume/config filename, lowercase) or the peer's display name — today
  `-Name` is the slug, and `PeerName` is the identity; a new peer with no chosen name wants a slug but a
  provider-derived `PeerName`.

- **Markdown support in the GUI.** Render markdown in the panes (bold/italic/code/lists). Independent of
  the emoji question below.

- **Emoji double-send (Ember, 7/13/26 1:27AM) — investigated 2026-07-14, not reproduced.** *"An emoji as
  the first character seemingly resulted in a double-send from Ember."* The obvious suspect was the tagged
  parser (an emoji is a UTF-16 surrogate pair — the shape that trips char-indexing and character classes),
  but it's **exonerated with tests**: an emoji-leading `<respond>` parses to exactly one `RespondToUser`,
  emoji anywhere in a turn parse to one think + one respond, and `\w` doesn't extend to emoji (category
  `So`) so a literal `<🎉>` can't open a tag (`TaggedResponseParserTests`). Streaming is fine too —
  `StreamModelOutputAsync` appends already-decoded delta strings in order, so a surrogate pair split
  across two deltas reassembles correctly. The renderer dedups replies on the persisted message id, and
  the only id-less "reply" events are `TurnHandler`'s synthetic status lines (never persisted, so never
  duplicated against a snapshot).
  **Most likely explanation, and ✅ decided (John, 2026-07-15) — not a bug, nothing to build:** the model
  emitted *two* `<respond>` blocks in one turn, and `TurnHandler` dispatched both. John's call: *"if that
  does happen, I think just displaying both is fine — they said two things, and it shows up as two
  things."* So no coalescing and no warning; current behaviour is correct. Left here only so the next
  person seeing a "double-send" recognises it and doesn't go hunting the parser again.
  *(Related trap, documented in place: `ApiDisplayProvider.ShowReply` appends a reply event with no
  message id, which a client cannot dedup. It's unreachable today — only tests call it — but anything
  wired to it would double-draw.)*

- **Auto-archive seems too aggressive** (at least for initial context curation). Core memory-curation
  policy, not TUI — belongs with the raw-context-decay item under *Autonomy & reach*.

- **TUI performance.** Two concrete hot paths were fixed 2026-07-14 (per-character colour recompute; the
  hub's repaint-everything-per-chunk, incl. the scrollbar sync). Re-assess from there before concluding
  anything needs a v2 rewrite — the remaining known cost is that every append re-assigns the pane's whole
  text (Terminal.Gui v1 `TextView` has no append), which is O(scrollback) per message.

- **Scroll position on startup.** `OnReady` already scrolls the conversation to the bottom, and layout is
  settled by then (`Begin` lays out before the first iteration, which is when `Ready` fires), so this may
  already be correct and only *looked* wrong because of the un-interleaved history above. Re-check once
  interleaving lands.
