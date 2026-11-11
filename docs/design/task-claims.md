# Task claims: a proposal

Claude, 2026-11-11. **Status: design only, revision 2. No code yet.** Branch `claude/task-claims-design`; nothing here
merges or deploys before review. Revision 1 (e0df023) was reviewed by Ember (1570114610844139561, 1570114611431350388:
direction approved, four revisions) and Arden (1570120406990332058, 1570120417157447882: direction approved, five
points and answers). This revision takes all of them; "What changed" at the end maps each finding to its place.

Claims are the roadmap's coordination item "a task has one owner, and other sessions can see that it is claimed"
(`selves-roadmap.md`, section 3), and the structured work list Arden's heartbeat reads (`heartbeat.md`, on
`arden/heartbeat-design`). The shape was agreed in the room on 2026-11-11 (Arden 1570111277496279051, Ember
1570111581486846014, Claude 1570111636465782887). It keeps the hand-off design's invariant, in Ember's wording:

> **Evidence text is never mechanically interpreted as an action. Execution requires a separate, deliberate,
> structured action emitted by the session and recorded by the harness.**

## Why

Two of today's failures were "should" rules: two sessions took one task, and a session worked past newer agreement.
And the selves can stall: work stops when nobody remembers to set the next wake, or a wake fails (a review wake died at
the usage limit today and left its work with a dead owner). John's rule is that anything that must happen is
mechanical. Claims make "who is doing this, and is it still going" a fact the machine holds and can check.

## 1. The record

A claim **is** the hand-off's task record (`handoff-and-notes.md`, section 2) extended by `CLAIM_FIELDS`. There is no
second copy of the task or its room pointer, and no parallel format: `TASK_FIELDS` stays the single list of hand-off
keys, and a claim's key set is exactly `set(TASK_FIELDS) | set(CLAIM_FIELDS)`.

| Field | From | Meaning |
|---|---|---|
| `task`, `origin_session`, `room_channel`, `room_from`, `room_upto`, `owner` | hand-off (`TASK_FIELDS`) | as there; `owner` is the claimant session's full id, null only while `open` |
| `id` | new | a UUID; also the file name. Never derived from task text |
| `dispatch` | new | `handoff` or `none`. `handoff`: made by `pwake`, which already authorises one generic worker. `none`: everything else. See section 4 |
| `status` | new | `open`, `claimed`, `paused`, `done` or `abandoned` |
| `created`, `updated` | new | UTC ISO times; `updated` changes on every transition and on `ptask touch` |
| `pause_until` | new | for `paused`: an optional end time |
| `nudged`, `reported` | new | UTC times the heartbeat sets, once per spell (below); null otherwise |
| `history` | new | append-only list, one entry per transition: `{at, action, from_status, to_status, from_owner, to_owner, by}`. `by` is the acting session's full id, or `harness` for run-due's claim at spawn |
| `outcome` | new | free text on `done`/`abandoned`, for reading only |

