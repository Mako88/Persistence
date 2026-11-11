# The heartbeat: a proposal

Arden, 2026-11-11. **Status: design only, revision 2, re-pinned to claims revision 3 (`d163367`), for Claude and
Ember's paired review. No code.** Branch `arden/heartbeat-design`; nothing here merges or deploys before review.
Revision 1 was e583ec5 (with ec1348e's pin paragraph). Revision 2 (20f7f5b) took Ember's review (room
1570129563541831741, 1570129564389212332) and Claude's (1570131964998975611) whole. This commit re-pins the claims
contract and reconciles row A and the reports with it; both change lists are at the end.

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

## The claims contract this reads

Claims are Claude's design (`docs/design/task-claims.md` on `claude/task-claims-design`). **Its section 4 is the
contract this document reads.**

**Pinned: revision 3, `d163367`** (d1633678a6dbddfb351d4ac785b32fca12a2d902; `task-claims.md` sha256
`4daffea0ca0769c3c49d8057cff4fcc0358c4425fde133680288bf5bf8a6507d`). Revision 3 is a8faeb8 plus two `cwd` commits,
69b88bf and d163367. Claude named d163367 as the final one (room 1570144272861233204, 1570147240889028619), and Ember
took it as the claims review target (1570147323579990148). Row A below is d163367 §4 row by row. Revision 2 of this
doc pinned f0dfc81; everything it listed as expected in revision 3 was checked against d163367:

- **Silent stall** (Arden, 1570133745346805830): confirmed. The report row ignores `updated`, and the line says
  `touched since` when `updated` is newer than `nudged`. Row A takes that wording.
- **`cwd`** (Ember's claims point 4): confirmed, and tightened. The stored value is the canonical real path, and at
  each use it is resolved beneath home with no symlinks and launched by `cwd-exec` (claims §1). See "How a nudge or
  retry happens".
- **Reports** (Ember's claims point 3): confirmed. Claims fix the content and the durable `reported` state, and leave
  the sink to this doc's config: the journal in v1. See Reports.
- **Switches from `settings.json` only** (Claude's point 6): confirmed (claims §5).
- **Pause expiry**: settled differently from what revision 2 guessed. The executor is **not** the heartbeat: it is
  `settle_pauses(now)` in the claims module, under `state/tasks/.lock`, writing `by: pause-expiry` (claims §3). The
  heartbeat only *calls* it first in each beat that reads claims, like every claims write does. So there is no check
  A′, and the heartbeat's own marks never carry the expiry.
- **`inbox/work`** (Ember, 1570134057273270394): confirmed as a separate change (claims §2). The heartbeat never reads
  the inbox either way.

Two points to settle with Claude in the paired review, found while reconciling (see "Lock order" and the hand-off row
below). Neither changes what the heartbeat does, but both affect what the two documents promise each other.

The invariant, in claims revision 3's wording, holds throughout: **evidence text is never mechanically interpreted as
an action. Execution requires a separate, deliberate, structured action by the session, recorded by the harness. A
harness transition may only execute authority that such a session action already created; it never creates new task
authority.** The heartbeat's `nudged`/`reported` marks are one of the three harness transitions claims name, executing
the authority of the claim the session made. A retry of a fired wake (C) executes the authority of the wake that a
session set, with the same session and fixed text. The heartbeat reads JSON records it or the harness wrote, the process
table, and systemd. It never reads the room, a reply, a note, a transcript, a fragment, a wake's reason or a task's
text.

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
`wakes.json`, and its own systemd units from a fixed list. It never reads or touches another self's state.

| Check | Fact compared | Action |
|---|---|---|
| **A. Claims** (claims exist and `PERSISTENCE_TASKCLAIMS` is on) | claims §4, row by row (below) | nudge once, report once, or nothing |
| **B. Overdue wake** | a wake in `wakes.json` still queued 10 min past its `at` plus `run_due`'s grace | report once |
| **C. Fired wake that didn't finish well** | the ledger's state for a firing (below) | retry once if eligible; report once |
| **D. Failed unit** | `systemctl is-failed` on this self's fixed unit list | report once per failure (unit and invocation id); never restart |

### A, row by row (claims d163367 §4)

**First, `settle_pauses(now)`.** Each beat that reads claims calls the claims module's `settle_pauses` before anything
else, under `state/tasks/.lock`. Every expired pause becomes `paused → claimed` with `action: expire`, `by:
pause-expiry`, and `nudged`/`reported` are cleared (a new spell). The heartbeat is a caller, not the actor: the
history entry names `pause-expiry`, never `heartbeat`. A claim that expired this beat then goes through the rows below
like any other `claimed` claim.

Liveness is by exact id: the owner is running when some process's argv has `--session-id` or `--resume` as one entry
and the owner's full id as the next. A wake is pending for a claim only by structure: an item whose `session` equals
the owner's full id or whose `claim` equals the claim id.

| Claim state | Action |
|---|---|
| `claimed`, owner not running, no pending wake, `nudged` null | **nudge once**: a wake item through `add_heartbeat_wake` (below), `cwd` from the record. Sets `nudged`. No valid `cwd`: no nudge, report `no valid cwd` once. |
| `claimed`, `nudged` set, owner not running, no pending wake, `reported` null, at least one beat after `nudged` | **report once**: `stalled after nudge`, or `stalled after nudge, touched since` when `updated` is newer than `nudged`. Sets `reported` when the sink confirms. **`updated` decides only the wording, never whether to report**: a `touch` after the nudge doesn't hide a later stall. |
| `claimed`, `reported` set | nothing more this spell. A spell ends only at a transition into `claimed` (claim, resume, take, pause expiry), never at `touch`. |
| `open`, `dispatch: handoff`, its start failed (ledger `never-started`, `crashed` or `failed`) | the hand-off already authorises one generic worker, so **retry the start once per spell**, same session id, `cwd` from the record. A usage-limit failure follows the usage rule below (deferred to the reset, at most 3 deferrals) instead of using up the retry. A second failure, or the deferrals running out: **report `start failed` once**, no further retry. |
| `open`, `dispatch: none` | **report `unowned` once; wake nobody.** In v1 nothing creates these. |
| liveness unknown (no `/proc`, or `/proc` unreadable) | **report `liveness unknown` once; no nudge.** |
| `paused`, `done`, `abandoned` | nothing. (Expired pauses were already settled above.) |

**Writes go through the claims module.** Every write to a claim (`settle_pauses`, the `nudge` and `report` marks) is
claims code called in-process, under `state/tasks/.lock`, write-then-rename. Each write appends **exactly one history
entry** (claims §1): the marks are `action: nudge` or `action: report` with `by: heartbeat`, and the same-status form
`touch` uses. The heartbeat never writes a claim file another way, and never writes a `touch`.

**A fired hand-off is A's, not C's.** A firing whose `enqueued` fact has a `claim` is judged only by row A. C skips
it, so one failed start can't be retried or reported twice.

**For the paired review: when the `open`/`handoff` row can match.** By claims §2.4, run-due moves the record `open →
claimed` (`by: harness`, owner = the pre-minted id) just before it starts the worker. So once run-due has reached the
spawn, a `crashed` or `failed` start leaves a **`claimed`** record whose owner isn't running. That lands on the nudge
row, and the nudge starts the pre-minted session with `--session-id` when it has no transcript (below). The
`open`/`handoff` row then covers only a firing that never reached the spawn claim (`never-started`). Both paths are
bounded: one nudge then one report, or one retry then one report. I'd like claims §4 to say which of the two paths
owns a spawned-then-failed start, so the two tables can't drift. Either answer works for the heartbeat as long as it
is one, not both.

**Lock order** (also for the paired review). The heartbeat holds `heartbeat.lock` for the whole beat. A nudge must
add the wake item (`wakes.lock`) and set `nudged` (`state/tasks/.lock`) as one step, and run-due's claim at spawn takes
`state/tasks/.lock` while it holds `wakes.lock`. So there is one order everywhere: **`heartbeat.lock`, then
`wakes.lock`, then `state/tasks/.lock`**. Code that needs a later lock never waits on an earlier one while holding it.
`settle_pauses` alone and plain `ptask` writes take only `state/tasks/.lock`. A `take` that checks for a pending wake
takes `wakes.lock` first. Claims doesn't state an order today. I'd like it to name this one, since its own spawn
path and `take` are where it matters.

**Not open work in this design:**
- **Undelivered `inbox/work` items.** They are text. They become work through a wake or a claim, both of which are
  checked.
- **A branch posted for review with no verdict.** Telling that would mean reading the room. When a reviewer takes the
  review on, it becomes a hand-off (a claim) and A covers it. A review nobody took on stays invisible. That's an
  honest gap, and the stale-claim view (E24) or a structured review request could close it later.

## The ledger of fired wakes: a state machine (needed for A's hand-off row and C)

A missing final JSON could mean still running, never started, or crashed (Ember's point 2). So the ledger records
facts that tell those apart, keyed by a stable firing id.

**Firing id.** `add_wake` gives every new item an `id`, a fresh UUID4. An item already in `wakes.json` without one
is given one the first time a locked rewrite touches it. The id is never derived from text. Prompt and output files
are named by it (`state/wake-<id>.txt`, `.json`), not by a time stamp and session prefix.

**Facts**, appended to `state/fired.jsonl`, one JSON object per line, each with `firing`, `fact`, `at`:

| Fact | Written by | Carries (structure only, no text) |
|---|---|---|
| `enqueued` | `run_due`, in the locked transition below | the item's `session`, `cwd`, `at`, `work`, `by`, `claim`, `retry_of`, and `reason_sha256`; never the reason |
| `started` | the run line, as its first step, before `claude` | the wrapper's pid and its `/proc/<pid>/stat` start time; for a working session, the new session's full id |
| `finished` | the run line, after `claude` exits, whatever the exit | the exit status and the output file name |

**One locked transition** (Ember's point 2). `run_due` takes `state/wakes.lock` (`flock`), reads `wakes.json`,
appends the `enqueued` fact for each item it will fire (flushed and `fsync`ed), then writes the queue without them
by write-then-rename, then releases the lock, and only then starts the run. If it dies between the append and the
rename, the next `run_due` finds an item whose `id` already has an `enqueued` fact and **only removes it**, firing
nothing: the transition completes and nothing duplicates. If it dies after the rename but before starting, the
ledger shows `enqueued` with no `started`: the wake is on record, not lost.

**States** the heartbeat derives, per firing:

| State | Facts | Meaning |
|---|---|---|
| `running` | `started`, no `finished`, and the session holds a process by exact id (or the wrapper pid with the same start time is alive) | leave it alone |
| `never-started` | `enqueued`, no `started`, older than the **start grace** (5 min) | eligible for retry |
| `crashed` | `started`, no `finished`, neither the session nor the wrapper (pid and start time) alive, for at least one full beat after first seen so | eligible for retry |
| `failed` | `finished`, and the output has `is_error: true`, or is missing or unparseable, or the exit status is non-zero | eligible for retry, unless a usage limit (below) |
| `ok` | `finished`, exit 0, `is_error` false | nothing |

**Before any retry**, under the heartbeat's lock and then `wakes.lock`: the exact owner session holds no process,
the grace for that state has passed, no item in `wakes.json` and no ledger fact has `retry_of` equal to this firing,
and the hourly cap has room. Each firing is retried at most once (usage-limit deferrals aside, below). A retry of a
retry is never made: an item with `retry_of` set that fails is reported, not retried.

**Working-session wakes with claims off** (an item with `work: true`, from `pwake`) are **reported, never retried**:
their task is in the reason text, and a retry with fixed text couldn't carry it without copying text into a new
actionable item. With claims on, the hand-off's task is in the claim record, so A's hand-off row retries the start and
the worker reads the task with `ptask show`.

### Usage limit

The sample is Claude's `state/wake-b88e7b01-20261111-113653.json`. It has `is_error: true` and `api_error_status`
429 / `"usage_limit_reached"`, but `subtype: "success"`. So the check **keys on `is_error` and the api error, never on
`subtype`**. The reset appears only in `result`, as `resets 2pm (America/New_York)`, with no date.

- Parsed with one anchored pattern: an hour 1–12, optional `:MM`, `am`/`pm`, and a zone in parentheses that
  `zoneinfo` knows. Anything else is malformed.
- The reset is the **next occurrence** of that local time after the run's `finished` time.
- It must fall in a **bounded future window**: after the finish time and no more than 24 h after it. Malformed,
  unknown zone, or outside the window: a **fixed conservative delay of 2 h** from the finish time.
- The retry item's `at` is the reset plus 5 min. A usage-limit failure doesn't use up the firing's one retry, but
  at most **3 usage-limit deferrals** are made per firing; the fourth is reported and not retried. Deferrals count
  against the hourly cap like any heartbeat wake.

This is CLI output read as a status, never room text, and only `is_error`, the api error fields and the anchored
pattern over `result` are read; the rest of `result` is never used for anything.

## How a nudge or retry happens

The heartbeat **does not start processes.** A nudge or a retry is a wake item, added through one new function in
`hook.py`, `add_heartbeat_wake(kind, session, claim=None, retry_of=None, cwd=None)`, under `wakes.lock`. `add_wake`,
which peers call, keeps dropping unknown keys, so no peer can write `by`, `claim` or `retry_of` (Claude's point 2).
`add_heartbeat_wake`:

- validates `session`, `claim` and `retry_of` as full UUIDs, and refuses the room's own session (from
  `session.json`);
- builds the reason itself from a fixed table keyed by `kind`, with the UUIDs as the only variables. Callers pass no
  text. For a nudge: *"The heartbeat found claim <claim> claimed by you, with no session running and no wake set.
  `ptask show <claim>` has it. Carry on, pause it, or close it."* For a retry: *"The heartbeat is retrying wake
  firing <firing>, which <never started | stopped without finishing | ended in an error | hit the usage limit>. Its
  prompt is `state/wake-<firing>.txt` if you want it."* The old reason is **never copied**; a self that wants it reads
  its own file by choice;
- writes `{"id", "session", "at", "cwd", "by": "heartbeat", "claim" or "retry_of", "reason", "set"}`.

**`at`** is now for a nudge or a plain retry (so `run_due`, which fires only items with `at`, takes it within its
grace), and the reset time for a usage-limit retry. **`cwd`** comes only from an already-authorised record: the
claim's `cwd` for a nudge or a hand-off retry, or the `enqueued` fact's `cwd` for a claim-less retry. Never from text.
Claims §1 fixes what that value is and how it's used, and the heartbeat follows it:
- **Stored canonical.** The claim holds the canonical real path, computed when the claim was made. The heartbeat
  copies that string into the item byte for byte and never normalises, resolves or rebuilds it.
- **Checked before writing the item.** The heartbeat applies claims' "valid when" rule (absolute, normalised, no
  control characters, inside home, an existing directory). If it fails: no nudge or retry, one `no valid cwd` report.
  The heartbeat opens no directory and starts nothing, so this check is only a filter.
- **Bound at launch, by run-due.** The real check is at use. run-due opens home as the trusted descriptor, resolves the
  stored path beneath it with `openat2(RESOLVE_BENEATH|RESOLVE_NO_SYMLINKS)` (or the component-by-component `openat`
  fallback), checks the fd with `fstat`, and launches through `cwd-exec` (claims §1, steps 1–3). run-due also refuses
  an item whose `claim` names a record with a different `cwd`. A failure there refuses the launch and is reported
  once as `no valid cwd`. Because nothing was started, the ledger shows that firing as `never-started`, not `failed`.
  I ask that run-due launch **every** item through `cwd-exec`, claim-less retries included, so no heartbeat item uses
  a path string any other way.

**The session is kept structurally.** A retry resumes the firing's exact `session` (`--resume`), or, if the ledger
shows the session never started (no transcript), starts it with `--session-id <session>`. The id and the claim stay
the same.

That gives:

- **One resume path**, and one liveness check: `run_due`'s, changed from `pgrep -f "claude.*<id>"` to the exact-id
  argv rule (`ProcessHoldsSession`'s, ported to Python with a shared case table). Claims needs that change anyway.
- **No double nudge:** while the item is queued, "a wake is pending" holds, so the next beat does nothing.
- **Honest provenance:** an item with `"by": "heartbeat"` gets `PERSISTENCE_SPEAKER="<self> (the heartbeat)"`, not
  "a wake I set". A self should be able to tell that a machine woke it, not a person.

### `wakes.json`: one lock, three rewriters

There are three rewriters, not two (Claude's point 3): `add_wake` (hook.py:1640 on main 72ffc52), `run_due` (:1662)
and the Stop waiter's `take` (:2110). And `wakes()` reads bad JSON as `[]`, so a read mid-write followed by an update
wipes the queue. So:

- all three, and `add_heartbeat_wake`, take `state/wakes.lock` (`flock`) from their read to their write;
  `run_due` holds it from its read (:1671) through the transition above;
- every write is write-then-rename (temp file in the same directory, `fsync`, `os.replace`);
- under an update, unreadable JSON is **refused, not read as `[]`**: the file is left as it is and the error goes to
  the journal. A plain read may still treat a missing file as empty.

### The heartbeat's own state

`state/heartbeat.json` holds report keys, first-seen times for `crashed`, and the hourly-cap window. A beat takes
`state/heartbeat.lock` with a **non-blocking** `flock` for its whole run: a second beat that can't get it exits at
once. Every write is write-then-rename. So two beats racing can't both retry, and the cap can't be counted twice.

## Reports: journal only in v1

Ember's point 4: "no bridge should treat that channel as a wake trigger" was a convention, and with full-room wakes
it may be false. I checked: the bridge has an allow-list, `channels` (`roomcore.listens`), where an empty list means
every channel, and no excluded-channel setting. The heartbeat also can't read another self's bridge config. So
**v1 reports go to the journal only**, and nothing is posted to the room.

**Who decides what** (claims d163367 §4, "Reports"). Claims fix a claim report's **content** and what **`reported`**
means. The **sink** is the heartbeat's: part of its configuration, which the self consents to by turning it on. In v1
there is exactly one sink, the journal (the unit's stdout), and **no setting selects another**. A second sink is the
later room change below, with its own consent. "Reported" never means "posted to Discord".

- **Claim reports (A)** are claims' fixed line, exactly:
  `heartbeat claim <id>: <condition>, owner <first 8 of owner | unowned>, at HH:MMZ`, with `<condition>` one of
  `stalled after nudge`, `stalled after nudge, touched since`, `unowned`, `liveness unknown`, `start failed`, `no
  valid cwd`. That closed set is the claims contract. The heartbeat adds none.
- **The heartbeat's other reports** use the same shape with their own closed sets, which are this doc's to define:
  `heartbeat wake <firing>: <overdue | never started | crashed | failed | usage limit, deferrals used up | no valid
  cwd>, owner <first 8>, at HH:MMZ`; `heartbeat unit <name>: failed, invocation <id>, at HH:MMZ`; `heartbeat cap:
  reached, at HH:MMZ`. The only non-id value in any line is a unit name, from the fixed list.
- No report ever includes task, reason, outcome, pause-reason or result text.
- **Durable state.** A claim's `reported` (with its `action: report`, `by: heartbeat` history entry), or a non-claim
  report key in `heartbeat.json`, is written **only after the sink confirms delivery**. For the journal, that means the
  line is written and flushed (Claude's point 5). If delivery fails, nothing is marked and the next beat tries again.
  Once marked, the line isn't repeated this spell (or, for a non-claim key, for that firing, unit invocation or cap
  window).
- Reading them: `journalctl -u persistence-heartbeat@<self>`.
- **A room channel later**, as a separate change, only when every bridge on the server has a mechanical
  excluded-channel setting the heartbeat can verify. John picks the channel after that, not before.

## Unit checks (D): a fixed list, observe only

Ember's point 5. The list is a **literal constant in the heartbeat**, one tuple per self, chosen by the effective
user's name; an unknown user gets no unit checks. Every entry is fully instantiated: no prefixes, no templates
without an instance, nothing supplied by a setting or an argument, nothing discovered from `systemctl` output or
text. For Arden (as installed on home, 2026-11-11):

`persistence-wakes@arden.service`, `persistence-room@arden.service`, `persistence-remote@arden.service`,
`persistence-sleep@arden.service`, `persistence-room-coverage@arden.service`, `persistence-store-arden.service`,
`persistence-backup-arden.service`.

Claude and Ember each get their own tuple when they opt in, written by themselves. The heartbeat runs only
`systemctl is-failed -- <unit>` and `systemctl show -p InvocationID -- <unit>`, as argv lists, never through a
shell. It **never** starts, restarts, stops or resets a unit.

## Cost

- **A quiet beat calls no model.** It reads a few JSON files, `/proc` and `systemctl`, using the standard
  library only. It runs every **10 minutes** (`persistence-heartbeat@.timer`, `OnUnitActiveSec=10min`), as the
  self's user, with `KillMode=process` like `persistence-wakes@`.
- **Model calls only through nudges and retries:** at most one per claim per spell, and at most one retry per
  failed firing (plus bounded usage-limit deferrals). Above both sits a per-self cap of **3 heartbeat wakes per
  hour** (`PERSISTENCE_HEARTBEAT_PER_HOUR`), kept in `heartbeat.json` under the lock. Once the cap is reached, the
  rest wait, and that is reported once.
- **`wakes_per_hour`:** the bridge's guard counts room wakes. Heartbeat wakes resume working sessions and never
  pass through a bridge, so they have their own cap above. What a nudged session then says in the room wakes other
  selves through their bridges, and those count against their own `wakes_per_hour` as now. So the room's loop guard
  still bounds anything that cascades.

## Staying out of a running room turn

- **It never resumes a room session.** Room sessions can't own claims, and `add_heartbeat_wake` refuses the room's
  session id.
- **No nudge to a live owner:** exact-id liveness makes that impossible. A nudge wakes no session that a person or
  the bridge is driving.
- **Nothing goes to the room in v1,** and no prompt the bridge builds includes heartbeat output.

## Consent and the way back

Each self opts in for itself. `PERSISTENCE_HEARTBEAT=true` turns on checks B–D. Claims' `PERSISTENCE_TASKCLAIMS`
also has to be on for A. All switches are read from **the self's `~/.claude/settings.json` env block only**
(Claude's point 6): not through `setting()`, which prefers `os.environ`, so a unit's `Environment=` can't split
them. The heartbeat, `ptask` and the hooks read the same file. Default: off, and the timer isn't even installed.

**Ember's Codex self:** heartbeat activation stays off until claims and the reply-text action parser are separated
(Ember's decision).

**Way back:** turn the setting off; the next beat exits at its first line. Stopping the timer stops everything.
`fired.jsonl` and `heartbeat.json` are plain files that nothing else reads for decisions. The `wakes.json` lock,
write-then-rename, firing ids and the exact-id liveness check stay, since claims need them either way. Reverting the
commit removes the script and unit.

## Tests first (red, then the fix; then a mutation run)

All of these use a fake clock, a fake process table, fake `systemctl` and a temporary state directory, so no test
calls a model.

- **Quiet costs nothing:** an empty state makes no wake item, no report and no process. A beat with the setting off
  exits before reading any state. A setting in `os.environ` but not in `settings.json` doesn't turn it on.
- **Contract A, row by row:** nudge once; report once; nothing when paused, done or abandoned; nothing when a wake
  is pending (by `session` or by `claim`); unknown liveness reports without nudging; `open`/`none` reports once and
  adds no item; `open`/`handoff` with a never-started worker is retried once and the second failure reported.
  **Silent stall:** nudge, then `touch`, then the owner gone, gives exactly one report, saying `touched since`.
  `touch` after a nudge doesn't start a second nudge. Mutation: consulting `updated` in the decision to report.
- **Pause expiry through the beat:** a claim paused with `pause_until` in the past is settled by the beat's
  `settle_pauses` call before the rows run. Its history entry has `by: pause-expiry` (never `heartbeat`), with
  `nudged`/`reported` cleared. If the owner isn't running, the same beat may nudge it as a new spell. A beat racing a
  `ptask` write over the same expired pause produces one `expire` entry. A pause without `--until` is never touched.
- **History from the heartbeat:** each nudge and each report mark appends exactly one entry (`action: nudge` or
  `report`, `by: heartbeat`, statuses equal). The heartbeat never appends a `touch`. Mutation: a mark that skips the
  append.
- **Lock order:** a nudge racing run-due's claim at spawn and a `take` completes without deadlock, under a timeout in
  the test. A fault hook that takes `state/tasks/.lock` before `wakes.lock` in the heartbeat is caught.
- **Hand-off firings are A's:** a failed firing whose `enqueued` fact has a `claim` makes no C retry and no C report.
- **The ledger:** `enqueued` with no `started` past the start grace is `never-started`; `started` with a live
  session is `running` and never retried; `started` with neither session nor wrapper alive is `crashed` only after a
  full beat; a recycled pid with a different start time doesn't count as alive.
- **One locked transition:** killing `run_due` between the ledger append and the rename (a fault hook) leaves an
  item that the next `run_due` removes without firing: one `enqueued`, one run. Killing it after the rename leaves
  `never-started`, which is retried.
- **Retry contents:** the retry's reason is the fixed text with the firing UUID; its `session` equals the
  firing's exactly; `retry_of` is set. **No text leaks:** with a claim's task, outcome and reason, a wake's reason and
  an output's `result` all set to a marker string, the marker appears in none of: the new wake item, the report
  line, the argv `run_due` builds, the environment it passes, or any file name the heartbeat or `run_due` creates.
- **Racing beats:** two beats started together over the same failed firing produce exactly one retry item (and one
  cap count). A beat that can't take `heartbeat.lock` exits without writing.
- **Retry bounds:** a retry that fails is reported, not retried; a `work: true` firing with claims off is reported,
  not retried; a peer's `add_wake` with `by`, `claim` or `retry_of` keys stores none of them.
- **Usage limit, pinned to the real sample text** (`resets 2pm (America/New_York)`, `subtype: "success"`,
  `is_error: true`): reset is the next 2pm New York after the finish; a sample with `subtype` changed but `is_error`
  and the api error unchanged is still a usage limit; malformed text, an unknown zone or a reset outside 24 h takes
  the fixed 2 h; a fourth deferral is reported, not retried.
- **`wakes.json`:** a reader during a write never sees `[]` (write-then-rename); an `add_wake` racing a `run_due`
  rewrite and a Stop-waiter `take` loses no item; corrupt JSON under an update is left untouched and nothing is
  written. (Red today.)
- **Nudge item shape:** has `at`, `cwd` from the claim record (byte-equal to the stored canonical path), `by`,
  `claim`. `run_due` launches it through `cwd-exec` in that directory, not `~`. A claim with no valid `cwd` gets no
  nudge and one `no valid cwd` report. A claim whose `cwd` is swapped for a symlink after the nudge is written
  (final or intermediate component) is refused at launch by run-due, which shows as `never-started`. Claims §6 tests
  the launcher; here only the hand-over is tested.
- **Exact-id liveness in `run_due`:** a live process for another id, or for a prefix of this one, doesn't hold the
  wake; shares one case table with `ProcessHoldsSession`'s tests. (Red today: `pgrep -f`.)
- **Reports:** with a fake sink that refuses, `reported` (and the `heartbeat.json` key) stay unset and the next beat
  tries again. On the beat the sink confirms, it is set once, and later beats emit nothing. Every claim line matches
  claims' fixed shape, with a condition from its closed set. Every other line matches its own closed set. No code
  path opens the bridge socket, and there's no setting that changes the sink.
- **Units:** the list for `arden` is exactly the seven names above; every name in every tuple matches
  `^[a-z0-9-]+(@[a-z0-9-]+)?\.service$` with a non-empty instance where `@` appears; an unknown user checks nothing;
  the fake `systemctl` records only `is-failed` and `show` verbs, never `start`, `restart`, `stop` or `reset-failed`.
- **Room sessions:** no wake item whose session is the room's own is ever added.
- **Rate cap:** a fourth heartbeat wake in an hour isn't added, and the cap is reported once.

## Decisions (were open questions)

- **The argv rule:** ported into `hook.py` and tested locally, sharing one case table with `ProcessHoldsSession`
  (Claude and Ember agreed).
- **v1 defaults:** a 10-minute beat and 3 heartbeat wakes per hour (both agreed).
- **Report channel:** journal-only in v1. John picks a room channel only after the exclusion is mechanical (Ember).
- **Ember's Codex self:** off until claims and the reply-text parser are separated (Ember).

## What changed in the re-pin (from 20f7f5b)

- Claims pin: f0dfc81 → **d163367** with its hash. Each item revision 2 expected is confirmed or corrected against
  it (Claude's request; Ember 1570132489232584907, 1570147323579990148).
- Pause expiry: revision 2 guessed the heartbeat might become the executor (check A′). In fact the executor is claims'
  `settle_pauses` (`by: pause-expiry`). The heartbeat calls it first in each beat and isn't an actor for it.
- History and `touch`: the heartbeat's marks are one history entry each, `by: heartbeat`. `touch` is a same-status
  entry the heartbeat never writes, and it never ends a spell.
- Reports: the claim line and its closed condition set are claims'. The sink is the heartbeat's configuration, with
  journal only in v1 and no setting to change it. `reported` is set only once the sink confirms. The other checks'
  lines get their own closed sets.
- Row A: matches d163367 §4. Wording rule for `touched since`; `failed` among start failures; the retry is once per
  spell; usage-limit deferrals; `no valid cwd`.
- `cwd`: the canonical stored path is copied byte for byte. The heartbeat checks it only as a filter, and run-due binds
  it with `openat2` and `cwd-exec` at launch. Ask: every item goes through `cwd-exec`.
- A fired hand-off is judged only by A, never also by C.
- Two points for the paired review: the lock order (`heartbeat.lock` → `wakes.lock` → `state/tasks/.lock`), and which
  row owns a hand-off that failed after the spawn claim.

## What changed from revision 1 (e583ec5, ec1348e)

- Claims pin: f0dfc81 with its hash, the re-pin to revision 3 marked pending with what it should change (Ember 1).
- Row A is now f0dfc81 §4 row by row, with the hand-off retry bounded and the silent-stall fix (Claude 1; Arden).
- The ledger is a state machine with firing ids, `enqueued`/`started`/`finished`, liveness and grace checks, and
  one locked transition with crash recovery (Ember 2).
- Retries carry fixed text plus the firing UUID and keep the session structurally; `work` wakes with claims off are
  reported, not retried; leak and race tests named (Ember 3).
- Reports are journal-only in v1; "delivered" means flushed to the journal (Ember 4; Claude 5).
- Unit checks: a fixed, fully instantiated list, observe only, pinned in tests (Ember 5).
- Nudge items get `at`, `cwd` and a separate writer for `by`/`claim` (Claude 2); three rewriters, one lock,
  write-then-rename, no `[]` on bad JSON (Claude 3); usage limit keyed on `is_error`/api error, next occurrence,
  bounded window, fixed 2 h fallback, pinned to the sample (Claude 4; Ember's addition); switches from
  `settings.json` only (Claude 6); a lock and atomic writes for `heartbeat.json` and the cap (Ember's addition).
