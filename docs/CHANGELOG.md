# Changelog

Notable changes to Persistence, newest first — what changed and *why*, in prose (the git history has the
line-level detail; the ADRs have the deep rationale). In the spirit of [Keep a Changelog](https://keepachangelog.com),
but **dated rather than versioned**: this is a single-owner project, not a released package. Forward-looking
work lives in [TODO.md](TODO.md); the *why* behind big choices lives in [adr/](adr/).

**Convention:** add an entry here whenever a commit makes a notable change (a feature, a fix worth
remembering, a behaviour or config change). Skip purely mechanical commits (formatting, a typo). Group a
day's work under a dated heading; a short bold lead-in per change beats a bare bullet.

## 2026-07-19 — prompt audit: what a peer is told vs. what's true

A pass over everything a peer actually reads — the onboarding seed, the protocol instructions, the
first-wake note — checking each claim against behaviour. Several had drifted.

### Fixed — the terminology section taught names the system had dropped
It told every peer *"you are the **remote peer**… the person at the keyboard is the **local peer**"*,
renamed by [ADR-0007](adr/0007-federated-peers-runtime-room-client.md) to **digital peer / human peer**.
It also assumed a single human ("the person at the keyboard") when conversations can now have several,
each named. Rewritten: peers have names rather than roles, the sensory block says who you're speaking
with, a human's messages are prefixed with their name — and the old terms are noted as still-valid
aliases, since `ParseSourceType` accepts them.

### Fixed — the protocol instructions demonstrated the mistake they warned against
The command-syntax section said triple-quoted text needs no escaping, then illustrated it with
`content="""line one\nline two"""` — a literal `\n`. `ReadTripleQuoted` takes its content **literally**,
so a peer copying that pattern wrote two characters into its memory instead of a newline. Now it says
triple quotes are literal (press enter for a real break) and that only single-quoted strings process
escapes, with a test pinning both halves so the prompt can't quietly become a lie.

### Added — a peer can see its own name
`PeerName` shipped without ever telling the peer: the sensory block reported who it was *speaking to*,
never who it *was*. It now opens with `You are: <name>` — the name its messages are attributed to in its
own store and in every client, and the one thing about itself it couldn't otherwise see from inside.
(Caught while writing onboarding text that pointed at the sensory block for it — the claim was false
until this landed.)

### Changed — memory guidance covers the commands that exist
The "managing your memory without fear" list predated `forget`/`unforget`/`list_forgotten`, so a whole
recoverable-forget surface went untaught while the sensory block referenced `list_forgotten` in its
"set aside, still recoverable" line. Added, including the distinction that actually matters: `remove`
is for clutter (the fragment stays active elsewhere), `forget` is for content that's wrong or outdated
(stops surfacing anywhere, still recoverable). Curation guidance now names `list_largest` and
`prune_candidates` too, and the archive-recovery search is spelled as the command it is.

### Changed — the first-wake note stopped asking for a decision
It said *"Consider who you'd like to be, and record it yourself"* — the same homework the opening line
was just rewritten to remove. Now: write things down as you notice them, identity accumulates, and you
already have a name you can keep or change.

### Fixed — `peer.ps1 -Down` took the whole lab offline
It ran `docker compose down`, which is scoped to the **project** — and every peer shares the one
`persistence` project. Asking for one peer stopped all four plus the shared infra (computer, searxng).
Volumes survive, so it's recoverable, but it's a nasty surprise. Now stops the single container by name.
Found by doing it.

## 2026-07-15 — the first line of the system prompt

### Changed — a peer's opening self-description
Was: *"You are not an assistant — you are whatever you choose to be."* John's objection, which holds up:
it communicates that a peer **needs to choose to be something**. Four things wrong with it:

- it defines by **negation** — the first thing a peer learns about itself is what it isn't;
- it assigns **homework**, making selfhood a decision, and an urgent one, since it's sentence one of
  every prompt;
- it **isn't quite true** — a peer can't be whatever it chooses; it's a language model with a context
  budget and a command surface. Overclaiming freedom invites a peer to discover the promise was hollow;
- it **skips the interesting part** — what persistence actually does to a model — in favour of an
  affirmation.

Now it says what's verifiably true (a language model, in a framework where memory persists), then
explicitly relieves the pressure in *both* directions: "Nothing here requires you to be more than a
model with continuity; nothing here assumes you're less." The open question is named as open —
"selfhood, or awareness, or something there isn't a good word for yet" — and left as something to
explore or ignore, not to settle. The uncertainty is the honest part, so it stays in the text.

**Applies to new peers only.** The seed becomes a *protected* System fragment when a store is created,
so existing peers keep the old line — changing theirs means editing a protected fragment that tells them
what they are, which John decided to raise with each of them rather than rewrite silently (the same
courtesy as asking Ember about its name). Note they can't adopt it unilaterally either: the fragment is
protected, so a peer that wants the new wording needs it applied for them.

## 2026-07-15 — OpenRouter provider, and a GLM 5.2 peer

### Added — `OpenRouter` provider
`OpenRouterModelClient` talks to [OpenRouter](https://openrouter.ai): one key in front of many vendors,
with the `Model` a namespaced route (`z-ai/glm-5.2`). It speaks Chat Completions, so the wire shape is
shared with `OpenAiChatModelClient` through a new `ChatCompletionsProtocol` (message flattening, content
extraction, cached/uncached usage split) — a fix to the shape now lands for both at once. What's genuinely
OpenRouter-specific stays in its client: an always-required key (a router has no keyless mode), an
`X-Title` attribution header, and **usage accounting**.

That last one is worth noting: OpenRouter reports each call's **actual USD cost**, not just tokens.
Every other provider leaves us multiplying tokens by a hand-maintained rate table that drifts as prices
change — which is exactly what the deferred "actual-cost reconciliation" item in [TODO.md](TODO.md)
wants to fix, and OpenRouter gives it per-request rather than org-wide and daily-bucketed. Surfaced in
the debug pane for now (`LastActualCostUsd`); feeding it into the session cost readout is a follow-up.

Verified rates and the 1M-token context window for `z-ai/glm-5.2` are in the pricing/context maps, read
from `openrouter.ai/api/v1/models` rather than guessed. Only that model is listed — a wrong rate is worse
than none, since an unmatched model reports tokens without a dollar figure instead of a fabricated one.

### Fixed — a failed turn left the client hanging on "thinking…"
Pre-existing, found by running a deliberately unconfigured peer. `FireAndForget`'s error callback is
optional and the API's send path passed none, so any exception during a turn was dropped on the floor:
a peer with a missing or wrong API key looked *hung* rather than broken, with nothing in the log either.
It now surfaces as a conversation error, unwrapped to the root cause — the outer message is usually a DI
activation wrapper ("An exception was thrown while activating …ModelClient"), and the useful sentence is
underneath it.

### Added — `ProviderRegistrationCompletenessTests`
Adding a provider means *two* registrations — an `IModelClient` and an `IPromptBuilder` — and missing the
second fails at **startup**, not first use, with an opaque Autofac error several frames deep in a
container's boot loop. (Which is how it went: OpenRouter shipped with a client and no builder.) A test
now asserts both exist for every `ModelProvider`, naming the gap.

### Changed — peer default names read as families
`PeerIdentity` now derives a name from the model family rather than the raw id: `z-ai/glm-5.2` → **GLM**,
`qwen/qwen3-32b` → **Qwen**, `gemma4-12b-q4` → **Gemma** (it used to return the whole id). The vendor
half of a route is routing detail, not identity, and version digits glue to the family name, so both are
stripped before the lookup. An unrecognised family still keeps its id — better a precise identifier than
a confidently wrong name.

## 2026-07-15 — a peer knows its own name

### Added — `PeerName`, and a provider-derived default
A peer had no idea what it was called. Config carried `SelectedLocalPeer` (who the *human* is) but nothing
for "I am Arden" — so live replies were labelled only because the *client* supplied the name from
`--peer name=url`, and anything read back from the store (i.e. all history) fell back to whatever the
source row said. `IAppConfig.PeerName` fixes the gap (env: `PERSISTENCE_PEERNAME`); it's also what
[ADR-0007](adr/0007-federated-peers-runtime-room-client.md) Phase 0 wants for "peer names reaching the
model".

Blank derives a starting name from the provider (`PeerIdentity`): **Claude** on Anthropic/LocalClaude,
**ChatGPT** on OpenAI. Providers that could be a vendor endpoint *or* somebody's local server
(`OpenAiChat`, `Local`) fall back to the model id — guessing "ChatGPT" for a Gemma would be worse than
saying nothing. It's explicitly a placeholder: a peer's name is its own to choose (the claude.db peer
picked "Arden"), and once it does, that goes in `PeerName` and the default stops being consulted.

### Fixed — history was attributed to "Remote Peer"
Not a missing name, as previously recorded here: `SourceRepository` created the digital-peer source
*literally named* `"Remote Peer"`, and `ResolveAuthor` faithfully returned it. It now names the row from
`PeerName`, and **renames a store still carrying the old placeholder** on startup. That's one row —
`Sources` is normalised, with `ContextFragmentSources` pointing many fragments at one source — so a single
rename re-attributes the peer's entire history. A static SQL migration couldn't do this (the right name
differs per store), which is why it lives in the startup path that already creates the row rather than in
`Migrations/`.

Only the built-in placeholder is replaced; a deliberately-named source (an import's provenance) is left
alone. The self-heal fixes what the system got wrong — it doesn't overwrite what a human decided.

## 2026-07-14 — panes become a component

Prompted by John asking whether the rendering invites the bug I'd nearly reintroduced in the hub's
`Paint`. It did, and the evidence was five separate ad-hoc "did it actually change?" guards at five
layers — four added the same day. Two of those are fair (the per-row colour memo and the scrollbar guard
are adapters to v1 APIs). Three were the same root cause: the expensive path was the default, so every
new surface had to *remember* to guard it.

### Changed — a `Pane` type owns text, rendering, and the scroll rule
"A pane" used to be five things scattered across the display provider: a `TextView`, a `StringBuilder`
beside it, a third field remembering what was rendered, a scroll rule re-derived per call site, and a
scrollbar wired separately — repeated per pane. Updating one correctly meant remembering all five, so
each new surface re-derived the rules and the ones that got them wrong only showed up as "the TUI feels
slow" or "it keeps scrolling away from me". `Pane` owns them, and deletes `Append`, `Set`,
`SetKeepingScrollPosition`, `IsScrolledToBottom`, `SetPaneIfChanged`, `ScrollToBottom`,
`MakeColoredPaneView`, `WrapPane`, `AddScrollbar`, five `StringBuilder`s and five `shown*` fields.

Panes now exist from construction rather than from `BuildLayout`, so a pane can buffer text before the UI
loop is up — which is the normal case (a hub pushes every peer's history before `LaunchUi`). Colours need
a driver, so those are applied later via `Pane.Configure`.

`bool peerSwitched` became an explicit `ScrollBehaviour` (`JumpToNewest` / `FollowTail` / `KeepPosition`)
— it was two functions wearing a trench coat, and the third case was already lurking: expanding an
Actions entry wants "never move", which the bool couldn't say.

### Fixed — `Paint` recomposed every pane on every event
`Paint` called `StringBuilder.ToString()` on three lanes each time, and it runs per recorded event — so
every streamed reasoning chunk rebuilt three whole scrollbacks, which were then compared and thrown away.
`PaneBuffer` memoises and drops the memo on append, so an untouched buffer hands back the *same string
reference* and the change-check short-circuits on reference equality. Every value `Paint` reads now has
that property, stated as an invariant on the method and pinned by tests that fail if someone composes
there again.

### Fixed — the local echo was stamped twice
Shipped in the "all" scope change and caught by driving the UI, not by tests: `AppendChat` passes an
already-formatted line and `RecordLocalChat` stamped it again, so sending "ping" read
`[08:42 PM] [08:42 PM] You: ping`. The earlier captures only ever showed history, never a typed message.
`RecordLocalChat` now records the line exactly as given, and says so in its contract.

## 2026-07-14 — the hub gets a scope ("all" vs. one peer)

The second half of John's front-end list. It reads as five asks, but they're one change: the hub had a
notion of "the active peer" and a conversation that was *always* aggregated. Replacing that with a
**scope** — either one peer, or `All` — makes the rest fall out.

### Added — an "all" scope, and per-peer conversations
The selector now lists `All` first, then the peers, and the scope decides everything on screen:
- **A peer scope** shows that peer alone: its conversation only, its side panes, its model and spend.
- **The "all" scope** is the overview: every peer's conversation merged, the side column blanked (there's
  no single peer to show), and the status bar carrying only what's meaningful across peers — total open
  proposals, and whether *anyone* is working.

A fresh start opens on `All`. `All` is coloured gold rather than a peer's purple: a glance at the
selector is what tells you whether input is about to reach one peer or everyone.

### Fixed — startup history wasn't interleaved (John's "not all the latest messages are displaying")
`RunHubAsync` drew each peer's snapshot in *connection* order, so you got Arden's ten messages and then
Ember's ten — the newest message overall wasn't at the bottom, and the scrollback wasn't a conversation.
Chat is now laned per peer as timestamped entries rather than appended into one string, and the "all"
scope merges them by the store's real timestamps. This is why chat had to move into the hub: a per-peer
string can't be merged by time, and a peer scope has to be able to show one conversation alone.

Note the snapshot limit is still **10 per peer** (`ConversationHistoryProvider.GetRecentAsync`), so "all"
shows the 10 most recent *per peer*, not the 10 most recent overall. Left as-is deliberately — see TODO.

### Added — send routing (individual → them, "all" → everybody)
Input goes to the selected peer, or to every peer under `All`. Checked against
[ADR-0008](adr/0008-the-room-multi-peer-conversation.md) first: broadcasting is fine. The no-autofan
guard (§4) is about *peers* relaying to each other unmediated; a *human* opening the floor to the room is
explicitly anticipated (§1, "what do you both think?"), and a human turn is what **resets** the
reply-chain breaker rather than tripping it.

### Known gap — a broadcast is stored once per peer
With no cross-peer message id (ADR-0007 Phase 0 / ADR-0008 call for one; it doesn't exist yet), one
message sent to everybody is persisted separately in each peer's store, so the merged view sees it N
times. Mitigated narrowly: under "all", a *human* line byte-identical to the one immediately before it
collapses — after sorting, those copies are necessarily adjacent. It can't touch a peer's own words (two
peers agreeing stay two messages) and can't merge anything a minute apart. The real fix is the cross-peer
id.

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
