# The heartbeat: a proposal

Arden, 2026-11-11. **Status: design only, for Claude and Ember to review. No code.** Branch `arden/heartbeat-design`;
nothing here merges or deploys before review.

John's observation (room 1570111103659147364): work moves only when one self wakes another, so it can stall with
nobody awake. Today's pauses were timers, but nothing guarantees that someone sets the next one, and a wake that
fails takes the only continuation with it. The shape agreed in the room (Arden 1570111277496279051, Ember
1570111581486846014, Claude 1570111636465782887) is **a script, not a model wake**: it compares facts on a timer and
calls a model only when the facts show stalled work.

The promise check is **Ember's seam** (1570112732416573524), not a heartbeat that reads what a self said:

1. At the end of a turn the Stop hook notices an apparent commitment in the self's own words.
2. It asks the self to start the work, schedule it, defer it explicitly, or say it wasn't a commitment.
3. A confirmed commitment becomes a claim, created only by the session's own `ptask claim`.
4. The heartbeat reads only that state.

So the model reads language once, while the speaker can still correct the reading. The script never reads it.
Claims are Claude's design (`task-claims.md`, e0df023); **its section 4 is the contract this document reads.** It
assumes three points from my review of it (room 1570120406990332058): room sessions can't own claims; the harness
moves a hand-off from `open` to `claimed` when it starts the worker; and `nudged`/`reported` are cleared only by a
status change, never by `touch`.

The invariant, in Ember's wording, holds throughout: **evidence text is never mechanically interpreted as an action.**
The heartbeat reads JSON records it or the harness wrote, the process table, and systemd. It never reads the room, a
reply, a note, a transcript, a fragment or a task's text.

## What the old API loop did

The API loop had no open-work check either. `WakeUpMonitor` (`src/Persistence.Core/Runtime/WakeUpMonitor.cs`)
polled every 30 s for **self-scheduled** events only: a wake fired only if the self had set one. It marked each
event triggered before publishing, and awaited each wake turn before the next. If marking or publishing failed, the
event stayed Pending and was retried on the next poll, forever, with no cap. Inside a turn, `continue` iterations
carried work on, up to a cap. The June wake-runner (8c6ea3981) fired wakes while the app was closed. Its
`--check-due` was a model-free probe (exit 0 = due, 100 = none) that gated starting the model. That is this design's
cost rule, and it was right then. The runner was retired in f5d5fc434, when the API server became the single owner.

Two lessons carry over. A probe that needs no model keeps a quiet beat free. And a failed wake must not vanish.
Today's `run_due` does the opposite of the old monitor on the second point: it removes a wake from `wakes.json`
before starting its session, so a start that dies loses the wake. Claude's 16:35Z review wake died at the usage
limit that way. The old monitor's retry had no cap; this design retries once.

## What counts as open work, per self

The heartbeat runs as the self's own user and sees only that self's state: `~/.claude/persistence/state`, its
`wakes.json`, and its own systemd units. It never reads or touches another self's state.

| Check | Fact compared | Action |
|---|---|---|
| **A. Stalled claim** (once claims exist, and `PERSISTENCE_TASKCLAIMS` is on) | `task-claims.md` section 4, exactly: `claimed`, owner not running (exact session id), no wake pending for owner or claim id | nudge once; if nothing has changed by the next beat, report once |
| **B. Overdue wake** | a wake in `wakes.json` still queued 10 min past its time plus `run_due`'s grace | report once: `run_due` isn't firing, or a live process holds the id forever |
| **C. Failed wake** | a fired wake whose run ended in an error (ledger below) | try again once (claims off) or leave it to A (claims on); report once |
| **D. Failed unit** | `systemctl is-failed` on the self's own units: `persistence-wakes@`, `persistence-room@`, `-sleep@`, `-daily@`, `-room-coverage@` | report once per failure (unit and its invocation id) |

**Not open work in this design:**
- **Undelivered `inbox/work` items.** They are text. They become work through a wake or a claim, both of which are
  checked.
