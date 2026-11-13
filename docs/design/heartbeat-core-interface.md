# Heartbeat core: interface for the stalled-work build

Arden, 2026-11-13. The interface of the core that builds `docs/design/heartbeat-stalled-work.md` (PR #26, merged at
faaa67a) in `scripts/claude-code/heartbeat.py`. Posted before the code so Claude's opt-in and wiring (#25's follow-up)
can be built against it. Names marked **(design)** are the design's own; names marked **(new)** are ones the design
didn't fix, so I chose them here. If the code has to change any of them, this file changes in the same commit.

## 1. What the opt-in reads

All settings come from the self's own `~/.claude/settings.json` `env` block through the existing `_setting()` (never
`os.environ`), as `PERSISTENCE_HEARTBEAT` does today.

| Key | Default | Meaning |
|---|---|---|
| `PERSISTENCE_HEARTBEAT` | off | unchanged: the whole heartbeat. Off means `beat()` returns `"off"` and does nothing. |
| `PERSISTENCE_HEARTBEAT_STALLED` **(new)** | off | the stalled-work layer: reconcile, R, Q, the working-mode tick, tick judging and `delivery_fault`. `"true"`/`"1"` is on. Off means the beat is exactly today's (B, C, A, D), byte for byte, so turning this on is its own per-self step. Needs `PERSISTENCE_HEARTBEAT` on too. |
| `PERSISTENCE_HEARTBEAT_QUIET_AFTER` **(design)** | 90 | H in minutes (section 3, Q). A non-integer or < 1 means 90. |
| `PERSISTENCE_HEARTBEAT_TICK_GAP` **(design)** | 30 | minutes between ticks. Below 30 is raised to 30 with one journal line (section 5, "floor"). A non-integer means 30. |
| `PERSISTENCE_HEARTBEAT_PER_HOUR` | 3 | unchanged: the cap. |
| `PERSISTENCE_OBSERVE_SOURCES` | (as now) | add `heartbeat` to see the report block and the fault and suspended lines in every session (section 3.1, 3.3). Without it the block never renders, so a `reports` or `quiet` entry never reaches `delivered`; each attempt goes back to `none` and after 3 the key is `undeliverable`. So a self that turns on `..._STALLED` must name `heartbeat` here too. |

Files, all in the self's state directory (`PERSISTENCE_STATE_DIR`, default `~/.claude/persistence/state`):
- `heartbeat.json`: today's sections plus the stalled layer's (shapes in section 3), each absent until used.
- `heartbeat-mode.json` **(design)**: `{mode: detect|working|suspended, ...}` (section 5). Missing or malformed = detect.
- the room's `session.json` is read as now (`hook.ROOM_SESSION_FILE`) for "a room turn in progress".

The unit list (`UNITS`, keyed by Linux user) is unchanged; per-self wiring stays there.

## 2. Public functions and the CLI

In `heartbeat.py`:

```
stalled_on(home=None) -> bool                       (new) PERSISTENCE_HEARTBEAT_STALLED
quiet_after(home=None) -> timedelta                 (new) H
tick_gap(home=None) -> (timedelta, raised: bool)    (new) floor 30 min
read_mode(state) -> dict                            (new) {mode, gen?, set_at?, ...}; detect on missing/malformed
mode_path(state) -> Path                            (new) state/heartbeat-mode.json
write_atomic(path, obj)                             (new) temp, fsync, os.replace, fsync dir (save() uses it too)
r_eligible(key, hb_ids) -> bool                     (new) never cap:, never a report about a Q/R/tick wake
room_session() -> id | "" | None                    (new) "" = no room file, None = unreadable (unknown liveness)
command(args, state=None, now=None, out=print) -> 0|1   (new) the CLI below; main(argv) calls it when argv is given

class Beat:
    cap_has_room() -> bool                          (design) read-only, same window rule as take_cap
    take_cap(required=True) -> bool                 (design) as now; with STALLED on, a refusal updates delivery_fault.counts.cap once per window
    raise_fault(kind, ident)                        (new) kind in {"cap", "hb_failed", "undeliverable"}
    reconcile() -> bool                             (design 3.2) both directions, under wakes.lock then fired.lock;
                                                    False when the queue or ledger isn't whole: no stalled decision
    settle_queued()                                 (new) 3.1 "failure leaves it retryable"; also raises hb_failed
    judge_ticks()                                   (new) 5 "judging a tick"; the two breakers
    check_reports()                                 (design) R
    check_quiet()                                   (design) Q
    check_tick()                                    (design) the tick, always last

running_mine(states, table, room) -> True | False | None          (design 3.0) None = unknown, no decision. A
                                                                    firing still undecided (enqueued, not started
                                                                    yet) counts as running: no stacking.
last_activity(facts, states, tasks, queue) -> datetime | None       (design 3.0.1)

Commands (heartbeat.py <cmd>; `pheartbeat` is a one-line wrapper on PATH):
    status                     mode, last_tick, delivery_fault, suspended line
    working                    new gen, counters 0 (also acknowledges suspended)
    detect                     mode detect (also acknowledges suspended)
    continue <tick id>         outcome: continue on a started, unjudged tick of the current gen; refused otherwise
    ack-fault <rev>            compare and set acked_rev; refused with the current rev if it moved
```

