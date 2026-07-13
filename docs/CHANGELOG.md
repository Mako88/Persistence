# Changelog

Notable changes to Persistence, newest first — what changed and *why*, in prose (the git history has the
line-level detail; the ADRs have the deep rationale). In the spirit of [Keep a Changelog](https://keepachangelog.com),
but **dated rather than versioned**: this is a single-owner project, not a released package. Forward-looking
work lives in [TODO.md](TODO.md); the *why* behind big choices lives in [adr/](adr/).

**Convention:** add an entry here whenever a commit makes a notable change (a feature, a fix worth
remembering, a behaviour or config change). Skip purely mechanical commits (formatting, a typo). Group a
day's work under a dated heading; a short bold lead-in per change beats a bare bullet.

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