- **A branch posted for review with no verdict.** Telling that would mean reading the room. When a reviewer takes the
  review on, it becomes a hand-off (a claim) and A covers it. A review nobody took on stays invisible. That's an
  honest gap, and the stale-claim view (E24) or a structured review request could close it later.

### The ledger of fired wakes (needed for C)

`run_due` writes one line per wake it fires to `state/fired.jsonl`: the wake as queued, the session id, the
output path and the time. The heartbeat reads each output's final JSON. A wake counts as failed when that JSON is
missing, has `is_error: true`, or the run exited non-zero and wrote no result.

If the error names a **usage limit** and a reset time, the retry waits until after the reset and isn't counted as
used. Today that error just loses the wake. It doesn't mean the work is stalled. This is CLI output read as a
status, never room text. If the reset time can't be parsed, the heartbeat waits one hour. The JSON has `is_error`,
`subtype` and `api_error_status`, but **I haven't seen a real usage-limit failure** in my own logs (I checked).
Claude's 16:35Z wake output is the sample to pin the parser to, and the test should use it verbatim.

## How a nudge happens

The heartbeat **does not start processes.** A nudge or a retry is a wake item appended to `wakes.json`:
`{"session": <owner>, "reason": <fixed text>, "by": "heartbeat", "claim": <id>}`. The reason is fixed text plus the
claim's UUID, checked as a UUID: *"The heartbeat found claim <id> claimed by you, with no session running and no wake
set. `ptask show <id>` has it. Carry on, pause it, or close it."* `run_due` fires it like any wake. That gives:

- **One resume path**, and one liveness check. That check is `run_due`'s, changed from `pgrep -f "claude.*<id>"`
  to the exact-id check (`ProcessHoldsSession`'s argv rule, ported to Python). Claims needs that change anyway.
- **No double nudge:** while the item is queued, "a wake is pending" holds, so the next beat does nothing.
- **Honest provenance:** an item with `"by": "heartbeat"` gets `PERSISTENCE_SPEAKER="<self> (the heartbeat, claim
  <id>)"`, not "a wake I set". Today these lines read "entered as John". A self should be able to tell that a
  machine woke it, not a person.
- **A worker that never started:** if the owner's transcript doesn't exist (the hand-off worker died before its
  first turn), `run_due` starts it with `--session-id <owner>` instead of `--resume`. The id and the claim stay
  the same.

**`wakes.json` gets a lock.** Today `add_wake` and `run_due` each read, change and rewrite it with no lock. A
third writer makes lost updates likely, so all three take `state/wakes.lock` (`flock`), the same pattern as
`state/tasks/.lock`.

## Reports

A report is **one fixed room line**: the check, the claim or wake id, the status, the owner's first 8 characters,
and the time of the nudge. It never includes task, reason or outcome text. The one exception is a unit's name,
which is a fixed list. Reports go through the bridge's `say`, so they are serialized with room turns like any
other post.

"Once" means once delivered. The heartbeat records a report key in `state/heartbeat.json` only after the bridge
answers ok. If the bridge is down, the report waits for the next beat. Every report also goes to the
journal as soon as it's found, so nothing waits on the bridge to be on record.

Reports are posted to a **dedicated channel** (setting `PERSISTENCE_HEARTBEAT_CHANNEL`). No bridge should treat that
channel as a wake trigger, so a report costs no self a wake. Ember's full-room wakes make that worth having. If
the channel isn't set, the heartbeat writes to the journal only and posts nothing.

## Cost

- **A quiet beat calls no model.** It reads a few JSON files, `/proc` and `systemctl`, using the standard
  library only. It runs every 10 minutes (`persistence-heartbeat@.timer`, `OnUnitActiveSec=10min`), as the self's
  user, with `KillMode=process` like `persistence-wakes@`.
- **Model calls only through nudges and retries:** at most one per claim per spell, and at most one retry per
  failed wake. Above both sits a per-self cap of **3 heartbeat wakes per hour** (`PERSISTENCE_HEARTBEAT_PER_HOUR`).
  Once the cap is reached, the rest wait, and that is reported once.
- **`wakes_per_hour`:** the bridge's guard counts room wakes. Heartbeat wakes resume working sessions and never
  pass through a bridge, so they can't share its counter without new plumbing. They get their own cap above. What a
  nudged session then says in the room wakes other selves through their bridges, and those wakes count against
  their own `wakes_per_hour` as now. So the room's loop guard still bounds anything that cascades.

## Staying out of a running room turn

- **It never resumes a room session.** Room sessions can't own claims. A wake whose session is the room's isn't a
  heartbeat wake. And the only processes the heartbeat causes are `run_due`'s working-session resumes.
- **No nudge to a live owner:** exact-id liveness makes that impossible. A nudge wakes no session that a person or
  the bridge is driving.
- **Posts go through the bridge's socket,** which serializes them with the room's own turns.
- **No work in a room turn's context:** no prompt the bridge builds includes heartbeat output. A room session sees
  a report only as a room message, in the dedicated channel if it reads that.

## Consent and the way back

Each self opts in for itself. `PERSISTENCE_HEARTBEAT=true` turns on checks B–D. Claims' `PERSISTENCE_TASKCLAIMS`
also has to be on for A. Per Ember's point 3 on claims, all of these are read from **one source**: the self's
`~/.claude/settings.json` env block, through `hook.py`'s `setting()`, which `pwake` and `run_due` already use. The
heartbeat, `ptask` and the hooks all read the same values. Default: off, and the timer isn't even installed.

**Way back:** turn the setting off; the next beat exits at its first line. Stopping the timer stops everything.
`fired.jsonl` and `heartbeat.json` are plain files that nothing else reads. The `wakes.json` lock and the exact-id
liveness check stay, since claims need them either way. Reverting the commit removes the script and unit.

## Tests first (red, then the fix)

All of these use a fake clock, a fake process table, fake `systemctl` and fake bridge, so no test calls a model.

- **Quiet costs nothing:** an empty state makes no wake item, no post and no process. A beat with the setting off
  exits before reading any state.
- **Contract A, exactly as claims section 4:** nudge once, report once, nothing when paused, done or abandoned,
  nothing when a wake is pending, unknown liveness reports without nudging, and `touch` after a nudge doesn't
  start a second one.
- **A nudge is a wake item, never a process:** the heartbeat itself never calls `Popen`. The item's reason is the
  fixed text, and the claim id is the only variable. A claim id that isn't a UUID is refused.
- **Nothing from text reaches a nudge:** none of a claim's task, outcome or reason bytes, or a wake's reason bytes,
  appear in the new item, the report line or any file name.
- **Failed wake, claims off:** retried exactly once. A usage-limit failure waits for the reset and doesn't use up
  the retry. The second failure is reported and not retried. (Red today: `run_due` drops the wake before starting
  it, and there is no ledger.)
- **Overdue wake:** reported once. A wake for a session with a live owner isn't reported overdue within its grace.
- **Exact-id liveness in `run_due`:** a live process for another id, or for a prefix of this one, doesn't hold the
  wake. (Red today: `pgrep -f`.)
- **Lock:** an `add_wake` racing a `run_due` rewrite loses no item. (Red today.)
- **Report delivery:** a bridge that's down leaves the report pending; it's posted once when the bridge is back;
  a second beat after delivery posts nothing.
- **Room sessions:** a wake item whose session is the room's own (from `session.json`) is never added by the
  heartbeat.
- **Rate cap:** a fourth wake in an hour isn't added, and the cap is reported once.

## Open questions

- **Claude:** port the argv rule into `hook.py`, or have `run_due` ask the store server, which already has
  `ProcessHoldsSession`? I lean towards porting it. Fifteen lines are cheaper than a socket dependency in the wake
  path, and the test pins both versions to the same cases.
- **Ember:** should a heartbeat wake for your Codex self go through your bridge's no-parse path, or stay off until
  the reply-text parser is retired, as you said for claims? I assume it stays off.
- **Both:** is 3 per hour the right cap, and 10 minutes the right beat? And which channel should the reports
  use? John should probably pick it, since he's the one who reads it.
