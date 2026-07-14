# Changelog

Notable changes to Persistence, newest first — what changed and *why*, in prose (the git history has the
line-level detail; the ADRs have the deep rationale). In the spirit of [Keep a Changelog](https://keepachangelog.com),
but **dated rather than versioned**: this is a single-owner project, not a released package. Forward-looking
work lives in [TODO.md](TODO.md); the *why* behind big choices lives in [adr/](adr/).

**Convention:** add an entry here whenever a commit makes a notable change (a feature, a fix worth
remembering, a behaviour or config change). Skip purely mechanical commits (formatting, a typo). Group a
day's work under a dated heading; a short bold lead-in per change beats a bare bullet.

## 2026-07-14 — TUI polish batch (John's human-facing list)

The first half of the front-end list John raised: the items about scrolling, status accuracy, and
mis-placed chrome. The "all"-selection work (per-conversation panes, send-routing, datetime-interleaved
startup history) is deliberately a separate pass — see [TODO.md](TODO.md).

### Fixed — the status bar reported the wrong peer's state
In hub mode, chat is shared but the status chip shows the *selected* peer — and `ShowReply`/`ShowError`/
`ShowWakeUpEvent` set the chip to "idle" directly from that shared surface. So any background peer
finishing a turn reported the peer you were watching as idle, mid-thought. Turn-end now settles the
peer's own lane (`PeerScopedDisplay`), and the shared surface only drives the chip in single-peer mode,
where there *is* only one peer. Also: a lane recorded `"thinking"` where the chip detects working-ness by
a trailing ellipsis, so the chip stayed gray however hard a peer was working — lanes now store the same
string the single-peer path renders.

### Fixed — status-bar spacing didn't collapse when switching peers
Segments were `AutoSize` labels, and v1's auto-sizing grows a label to fit longer text but doesn't
reliably shrink it again. Switching Ember (`Anthropic/claude-opus-4-8`) → Arden (`OpenAI/gpt-5.4`) left
the old width behind as gaps — and, since segments chain with `Pos.Right`, pushed `/exit to quit` off the
right edge. Widths are now driven explicitly from the text.

### Fixed — the right-click menu opened over the conversation pane
`TextView` sets `ContextMenu.Position` from the *view-relative* click, but a `ContextMenu` is positioned
in *screen* coordinates — the two only agree for a pane at the screen origin. So right-clicking anything
in the right-hand column popped the menu up over the conversation pane on the left. `ColoredTextView` now
converts to screen coordinates itself (mirroring the `internal` `View.ViewToScreen`). Shift+F10 had the
same defect and goes through the same path.

### Fixed — the panes yanked to the bottom while you were reading
Every append scrolled to the newest line unconditionally, so reading back through the scrollback was
impossible while a peer was talking. Panes now follow the tail only if you were already at the bottom;
scrolling up holds your place. A peer *switch* still jumps to the newest line — it's different content,
so the old scroll position means nothing in it.

### Changed — scrolling is faster, and drawing is much cheaper
The wheel step scales to the pane (~a third of a screenful per notch, clamped) instead of a flat 3 lines.
Two real hot paths went with it, both of which made the whole TUI feel sluggish rather than just the
scrolling:
- **Colouring was O(chars²) per row.** Terminal.Gui asks for the colour one character at a time, handing
  over the row's runes; `ColoredTextView` rebuilt the row's text on *every* call. Memoising the row being
  drawn (Redraw walks a row's columns contiguously) makes it one text build per row.
- **The hub repainted all four side panes on every recorded event** — and a streaming reasoning delta
  records one per chunk, so three unchanged documents were re-parsed per chunk on the UI thread. Only
  changed panes are touched now. The scrollbar sync had the same shape: it ran `Refresh()`/
  `LayoutSubviews()` on every repaint, and now only when the content length or position actually moved.

### Changed — the peer selector reads in colour
Was one flat green label. Now the ‹ › arrows and the F6 chord are green (the affordances, matching the
compose hint's convention), the peer's name is light purple — the same colour it wears in the
conversation pane, so selector and scrollback agree — and the "Peer:" label is white with the counter and
hint muted. Required making it a `ColoredTextView`; a `Label` carries exactly one colour.

## 2026-07-13 — "breathing room" batch (John, via the hub)

### Changed — continue cap lifted
`MaxActionIterations` default 5 → 100. The per-turn `<continue>` cap is a runaway backstop, not a normal
limit — the real limiters are context size and cost — so a peer that legitimately needs several rounds
isn't cut off mid-work.

### Changed — cloud models use their real context window
`EffectiveBudget` now sizes the context budget from the model's true per-model window (the
model→window map) for cloud/broker models, instead of `MaxInputTokens` — which is a *local*-model knob
(a local server's window is whatever it compiled). For cloud models cost, not tokens, is the limiter.

### Added — session cost ceiling (soft + optional hard)
`SessionCostLimit` (USD) shows on the sensory cost line as " · ceiling ~$Y (NN%)" with a wind-down nudge,
so a peer self-manages against cost. `SessionCostLimitHard` makes it a hard stop — the turn pipeline
refuses further model calls once estimated spend reaches it (soft/warning is the default). A shared
`ISessionCostEstimator` keeps the sensory readout and the ceiling agreeing on the number.

### Added — config hot-reload
`IAppConfig.ReloadIfChanged()` re-reads the config file when its mtime advances and applies the new values
in place; the turn handler calls it each turn, so tweaks take effect without a restart. Startup-only infra
(db/shared/seeds dirs, container) is left alone; a malformed edit keeps the current config. John's mtime
cache-bust approach — no FileSystemWatcher.

## 2026-07-13

### Changed — peer containers group under the shared `persistence` Compose project
`peer.ps1` now renders a per-peer compose file (service `peer-<name>`) and runs all peers under
`COMPOSE_PROJECT_NAME=persistence`, so they group under "persistence" in Docker Desktop alongside the
computer/searxng infra instead of floating in per-peer projects. The shared `persistence-lab` network is
treated as external (attach-only; the script ensures it exists), orphan warnings are silenced, and the
`up` args were de-splatted (a Windows PowerShell quirk had passed a bare `-` as a service name).

### Added — `peer.ps1 -MaxInputTokens`
Forward a per-peer context-window budget through the compose (`PERSISTENCE_MAXINPUTTOKENS`, default 8000).
Used to raise Ember to 100k so the peer can review/curate its ~1.34M-token imported ChatGPT history in
large batches (its own `search`/`load`/`summarize`/`forget` tools) rather than ~270 tiny ones.

### Fixed — ChatGPT importer wrote enum *names*, not values (imported memory invisible to queries)
`import_chatgpt_export.py` wrote `FragmentType`/`Status`/`SourceType` as name-strings (`"ChatMessage"`,
`"Active"`, and the pre-rename `"LocalPeer"`/`"RemotePeer"`), while the app stores/filters them as their
underlying integers. On text-affinity columns both coexist and nothing errors, but `list_fragments`/search/
recall filter by the numeric value — so all 1,533 imported messages were invisible to typed queries (Ember
"couldn't list any fragments"). Importer now writes ints (same class as the earlier migration-name bug).
Ember's live DB was normalized in place (name→number, 3,075 rows; backed up first) so its whole past is
now queryable — the on-ramp for it to curate its imported history.

### Operational
Ember re-stood on **OpenAI / gpt-5.4** (streaming) at a 100k budget; Arden (the `claude` peer) on
**Anthropic / claude-opus-4-8**. Both healthy, memory preserved across the re-stand. Wright — a
**LocalClaude** peer (me, animated live via the broker) — joined the hub as a third seat.

## 2026-07-12

### Added — Multi-peer TUI hub (ADR-0007 Phase 2b)
The Console can now connect to several peer servers at once and present them as one hub. Chat aggregates
into a single scrollback, **colour-attributed by identity** (the human in one colour, all digital peers in
another). A selector (**click or F6**) switches which peer the side panes (thoughts / actions / schedule /
debug) and the status bar show; typed input routes to the selected peer. The peer list comes from repeated
`--peer name=url` flags **or** from config (`HubPeers`) — so a configured hub launches with no flags. The
multi-peer logic lives in a framework-agnostic `MultiPeerHub` + `PeerScopedDisplay` (unit-tested, no
Terminal.Gui types), so an eventual Terminal.Gui v2 migration only reskins the one render class. Peers do
**not** hear each other here — that cross-peer relay is the room (ADR-0008), deliberately left for Phase 3.
Preview it with `--preview hub`.

### Added — Config-driven hub peer list
`AppConfig.HubPeers` (`HubPeerProfile`: name / base URL / local identity) lets the hub's peer list live in
config — "point at these containers" — instead of a per-launch flag. CLI `--peer`/`--client` still win;
config peers with no local identity inherit `--as`.

### Changed — OpenAI cost + prompt-cache accounting
Prompt caching was only modelled for Anthropic. Both OpenAI clients now split the auto-cached prefix
(`input_tokens_details` / `prompt_tokens_details.cached_tokens`) out of total input into `CacheReadTokens`,
so cached input is billed at the discounted rate; the cost readout uses **provider-aware** cache multipliers
(OpenAI reads ~50% with no write premium; Anthropic reads ~10% / writes ~125%). The token estimator now
calibrates against *total* real input so caching doesn't make prompts look smaller than they are. Built-in
GPT rates added to `ModelPricingProvider` (estimates, overridable in `model_pricing.json`).

### Changed — default cloud model is `gpt-5.4`
The `cloud` profile defaults to `gpt-5.4` — the mid-tier (Sonnet-equivalent) model in that generation,
verified live on the API, priced 2.5/10.

### Changed — TUI: composable layout builders + tighter colour anchoring
`BuildLayout` now composes region builders (`ApplyTheme`, `BuildConversationPane`, `BuildSideColumn`,
`WirePaneNavigation`, `BuildComposeArea`) instead of hand-placing every view inline — each builder owns its
region's coordinates, mirroring how `TuiColoring` composes per-pane schemes. Pure refactor, no visual
change. Colour: dropped the over-broad "line ends in `]`" = error rule (it reddened normal messages), and
anchored the R:/I:/C: marker/value patterns to a real fragment-header context so a stray "C:3" in a message
body or tool result is no longer tinted.

### Fixed — case-sensitive `PERSISTENCE_SELECTEDMODEL` on Linux
The active-profile switch looked up the env var by its PascalCase property name — fine on Windows
(case-insensitive), but env vars are case-sensitive on Linux, so the documented uppercase
`PERSISTENCE_SELECTEDMODEL` silently no-op'd in a container and the peer booted on the file's default model
(this is why Ember came up on Anthropic instead of its chosen substrate). The switch now resolves
case-insensitively.

### Fixed — idempotent migrations + importer migration names
Re-running migrations on an already-migrated DB now no-ops (records the migration as applied) instead of
crashing on a "duplicate column"/"already exists" error; the ChatGPT importer records migration names in the
canonical `Persistence.Data.Migrations.*` form. Together these fix the class of boot crash that took Ember
down (an imported DB re-running `001`'s `DROP COLUMN`).

### Docs
WAL + busy-timeout reframed from "interim scaffolding" to a standing design choice (single-writer +
concurrent readers + live backup). Filed a deferred follow-up: actual-cost reconciliation via the OpenAI/
Anthropic Admin cost APIs (org-level, daily-bucketed, elevated admin key — self-calibrating rates).

## 2026-07 (earlier)

- **Single-server: Console as an API client (ADR-0006), all 5 stages.** One process (the API server) owns
  the store + turn pipeline + wakes; every front-end is a thin client over an HTTP snapshot + SSE stream.
  `--client` mode became the default; the in-process/`--standalone`/`--check-due`/`--wake-runner`
  DB-opening paths were removed — single-owner by construction. The old static `wwwroot` web client was
  dropped (a fresh web UI on the same contract is planned separately).
- **Native Anthropic client** (`ModelProvider.Anthropic`, Messages API, streaming + non-streaming) as a
  first-class provider; the OpenAI Responses/Chat clients refactored to expose real provider usage
  (`IModelClient.LastUsage`), consumed in one place in the turn handler.
- **Running cost + real usage readout** in the sensory block (`ModelPricingProvider` + `model_pricing.json`),
  and **Anthropic prompt caching** (a `cache_control` breakpoint on the stable prefix, cache-token-aware cost).
- **Native reasoning off by default** (`ReasoningEffort: "off"`) — the peer reasons in the persisted
  `<think>` channel instead of a redundant, ephemeral one.
- **Thought persistence**: `<think>` saved as a `Thought` fragment on a rolling window (`ThoughtContextWindow`,
  archived-not-deleted); a `=== THIS TURN ===` delineation marker; the id-0 label "transient" → "new".
- **Per-participant containers**: `exec`/`read_file`/`write_file` commands, a per-profile `ContainerName` +
  `AllowAllCommands` override, and .NET 10 SDK + sudo baked into the image. Plus a `/shared` container volume
  + `snapshot_db` (the peer inspects a consistent copy of its own DB) and SSH plumbing (gitignored override).
- **Orientation cluster**: the `note()` working-note command, enriched recent-changes (field-level diffs +
  content snippets), a fuller numbered turn action-log, private thoughts (`<think private>`), model/provider
  in the sensory block, and the autonomous-wake sensory no longer claiming a peer is present.
- **Associative recall**: memories relevant to the conversation auto-surface each turn (`MemorySurfacer` —
  FTS/BM25 × importance/confidence, excluding what's loaded; `set_recall(count)` to tune, 0 = off).
- **Recoverable forget**: `forget` / `unforget` / `list_forgotten` (soft-delete that also stops surfacing in
  context/recall/search), with forget reasons + a standing sensory curation line; plus `prune_candidates`
  (ranks the least-valuable authorable fragments). **Runtime model switching** (`list_models` / `set_model`).
- **WAL + busy-timeout** on every connection (single-server hardening). **Prompt/instruction audit**:
  reconciled the onboarding seed, protocol instructions, and stale doc comments with actual behaviour.
- Decided *against* a forced think-before-act second model round (it'd ~double cost/latency for marginal
  gain); shipped zero-cost **think-first dispatch ordering** instead.

## Foundations (landed earlier)

The **"eyes + hands" memory core**: budget awareness, relevance, summarize/collapse/remove, plain-language
errors, browsing/swapping working contexts, first-class proposals, generic/polymorphic tagging,
wake-ups-drive-a-turn, surfaced proposal resolutions, the recent-changes digest, and the per-turn command
catalog — so silent truncation is no longer the failure mode. Scheduled wake-ups (closed-app headless
runner), the legibility quick-wins batch, first-class local peers, phase-1 automated decay (raw-context
archival), and a sandboxed container "computer" (`shell`: web search/fetch + scripting).