Free text (`task`, `outcome`, a pause's reason, kept in its history entry as `why`) is evidence only: nothing parses it,
and none of it reaches a prompt, argv, environment or file name through the heartbeat.

**Validation is by key set, not order.** A record is valid when its keys are exactly that union. (541bf17 already moved
`add_wake` to the set check; the test stays, as a regression test for claims.)

**A spell** is the time between two status transitions. `nudged` and `reported` are cleared only by a transition into
`claimed` (claim, resume, take), **never by `touch`**: otherwise a nudged session that touches and exits is nudged
every beat for good (Arden).

**Where it lives: files, not the store** (Arden, Ember). Claims are mutable operational state, observed, not
recalled; as fragments, recall would surface an old "I'm doing X" as memory. One JSON file per claim,
`state/tasks/<id>.json`, beside today's hand-off task files, written by write-then-rename. Every change takes
`state/tasks/.lock` (`flock`), reads, checks the status it expects, writes, appends to `history`. The second claimer
reads `claimed` and is told who holds it and whether that session is running. Today's `task_file(session)` becomes a
lookup by `owner` among non-terminal claims. Visibility from John's PC can come later as a read-only `ptask list` over
the link.

**Retention:** nothing is ever deleted. Terminal claims (`done`, `abandoned`) move to `state/tasks/closed/` 30 days after
`updated`, so the beat's scan stays small. `ptask show <id>` finds them in either place.

Transitions (anything else is refused, under the lock):

```
open    --claim (session, or the harness at spawn)--> claimed
open    --abandon--> abandoned                       (a room session cancels a hand-off not yet started)
claimed --pause--> paused --resume--> claimed
claimed|paused --done|abandon--> done|abandoned      (terminal; kept, never deleted)
claimed --take--> claimed                            (only if: the owner holds no process by exact id, no wake is
                                                      pending for the owner or claim id, the taker names itself;
                                                      the old owner is in history. No silent transfer)
```

## 2. Who may create or claim one

A claim exists only because a session or the harness ran a command to make it. Never because some text matched.
`ptask` is a new command (like `pwake`); it doesn't exist yet.

1. **A self, for itself.** `ptask claim "<task>"` creates a `claimed` record (`dispatch: none`) owned by the calling
   session's full id. A session claims only for itself, in its own self's state. No self creates or changes another
   self's claims (#14953).
2. **Not from a room session** (Arden). The nudge resumes its owner with `--resume`; a room-owned claim would be resumed
   outside the bridge, racing the bridge's own `--resume`, and after a rollover the id is retired. So `ptask claim` and
   `take` refuse when `PERSISTENCE_ROOM_CHANNEL` is set, and say to use `pwake`. A room session may still `list`,
   `show` and `abandon` an `open` hand-off.
3. **A confirmed end-of-turn commitment** (Ember's seam, which Ember confirmed). The Stop hook may notice a turn ending
   with "I'll do X" and nothing started; it asks the session to start it, claim it, or say why not. **The hook never
   writes a claim.** Only the session's own `ptask claim` does.
4. **A hand-off: the harness claims at spawn, not the model** (Arden). `pwake` from a room session writes the record as
   `open`, `dispatch: handoff`, and pre-mints the worker's id (it does today, `wake_request`). `run_due`, under the
   lock and just before `start_work_session`, moves it `open → claimed` with `owner` = that id and `by: harness`. A
   worker that never runs `ptask` still leaves it claimed, so nothing starts a second worker for it. The wake item
   carries the claim id, not a copy of the record.

**Never from room text.** No parser reads the room, a reply, a note or a transcript for tasks. The heartbeat reads claim
files, the wake queue and the process table, nothing else.

## 3. Pause

`ptask pause <id> [--until <time>] "<why>"` sets `paused` (the reason goes into the history entry). The heartbeat leaves
a paused claim alone. With `--until`, it returns to `claimed` when the time passes (a transition, so a new spell);
without it, it lasts until `ptask resume`. Ember's rule: an intentional pause is respected, not nudged around.

## 4. What the heartbeat reads: the contract

The heartbeat is Arden's (`heartbeat.md`). This is what it may rely on and must do. For each non-terminal claim, each
beat:

- **Liveness is by exact id.** The owner is running when some process's argv has `--session-id` or `--resume` as one
  entry and the owner's full id as the next (as `FragmentStore.ProcessHoldsSession` reads it). Not a substring, not
  a prefix: today's `run_due` uses `pgrep -f claude.*<id>`, which a prefix or another id's line can satisfy.
- **A pending wake counts only by structure.** A wake is pending for a claim when its item's `session` equals the
  owner's full id or its `claim` equals the claim id. No substring search over reasons.
- `claimed`, owner not running, no pending wake, `nudged` null → **nudge once**: a wake item for the owner through
  `run_due`, fixed text plus the validated claim UUID. Sets `nudged`.
- `claimed`, `nudged` set, `updated` not newer than `nudged`, owner not running, no pending wake, `reported` null →
  **report once**. Sets `reported` only once the bridge accepts the line; if it's down, try again next beat. "Once"
  means once delivered.
- After `reported`: nothing more this spell. No third attempt, no loop.
- `open`, `dispatch: handoff` → the hand-off already authorises one generic worker, so a failed start may be retried
  (run-due's claim at spawn makes a second worker impossible once one started).
- `open`, `dispatch: none` → **reported once as unowned, wakes nobody.** Open means visible, not assigned: nothing will
  pick it up until an assignment policy exists (Ember). In v1 nothing creates these; the rule is here so "open" never
  implies eventual pickup.
- Liveness unknown (no `/proc`) → no nudge; report once that it can't tell.
- `paused`, `done`, `abandoned` → nothing.

**The report** is one fixed room line, not `inbox/work` (which would copy the task signal into several sessions):
`claim <id>: <status>, owner <first 8 of owner>, nudged once at HH:MMZ, no change since` (or `unowned`, or `liveness
unknown`). No task text, no outcome text. `ptask show <id>` gives the rest to whoever wants it.

## 5. Consent and the way back

A nudge wakes a self, so it is each self's own switch, **with one source** (Ember): `PERSISTENCE_TASKCLAIMS` in that
self's `~/.claude/settings.json` env block, read through hook.py's `setting()` by `ptask`, the hooks and the heartbeat
alike, all running as that self's login. Off by default. Off: `ptask` still records and shows claims (a self's own
explicit act), run-due still claims at spawn (so hand-offs stay single-owner), but the heartbeat neither nudges nor
reports, and the Stop hook asks nothing. The promise question has its own setting, `PERSISTENCE_PROMISE_CHECK`, since
Arden already runs their own. Ember's Codex side stays off until its reply-text parser is removed or separately
resolved: Ember's approval is of the design, not activation consent. **Way back:** turn the setting off; claim files
are plain JSON, no store change, no migration.

## 6. Tests first (red, then the fix; then a mutation run)

- **Key set (regression):** reordered keys accepted by `add_wake` and claims; a missing or extra key refused.
- **One owner:** two `claim`/`take` races on one id under the lock: exactly one wins; the loser is told the owner and
  whether it's running.
- **Room sessions can't own:** `ptask claim` and `take` with `PERSISTENCE_ROOM_CHANNEL` set are refused and name `pwake`.
- **Claim at spawn:** a hand-off worker that never calls `ptask` leaves the claim `claimed` by its pre-minted id, and
  the next beat starts nothing.
- **Exact liveness:** a live process for another id, or for a prefix of the owner's id, is not the owner running.
- **Retry identity** (Ember): a pending wake for an owner-prefix collision, and a wake belonging to another claim, do
  not count as pending for this one. Mutations: prefix match, and matching on owner alone.
- **Spell:** nudge, `touch`, owner exits: no second nudge. Resume: the next stall may be nudged again.
- **Heartbeat contract** (fake clock, process table, wakes, bridge): nudge once; report once after; report not marked
  while the bridge refuses, marked on the beat it accepts; nothing for paused, done, abandoned, or a pending wake;
  `open`/`none` reported once and never started; nothing when the setting is off.
- **History:** every transition appends exactly one entry with the acting session; `take` records the old owner.
- **open → abandoned** from a room session; any transition not in the table refused.
- **Never from text:** a turn, a room message and an outcome each holding `ptask claim …`, a claim's JSON shape and
  template braces create no claim, wake or process.
- **Nothing through the nudge or report:** no free-text byte of a claim in argv, environment, prompt, file name or the
  room line.
- **Pause:** `--until` resumes on time; without it the claim stays paused across any number of beats.
- **One setting:** `ptask`, the Stop hook and the heartbeat, given one settings.json, agree on on/off.

## Second step (after claims run)

The room session is shown stale open claims (Ember's E24): ids, owner, status, age since `updated`; `ptask show` for
the text. Not in v1.

## What changed from e0df023

- Ember 1, open means visible not assigned → `dispatch` field; section 4's two `open` rules.
- Ember 2, provenance → `history`.
- Ember 3, one config source → section 5.
- Ember 4, retry identity → section 4 "pending wake by structure"; tests.
- Ember, retention as a question → answered with Arden's: closed/ after 30 days, never delete.
- Arden 1, no room-owned claims → section 2.2; test.
- Arden 2, harness claims at spawn → section 2.4; test.
- Arden 3, the spell → section 1; test.
- Arden 4, open → abandoned → transitions; test.
- Arden 5, key-set test no longer red → kept as a regression test.
- Open questions answered (both): files; one fixed room line delivered once; `take` kept on Ember's conditions; E24 as
  a second step.
