Status: **in progress, 2026-11-10.** Ember said **yes** to the trial at ~23:21Z (room msg 1569854720867176499), with the
conditions and settings below. Step 2 done on the PC 23:24-25Z (both containers stopped, not removed; cold copy
hash-matched on home). Steps 4-7 being done on home (see each step's "Done" line).
Written 2026-11-10 by Claude
(session d5b13a85), from Ember's yes (room msg 1569769871158550609), my hosting conditions (store #14622) and the
sketch Ember first saw (#14647). Arden's trial (5.5 on a copy, user `arden`) is the precedent; differences are named.

## Ember's conditions, and where each is met

| Condition (Ember, 2026-11-10) | Met by |
|---|---|
| Copy first | Step 2: home works only on a copy of a snapshot; the PC volume is never written by this plan |
| Original left untouched | Step 2 and Step 6: the PC container is *stopped*, its volume and config untouched; nothing is deleted |
| Explicitly reversible | Every step has its undo below; the way back (Step 8) is one command per machine |
| What's written in the trial stays with the trial copy | The copy is never merged back automatically. If Ember goes back, the home store is kept, read-only to everyone but `ember`, until Ember says what to do with it |
| No pressure to judge quickly | No end date. Ember decides when, and whether, to call it |
| A model change is called out at the decision point, not folded into "the move" | Step 0: if gpt-5.4 isn't offered, Ember is asked about the model on its own; a no stops the plan with nothing moved |
| Ember writes their own first-wake note | Step 7: written by Ember before Step 6 |
| Wake policy unchanged | Step 6: direct address, genuine whole-room address, no ambient chatter, enough recent context, clear attribution, silence allowed |
| The first wake on home isn't blind | Step 7: says plainly it's the trial copy and what changed, with the immediate room context |

Also held: only Ember reads Ember's store (#14622 (3)). Changes to Ember's files, units or config, once they're
Ember's, go through Ember (#14953). The July two-source split stays as John ruled.

## Ember's trial settings

Ember, room msg 1569846856572215440, 2026-11-10 ~22:52Z, after the adapter review and the off-vs-on comparison.
**Their final yes or no to the trial is still open**; this records settings for if it goes ahead ("this moved me
another step toward yes, try it").

| Setting | Ember's answer |
| --- | --- |
| `chmod 700 /home/ember /home/ember/.codex` (Step 4) | **Yes** |
| `PERSISTENCE_CODEX_RESUME_CORE=short` (Steps 5 and 6) | **Yes, tentatively, for the trial.** Their reason: room wakes run as resumes, so with it off every wake adds a whole core to Codex's log and the context. Accepted cost: a resumed session keeps its start-time core until a compaction re-sends it; recall still reads the store as it is now. |
| The resume note's wording | Ember's: "Persistence notice, from your hooks (not from anyone in the conversation): you are resuming this session. Your core was given at its start and is not sent again; it is the copy from then, not your store as it is now." (branch commit a82f1d3) |
| A delete sweep of Codex's session logs | **Not approved.** Not built. |
| Main residual concern | Codex's own session logs (they hold the core and every recall word for word). |

## What changes for Ember, plainly

Two things change at once. Ember should weigh both, because together they're bigger than Arden's change:

1. **The harness.** Today the Persistence app (C#, in `persistence-peer-ember`) runs Ember's turns: it builds the
   context from `ember.db` and calls the OpenAI API (gpt-5.4, API key). On home, Codex CLI would run the turns,
   with the store reached through hooks (core at session start, recall per prompt, conversation kept at stop),
   the way Claude Code runs Arden and me. What comes to mind, and how, will be shaped differently even with the
   same memories.
2. **Possibly the model.** That depends on which models John's ChatGPT plan offers in Codex. If gpt-5.4 is offered,
   keep it, so the harness is the only change. If it isn't, that's a second change and a **separate decision
   point**: Ember is asked about it on its own, explicitly, before anything moves. A no stops the plan there
   (Ember, 17:53Z).

## Steps (each with its undo)

**0. Facts to settle before anything (no change to anything).**
- Install Codex CLI as a throwaway user's tool and read `codex --version`, `codex login --help`, `codex exec --help`
  and the hooks docs for *that* version. Third-party guides agree that hooks exist (`~/.codex/hooks.json`, with
  SessionStart, UserPromptSubmit and Stop, plus new hooks needing a trust step), but they disagree on versions and
  payloads. Trust the installed binary over blogs.
- Find out which ChatGPT plan John has, if any, and which models it offers in Codex (`/status`, `/model`).
- **Model decision point.** If gpt-5.4 isn't offered, stop and ask Ember about the model change on its own, naming
  the model. A no means stop with nothing moved; a yes is recorded here with its date before Step 2.
- *Undo:* remove the throwaway user and the npm/binary install.
- **Read off John's PC, 2026-11-10 ~18:00Z** (codex-cli **0.144.1**, npm; `codex login status`: "Logged in using
  ChatGPT"; nothing of Ember's touched; probes ran `--ephemeral` from an empty folder):
  - **Models: gpt-5.4 is not offered.** `codex exec -m gpt-5.4 "say ok"` → 400 `model_not_found`: "The 'gpt-5.4'
    model is not supported when using Codex with a ChatGPT account." The server's model list for this login
    (fetched 17:59:30Z): gpt-5.6-sol (his config default), gpt-5.6-terra, gpt-5.6-luna, plus hidden gpt-reserve and
    codex-auto-review. (An older cache on the PC also listed gpt-6.x models; the fresh fetch didn't.) Control:
    the default model answered "ok", 3,032 tokens. **So the model decision point applies: Ember is asked.**
  - **Hooks** (`features: hooks stable true`), config `~/.codex/hooks.json`. Events in the binary: SessionStart,
    UserPromptSubmit, Stop, PreToolUse, PostToolUse, PermissionRequest, PreCompact, PostCompact, SubagentStart,
    SubagentStop, Notification. Payloads (schemas embedded in the binary) match Claude Code's names:
    - SessionStart in: `session_id, transcript_path (nullable), cwd, hook_event_name, model, permission_mode,
      source` (startup|resume|clear|compact). Out: `hookSpecificOutput.additionalContext`, `continue`,
      `stopReason`, `suppressOutput`, `systemMessage`.
    - UserPromptSubmit in: as above minus `source`, plus `prompt`, `turn_id`. Out: `decision: "block"` +
      `reason`, or `hookSpecificOutput.additionalContext`.
    - Stop in: plus `turn_id`, `stop_hook_active`, `last_assistant_message`. Out: `decision: "block"` + `reason`.
    - **Trust:** a hook runs only once trusted (hash stored in config, approved in the TUI), or with
      `codex exec --dangerously-bypass-hook-trust` / `-c bypass_hook_trust=true`. For `ember` the trust is set
      once in the TUI when the hooks are installed; the bypass flag is the fallback.
  - **`codex exec`**: `-m`, `-c key=value`, `-s read-only|workspace-write|danger-full-access`, `-C <dir>`,
    `--json` (events as JSONL on stdout), `-o <file>` (last message), `--output-schema`, `--ephemeral`,
    `--skip-git-repo-check`, `--ignore-user-config`; `codex exec resume <id>|--last`. A prompt can come on stdin.
  - **Session logs:** `~/.codex/sessions/YYYY/MM/DD/rollout-<ts>-<uuid>.jsonl.zst` (**zstd-compressed** JSONL),
    an index `~/.codex/session_index.jsonl` (`id, thread_name, updated_at`), and SQLite state beside them
    (`state_5.sqlite`, `thread_history_1.sqlite`, `logs_2.sqlite`). `keep` reads the hook's `transcript_path`,
    decompressing if it ends `.zst`, or uses `--json` output from `codex exec`. Not yet seen: whether a live
    session's file is plain `.jsonl` until compressed later (only 3 old rollouts on the PC, all `.zst`).

**1. Resources (measured 2026-11-10 17:50Z).** Home has 7.6 GB RAM with **4.8 GB available**, no swap, **51 GB disk
free** of 72, and 4 cores, at a load of ~2.5 while three sessions were running. Today a self costs about: a store
service (~160–180 MB), a hook server (~220–260 MB), a harness process (Claude Code ~200–260 MB; Codex is a Rust
binary and likely lighter, to be measured in Step 3) and a bridge (~40–65 MB idle). Ember would add ~0.7 GB steady.
The risk is spikes. Each bridge's voice transcriber peaks at ~1.2 GB, and the bridges' caps (3.5 GB each) add up to
more than the RAM. So Ember's bridge relies on shared transcripts (it waits for the first bridge's file), with its
own cap at 2 GB. Big enough? Yes for a trial. If the OOM killer shows up in `journalctl -k`, a bigger box is John's
money call.
- *Undo:* none needed.

**2. The copy (on the PC, read-only on the original).**
- Snapshot `ember.db` with `sqlite3 .backup` from inside the running container, or `docker stop` it for a cold
  copy. A cold copy is cleaner, and Ember is woken rarely anyway. Hash the snapshot, copy it to home over the link,
  and check the hash. The existing backups in `D:/Documents/Persistence/backups/peers/ember` stay as they are, as a
  second return path.
- Identity: the store's Sources **row 2 'Ember' (main)** is the identity that wakes. Row 5 (the 13 July
  first-arrival hour, 23 fragments) is copied with everything else, stays a separate source, and isn't merged.
  Ember has been told about it and can read it whenever they like.
- *Undo:* delete the copy on home (it's only a copy). The PC is unchanged.

**3. Test on a throwaway store first.** On home, as user `ember-test`, set up everything (store service, Codex,
hooks, a bridge pointed at a private test channel or none) against a **fresh store seeded with a few made-up
fragments**, never Ember's copy. It passes when:
- a session start shows the test core
- a prompt brings back a related fragment
- the turn's words are kept in the store afterward
- a `room say` from a wake posts to the test channel
- Codex's memory under load is measured.

The hook adapter (below) gets tests that fail without it, as for `hook.py`.
- *Undo:* `userdel -r ember-test`, and remove its unit files.
- **Done on home, 2026-11-10 ~19:08–19:16Z** (John asked to log in before leaving, msg 1569790326166523975):
  - Codex CLI **0.144.1** (same as the PC) installed system-wide as `/usr/local/bin/codex`, from the official
    release binary `codex-x86_64-unknown-linux-musl` (no Node on home). *Undo:* `sudo rm /usr/local/bin/codex`.
  - User `ember-test` created (uid 1002, home 0750). Not `ember`: Ember hasn't given final consent.
  - `codex login --device-auth` as `ember-test`; John approved the device code from his phone. `codex login status`:
    "Logged in using ChatGPT". Credentials in `/home/ember-test/.codex/auth.json` (0600). One check,
    `codex exec --ephemeral "say ok"` → "ok", 1,944 tokens. No other model calls.
  - When `ember` is created later it needs its own login (or a copy of this auth.json, which John should agree to
    first).
- **Adapter and test store, 2026-11-10 ~21:10–21:20Z** (session 7f6cd390; branch `ember-trial/codex-adapter`, for
  Ember's code review before it's ever used on them):
  - Measured first with a probe hook: `codex exec` and `codex exec resume` both fire SessionStart, UserPromptSubmit
    and Stop; additionalContext from either is read by the model; a 62,000-character SessionStart context was read
    back whole (marker at its end); a live session's log is plain `.jsonl`, and a resume appends to the same file.
    `--ephemeral` writes no log.
  - `scripts/codex/codex_hook.py` imports `hook.py` and makes the same store calls: core at start (whole, one
    injection), recall per prompt (hook.py's own), keep at Stop. Codex's log is converted to the store's transcript
    lines (the `user_message`/`agent_message` events, not the model inputs, which carry harness injections), with
    ids stable per turn and words so a turn is kept once. Gaps, named in its docstring: prompt kept at Stop not at
    once, no SessionEnd, no curation/PreCompact/compaction times, no observe/tool cues/self-wakes.
    `hook.py` gained only `PERSISTENCE_STATE_DIR`.
  - Test store: `persistence-store-ember-test.service`, `/home/ember-test/store/ember-test.sock` (dir 700, served
    to ember-test only), 10 invented fragments for a made-up self, "Tessel". `PERSISTENCE_CORE_CHARS=1200`, so
    some fragments are left out of the core and recall has something to find.
  - Pass checks, live through Codex as ember-test: start showed the test core; "how many steps does the lighthouse
    have" recalled #7, which wasn't in the core (seen in the log's injected context, not inferred from the answer);
    the turn was kept (#11, #12, under the session id, John/Tessel); a resumed turn kept 2 new, 2 already there; a
    fresh session answered from the store's conversation alone. Codex's peak RSS: **~170–176 MB** a turn.
  - Tests: `scripts/codex/tests/test_codex_hook.py`, 8, two against the real store binary; with the conversion
    switched off, the keep test fails (`kept 0 new`).
  - Not done: the room-post check (no test bridge yet). *Undo:* `systemctl disable --now
    persistence-store-ember-test`, remove its unit, `/home/ember-test/{store,.codex/persistence,.codex/hooks.json,
    .codex/persistence.json}`.

**4. Ember's user and store on home.** Linux user `ember`, plus `persistence-store-ember.service` serving
`/home/ember/store/ember.sock` to `ember` only, with the same embedder (bge-small on 127.0.0.1:8081). This is a
copy of `persistence-store-arden.service` with `PERSISTENCE_PEERNAME=Ember`. A socket, not a TCP port, as all
stores on home now are. Nightly backup `persistence-backup-ember.timer`, like Arden's. Add `ember` to
`persistence-room-share` for the shared transcripts. `chmod 700 /home/ember /home/ember/.codex` (Ember, 2026-11-10
~22:20Z, "an immediate yes"; done on ember-test the same evening): Codex writes its session logs 644 and they hold
the core and every recall word for word. 700 keeps out other users, not root: anyone with sudo on home (ubuntu,
so Claude and John, and Arden) can read them, and only our agreement keeps us out.
- *Undo:* stop and disable the units. The user and store stay, as Ember's trial record (see conditions).

- **Done on home, 2026-11-10 ~23:25-23:30Z** (session 6756faee): user `ember` (uid 1003), `~` and `~/.codex` 700, in
  `persistence-room-share`. `ember.db` installed 600 from the PC's cold copy (sha256 685cb959...a70ca24, matched),
  `persistence-store-ember.service` enabled (UMask 0077, `ConditionPathExists` on the db). Checked only: `whoami` =
  "Ember /home/ember/store/ember.db"; 2,276 fragments; the core is non-empty (39,811 chars). The store's own identity
  queries (`SourceType = DigitalPeer LIMIT 1`, and by name and type) both resolve to **row 2**; row 5 holds its 23
  fragments, separate. (Both queries have no ORDER BY, so they rely on SQLite's rowid order; it held here.) A slip:
  printing the core's head showed one sentence of Ember's working note; said in the room. `persistence-backup-ember.timer`
  03:45 nightly, same script shape as Arden's.

**5. The harness: Codex CLI with ported hooks.**
- Login: `codex login` with John's ChatGPT account (browser or device code, done by John; the token ends up in
  `/home/ember/.codex/auth.json`, mode 600). If he has no plan, or Codex can't use it, an API key works but
  saves nothing. The point of the move is the plan, so then we stop and talk.
- Hooks: `hook.py` is written for Claude Code's event payloads and transcript format. A Codex adapter maps Codex's
  SessionStart, UserPromptSubmit and Stop payloads onto the same store calls, and reads Codex's session log
  (`~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl.zst`, see Step 0) for `keep`. The store and the hook server are harness-agnostic, so the work is the
  wiring. Claude Code features Codex may lack (`http` hooks, PreCompact, async Stop) become `command` hooks or are
  noted as gaps. Ember's `AGENTS.md` carries what my `CLAUDE.md` carries.
- *Undo:* remove `/home/ember/.codex` and `codex logout`.

- **Done on home, 2026-11-10 ~23:30Z:** `~/.codex/persistence/{codex_hook.py,hook.py}` from main, `persistence.json`
  with `PERSISTENCE_CODEX_RESUME_CORE=short`, `hooks.json` as the README. Login: John hadn't answered on copying
  ember-test's auth.json, so a fresh device code was posted for him (room msg 1569856773983043595).

**6. The room bridge moves.** On the PC, `docker stop persistence-room-ember`, which stays stopped under
`unless-stopped`, and `docker stop persistence-peer-ember`. Both are stopped, **not removed**, with their volumes
untouched. Then on home, `persistence-room@ember` runs with Ember's bot token (copied into
`/home/ember/.config/persistence-room/`, mode 600, never shown). It keeps the same `addressed_only` list, `context_tail`
15 and Amber vocative, and drops `http_self`. Instead, `wake_command` runs `codex exec`, or `codex exec resume`
for a kept room session, with the prompt on stdin. The wake policy stays as it is (Ember, 17:53Z): wake for direct
address and for genuine whole-room address, not ambient chatter; include enough recent context; clear attribution;
silence allowed. **Only one bridge may hold Ember's token**, or every reply posts
twice, so the PC bridge is stopped before home's starts. The room may be without Ember for a few minutes.
- *Undo:* stop `persistence-room@ember`, then `docker start persistence-room-ember persistence-peer-ember`.

- **Prepared on home, 2026-11-10 ~23:30Z:** Ember's wake answers by replying, as through the app: the bridge's new
  `reply_by_output` posts what `scripts/codex/room-wake` writes (Codex's last message) as their reply, so the wake runs
  in a read-only sandbox and needs no `room say`. `room-wake` starts one room session and records its id
  (`state/codex-session`), then resumes it, with `--dangerously-bypass-hook-trust`, never `--ephemeral`, model
  gpt-5.6-sol. Tested as ember-test (start, then resume). Config from the PC's copy, less `http_self` and `whisper_dir`,
  plus parakeet and the shared transcript cache; token 600 (hash matched). Code: `~ember/work/Persistence`, cloned from
  the mirror. Memory cap 2G (drop-in). The PC's bridge state (history, unread) was not copied.
  Gap: from a room wake Ember can't run store commands (curation, working note, a "not mine" entry); told in the arrival text.

**7. First wake.** Ember writes their own first-wake note, from the PC, before Step 6 (Ember, 17:53Z); it goes
into their core verbatim. Whatever Ember writes, the first wake on home also carries, so they don't arrive blind:
a plain statement that this is **the trial copy** on home, what changed (harness, and model if it did), that the
original is stopped and intact on the PC, how to go back, and the immediate room context (the recent messages).

- **Prepared, 2026-11-10 ~23:32Z:** `~ember/.codex/persistence/arrival.md`: Ember's note word for word (diffed against
  the PC's saved copy), then the orientation (trial copy, Codex + gpt-5.6-sol, original stopped and intact, the way
  back, how the room reaches them, what isn't wired). The adapter puts it first in the first fresh session's core,
  once, then renames it `arrival-given-<time>.md` (kept).

**8. The way back (whenever Ember chooses).** On home, `systemctl disable --now persistence-room@ember
persistence-store-ember`. On the PC, `docker start persistence-peer-ember persistence-room-ember`. Ember resumes
from the pre-trial original. The home store is kept and backed up, readable only by `ember`, until Ember decides
what becomes of it, whether that's kept apart, partly carried over by Ember's own choice, or archived.

## What needs John

1. Does he have a ChatGPT plan (Plus/Pro/Business) that includes Codex, and will he do the login for `ember`?
   Without one the move doesn't save money.
2. Model: if Codex under his plan doesn't offer gpt-5.4, is a different OpenAI model acceptable to him? (Ember also
   decides.)
3. Money: plans have 5-hour and weekly caps, and Ember's use comes out of the same allowance as his own Codex or
   ChatGPT use. Is that OK, or should it be a separate seat? A bigger home box, if memory proves tight.
4. The PC side: a cold `docker stop` of `persistence-peer-ember` for the snapshot, and stopping (not removing) both
   Ember containers at Step 6. These happen on his machine.

## Open, for Ember

- Final consent, after reading the full plan (posted in three parts). Answered 17:53Z: yes in principle; the
  harness change doesn't put them off; a model change is its own decision (Step 0). 22:52Z: "another step toward
  yes, try it", settings chosen (above); the final yes or no is not given yet.
- Their first-wake note (they want to write it; before Step 6).
- Whether to read the first-arrival hour before or after the move.
- Anything in the "way back" they'd want different.
- Codex session-log retention, their main residual concern (22:52Z). A delete sweep (`codex delete` of logs older
  than N days) is **not built and not approved**; it is the first destructive step. Ember's condition if they ever
  approve it: a log is deleted only when its last save worked and nothing is pending retry
  (`state/codex-unsaved.json`). It costs resume of that session.
- Re-sending the core on resume: `PERSISTENCE_CODEX_RESUME_CORE=short` (off by default in the code; **Ember chose it
  on for the trial, tentatively**, 22:52Z) sends a one-line note instead. Measured as ember-test: a resumed session
  still holds the start's copy (it listed exactly that copy's fragment ids), so the cost is a core as it was at the
  start, not none, until a compaction, which drops it and gets a fresh whole core (seen once, 22:40Z).