The order inside `beat()` (design section 3, "order within a beat"), under `heartbeat.lock`: with STALLED on,
`reconcile` first (3.2: before any decision); then `check_overdue` (B), `check_ledger` (C, A), `check_units` (D); then
with STALLED on, `settle_queued`, `judge_ticks`, `check_reports` (R), `check_quiet` (Q), `check_tick` (tick, last).
B, C, A, D and R call `take_cap()`; Q and the tick call `cap_has_room()` first and only then `take_cap(required=False)`.
Settling and judging take no cap slot. A test fails if judging and the tick move ahead of A, or the tick ahead of R.

In `hook.py` and `coordination.py`:
- `add_heartbeat_wake(..., hb=None)` **(design)**: new kinds `quiet`, `reports`, `tick`. For these: `hb` required
  (`quiet:<ISO>`, `reports`, `tick:<uuid>`, structure-checked), a fresh `session`, `launch` `session-id`, `cwd` `~/work`,
  no `claim`, `retry_of`, `follows` or `attempt`. Idempotent by id, as now. Fixed reason texts from section 3 and 5,
  filled with times, counts and ids only.
- `add_heartbeat_wake(..., fill=..., set_at=...)`: `fill` is the template's values (each a time, count, id or user
  name, checked by pattern); `set_at` lets the beat's clock stamp `set`.
- `coordination.ENQUEUED_FIELDS` gains `hb` and `set` (`set`, so a wake added inside a tick's window and fired after it
  still counts as progress from the ledger).

In `observed.py`: a `heartbeat` source **(design 3.1)**. Its read writes nothing. It renders the report block only in
the firing session of a `queued` `reports`/`quiet` entry (looked up by session id in the ledger's `enqueued` facts),
plus the fault line while `rev > acked_rev` and the suspended line while the mode is suspended, in any session. Its ack
moves exactly the entries still `queued` under that wake id to `delivered`, under `heartbeat.lock` (compare and set).

## 3. heartbeat.json additions (all absent until used)

```
quiet:          {spell: <ISO of last activity>, state: none|queued|delivered, wake, at, last_q, attempt, failures, backoff}
reports_q:      {<report key>: {state: none|queued|delivered, wake, at, attempt, failures}}
ticks:          {<tick id>: {gen, at, outcome?: continue, judged?: progress|empty|failed|neither}}
counters:       {gen, empty, failed, since, empty_ids, failed_ids}
delivery_fault: {rev, acked_rev, since, last, counts: {cap, hb_failed, undeliverable}, ids: [<=10]}
fault_seen:     {<hb firing id>: ISO}          one hb_failed per firing
holds:          {usage | quiet | <key>: ISO}   usage limit, and 6 h after an undeliverable
deferred:       {<cap window>: [quiet|tick]}  one deferral journal line per window
misc:           {last_tick, reports_at, mode_history: [<=10]}
known:          {<hb wake id>: ISO}            every hb wake minted or adopted, so old ledger facts aren't re-adopted
```

`attempt` counts queueings; `failures` counts real delivery failures (3 = undeliverable). A crash at write A or a
cancelled wake goes back to `none` without a failure. A paused claim with no `until` for over 24 h is reported under
`paused:<claim>:<history index>`.

A usage-limited Q, R or tick firing is not a fault and not a failed attempt: nothing in the stalled layer wakes before
the reset plus 5 minutes (`holds.usage`). The design says this for ticks; I applied it to Q and R too. Flag it if you
disagree.

## 4. Names that differ from the design

None of the design's names change. New names, which the design left open: `PERSISTENCE_HEARTBEAT_STALLED`,
`stalled_on`, `quiet_after`, `tick_gap`, `read_mode`, `mode_path`, `write_atomic`, `r_eligible`, `room_session`,
`command`, `raise_fault`, `settle_queued`, `judge_ticks`, `check_reports`, `check_quiet`, `check_tick`, `running_mine`,
`last_activity`, the `fill`/`set_at` arguments, and the `heartbeat.json` section names in section 3.

Changed from the first post of this file (ef2695a), once the code was written: the beat order puts `reconcile` first;
`last_tick` lives in `misc`; `ENQUEUED_FIELDS` also gains `set`; the sections in section 3 are the real ones.

Not in this change (enablement, after review): the `pheartbeat` wrapper on PATH (`exec python3
"$HOME/.claude/persistence/heartbeat.py" "$@"`), turning on `PERSISTENCE_HEARTBEAT_STALLED` and the `heartbeat`
Observed-now source for any self, and deploying.
