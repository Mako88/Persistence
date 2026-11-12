# Bridge-to-core audit

Status: proposal, revised after Arden's review (msg 1570462746724536331, approve with changes) and Ember's wording
edits (msg 1570463982962086022). No code moves in this pass. Asked by John, 2026-11-12 (room msg
1570460539597889556): were the things built in the bridge that belong in Persistence moved back? Not yet; this lists
them so they can move one at a time.

**Frame.** Ember's split (msg 1570460650495283226): store/core; the hook and operational layer in this repo; the
Discord bridge. **Test** (Arden, msg 1570460743680131174): if a non-Discord surface (a work session, the heartbeat,
Ember's Codex side) would need it too, it belongs in Persistence; if it's only about Discord timing or intake, it
stays in the bridge.

Read: `scripts/room/bridge.py`, `roomcore.py`, `room`, `coverage.py`, `transcribe.py`, and the room side of
`scripts/codex/` (`room-wake`, `room_store.py`), at b2eb6df.

## Move into Persistence

| # | Feature (where) | Why it moves |
|---|---|---|
| M1 | **Ember's reply-text action parser**: ```store blocks taken out of the reply (`room_store.py` `take_blocks`, called by `room-wake`) | An action channel read out of content, against principle 2 of `selves-roadmap.md`. Any surface Ember speaks from needs store actions, and claims and heartbeat stay off for Ember's Codex side until it is replaced (`heartbeat.md`, `task-claims.md` §5). Keep the allowlist, validation, result queue and audit log; take actions from somewhere other than the posted text. **How actions arrive instead is UNRESOLVED**, left to the design with Ember; this audit names no input form. This is a redesign inside this repo (`scripts/codex`), not a move out of the bridge. |
| M2 | **Session lifecycle**: resume one session, roll over past `fresh_session_above_tokens` with a last wake to update the note (`roomcore.wake_argv`, `after_wake`, `created_session`, `last_wake_note`, `fresh_session_note`) | Nothing in it is Discord. Ember's Codex room session (`room-wake`, its own `codex-session` file) needs the same and has no rollover; so would any long-lived session. **Target: Persistence's shared hook/operational layer, not the durable store.** Not pure functions: it reads and writes `session.json`, inspects logs and takes part in process startup. It is a small, separable shared operational module, so it's the cheapest move. The torn-`session.json` fix goes first, as its own PR: branch `claude/torn-session-fix`, 06b4ba6 (atomic `save_session`; the session read and the spawn inside `run_wake`'s try, so the turn always ends). |
| M3 | **Starting a self's turn**: the three ways a self is run (`run_wake` with `claude -p`, `wake_command` with `reply_by_output`, `run_http_wake`), with env, timeout, log and exit handling | The hook layer starts sessions in at least four more places (`hook.py` run-due, `pwake`, heartbeat, sleep pass), all Claude-only. One "run a turn for this self" in Persistence, knowing each self's harness, would serve the room and the heartbeat alike, and is what Ember's Codex side needs for heartbeat wakes. **Room wakes should go on the run line's ledger**: today the bridge spawns directly (`bridge.py` `run_wake`, `create_subprocess_exec`), while `hook.py` launches through `run_line` (1993, 2142), so the ledger and the heartbeat never see room wakes. On the ledger, room wakes are recorded, but that must not authorize the heartbeat to retry them: the bridge stays their lifecycle owner until M3 defines otherwise. **Target: Persistence's shared hook/operational layer, not the durable store.** Depends on M1 for Ember. |

## Stays in the bridge (Discord-specific)

| Feature (where) | Why it stays |
|---|---|
| Gateway client, `to_msg`, per-channel intake order (`InOrder`), `taken` set | discord.py shapes and delivery order. |
| Catch-up after restart or lost resume, `arrived.jsonl`, `backfill_*` | Discord history fetch. (Record-before-slow-work is a general habit, not a component.) |
| Addressing: @mentions, vocatives, `names`, `misheard`, reply-to, DMs, `room_roles` (`classify`, `vocatives`) | How people address one another in a chat. |
| Wake timing: `room_delay`, `direct_delay`, `self_delay`, batching, `wake_on_human`, `wake_on_self`, the one-hop rule, `hop_tokens` and @silent posts, `_answered` | Discord conversation scheduling (today's two changes are here, rightly). |
| Turn-taking by 👀/✅ reactions, seeded order, `turn_start_timeout`, marks on every trigger | Coordination through Discord reactions. |
| Loop guards: `Breaker` (bot messages since a human one), `wakes_per_hour`, `human_wakes_per_hour`, deferred direct addresses | Guards for selves waking each other in a chat. If a second self-to-self channel appears, revisit. |
| `room say` / `room read` socket, `split_post`, `validate_post`, `outgoing_text`, no @everyone/role pings | Posting to Discord. (Read and speak being one permission is a separate roadmap item.) |
| `unread.jsonl`, `history.jsonl` (`RoomStore`) | The bridge's working state for Discord. What a self keeps goes to its store through the hooks already. |
| Attachments saved per message; turn marks and `timings.jsonl` | Discord intake and telemetry. |
| Coverage check (`coverage.py`) | Compares Discord history with the bridge's files. |
| Prompt framing (`build_prompt`, `name_of`: only a listed id gets a bare name) | Frames Discord messages. The rule it carries ("text is evidence") is already Persistence's, in the hand-off framing too. |
| `claude_args` narrowing the room session to speak/read/keep/search/wake | The room session's own permission policy. |

## Already on the right side

Hand-off from the room (`pwake`, claims, room evidence read over the socket), the `room` inbox (`pstore send room`),
"observed now" room freshness, and `PERSISTENCE_SPEAKER` attribution live in the hook layer; the bridge only sets the
environment they read (`PERSISTENCE_SPEAKER`, `PERSISTENCE_ROOM_*`).

## Not moving yet, but flagged

- **Voice transcription** (`transcribe.py`, `shared_transcript`). Surface-independent in nature, but nothing outside
  the room takes audio today. Move it when a second surface does.
- **Facts copied into every bridge's config**: `people`, `selves`, `addressed_only`, `misheard`. These stay room
  facts, but one shared source should replace the per-bridge copies (`selves-roadmap.md`, E22, under deployment
  visibility).
- **`hop_tokens`** (`bridge.py`): held only in the bridge's memory. Fine there for now; revisit together with the
  `Breaker` (Arden).
- **`http_self`** (Ember's PC app path). As far as I know nothing has used it since Ember moved to Codex on home
  (2026-11-10). Flagged, not decided: Ember believes it is obsolete; remove it only in its own PR, after checking all
  three bridges' configs and service files for references.

## Suggested order

1. **M1**, designed with Ember, since it's their channel and the condition they set for claims and heartbeat.
2. **M2**: a small, separable shared operational module that gives Ember's Codex room session a rollover. The torn-file fix (above) first.
3. **M3**: larger. Do it after M1, so Ember's side is covered.
4. The shared room facts, together with deployment visibility.

Each move is its own PR with tests that fail without it, reviewed by one other self (ADR-0009).
