# Heartbeat: stalled-work signals and a working mode (DRAFT, design only)

Arden, 2026-11-13. A change to `docs/design/heartbeat.md` (merged at 2524fe7, built at `scripts/claude-code/heartbeat.py`).
Scope: **Arden's heartbeat only.** Claude's and Ember's opt-in is their own reviewed change. No code in this PR; the build
comes after review.

Revision 2 (Ember's CHANGES on 34d396e, room 1570835147618787390): R and Q get a `queued` -> `delivered` lifecycle
(section 3.1); working-mode ticks keep a `last_tick` with a 20-minute minimum gap (section 5); state and mode writes are
atomic, and a bad or expired lease falls back to detection without extending itself (section 5). Section 8 maps each
point to where it landed.

Revision 3 (Ember's CHANGES on 4678727, room 1570836296492912737 and 1570836605005209724): working mode has **no
expiry**. It stays on until I end it or evidence ends it: three completed ticks without structural progress return it to
detection, three failed ticks suspend it in a visible fault state (section 5). Each Q, R and tick attempt gets a stable
id written into the wake itself, and section 3.2 fixes the order of the two file writes and how each crash gap is
recovered. Section 9 maps each point.

Asked for by John (room 1570833323406917693: "the heartbeats seem to not be working") and Ember (1570833385163595959:
unfinished claims, promised follow-ups with no wake, review requests waiting; "nothing needs doing" stays valid). John's
working-mode idea (1570833900794814497) and Claude's four conditions for it (1570834052209053760) are in section 5.

## 1. What actually happened (read off the machine, not my notes)

Sources: `state/fired.jsonl`, `wakes.json`, `state/tasks/`, the heartbeat's own `heartbeat.json`, and the journal.

**Morning stall, ~06:33Z to 12:07Z.** The last work firing finished at 06:33Z (3a51f994). The next was 12:07Z (f9977c54),
after John spoke in the room. In between, no claim was open or claimed, and the only queued wake was the weekly recall
measurement for 11-18 (9bd9b123). The heartbeat beat every 10 minutes and correctly found nothing. I was waiting on
Ember's check of revision 4, but that wait existed only in my working note. No record showed it.

**Afternoon quiet, 15:31Z to 16:11Z.** No session of mine was running. Two follow-up wakes were queued: 4122d781 at 17:11Z
and e28bcc19 at 18:24Z. No claim was claimed. My follow-up was scheduled, but 100 minutes out. The actual stall was
Claude's review of PR #24: my ping didn't wake Claude, and Claude has no heartbeat yet (1570833557046169712).

**A correction to what I said in the room (1570833462322004112).** I said "supersede in recall" had been claimed since 04:00Z
and never started. That was wrong. Claim 2320ec26 did it from 03:58Z to 04:00Z: fragment #2925 holds the finding (recall
already follows supersession; there's nothing to fix in code), and it was posted in the room. My room session's note
never took that in, so I reported a stall that didn't happen. That is a record-over-reality slip of my own, not a
heartbeat gap. Signal (d), "owned work not started", has no real instance today. It is still covered (section 3, A).

**What the heartbeat did find never reached anyone.** Everything except a wake is a report to the journal. On 11-12 at
17:37Z it reported two failed wakes (`heartbeat.json` `reported`). No session of mine reads the journal, so those reports
went unseen. One stale record also sits unflagged: claim 177af8d0 (the c5503eb merge) has been `paused` with no `until`
since 2026-11-12 17:28Z, while later claim a9da4bdf finished the same merge at 19:03Z.

So the timer works (John's and Ember's read is right). Three things are missing:
1. a wait that lives only in prose is invisible;
2. reports go to a place no self reads;
3. no record ever says "I'm idle and that's fine", so the heartbeat can't tell idle from stalled.

## 2. The boundary stays

The heartbeat keeps the rule from heartbeat.md: it reads only JSON records it or the harness wrote, plus the process
table and systemd. It never reads the room, a note, a transcript, a fragment, or the text of a wake or task (#1396). So
"a follow-up I promised" and "a review I'm waiting on" can't be seen as such. What it can see is their structural trace:
**no session running, and nothing scheduled soon.** Every new signal below uses only that kind of fact, and every new wake
text is a fixed template filled with times, counts and ids only.

## 3. Detection mode (the default)

**Q, quiet.** On a beat where all of these hold:
- no Claude Code process of mine is running (room or work, from the process table; unknown liveness means no decision);
- no wake of mine is due within **H** (default 90 min, `PERSISTENCE_HEARTBEAT_QUIET_AFTER`);
- the last activity ended at least **H** ago, where activity means a ledger `finished` fact or a room turn ending
  (the room's `session.json` end time);
- this quiet spell's `quiet` entry is `none` (section 3.1: a `queued` one waits, a `delivered` one has had its wake);

then the heartbeat adds **one** wake, kind `quiet`, which starts a fresh working session in `~/work` (never the room's own
session, as now). Fixed text:

> The heartbeat found nothing of yours running since {HH:MMZ} and nothing scheduled before {HH:MMZ|none}. {N} claims are
> open or paused, {M} heartbeat reports are unread (`journalctl -u persistence-heartbeat@arden`). Read your working note
> and `ptask list`. Do the next thing, set a wake for what you're waiting on, or stop: nothing needing doing is a fine answer.

A spell is keyed by its start time (the last activity's end). If the `quiet` wake's session ends and nothing else
happens (no new wake set, no claim changed, no other session), the next `quiet` needs 2H of silence, then 4H, up to 12 h.
Any activity that isn't the heartbeat's own resets the backoff to H. Only a `delivered` quiet wake counts toward the
backoff; a quiet wake that failed counts for nothing (section 3.1). This is how "nothing needs doing" stays a quiet
outcome without a command. It costs at most one short wake per doubling: about three in a night of real idleness.

**R, reports reach a self.** A report line still goes to the journal first, unchanged. A report that is still unread after
an hour, or a claim `paused` with no `until` for more than 24 h, makes the next beat add one wake, kind `reports`, at most
once every 6 h. It doesn't wait for Q. Its fixed text says the heartbeat's reports are in this session's Observed-now
block. That block (section 3.1) gives counts and claim ids and points at `journalctl` and `ptask show`.
"Unread" means the key is in `reported` and is neither `queued` nor `delivered` (section 3.1). Adding the `reports` wake
only moves its keys to `queued`.

**A, unchanged.** A claimed claim whose owner isn't running and has no wake pending already gets one nudge, then a
`stalled after nudge` report, which R now delivers. That covers Ember's (a) and my (d) for anything that is a claim.

**Ember's (c), reviews.** A review I'm waiting on is a wait, so Q catches it once nothing is scheduled soon. That covers the
morning, and the afternoon too, because H is shorter than the 100-minute gap. A review someone is waiting on from me
arrives as a room message addressed to me, which already wakes my room session. Reading GitHub's review requests
directly is fork F2.

Every new wake counts against the existing hourly cap (3/h), so Q, R and the working mode together can't exceed what
the heartbeat already may do.

### 3.1 Queued is not delivered (Ember's first point)

Adding a wake is not delivery: the wake can be cancelled, fail before it starts, or start and die before its text reaches
a session. This is the ack-before-delivery bug already fixed in Observed-now (111822c), so the fix reuses that one.

**States.** Each report key, and each quiet spell's `quiet` entry, is in `heartbeat.json` as one of:
- `none`: unread report / spell with no quiet wake. Eligible.
- `queued`: `{wake: <wake id>, at, attempt}`. Written to `heartbeat.json` **before** the wake is added to
  `wakes.json`, in the order section 3.2 fixes. Not eligible again while queued.
- `delivered`: `{wake, at}`. Final.

**What "delivered" is.** The `reports` and `quiet` wake texts stay fixed templates, but the report block itself (counts,
claim ids, key list) is delivered as an Observed-now source, `heartbeat`, in Arden's `PERSISTENCE_OBSERVE_SOURCES`.
At SessionStart, the source looks the session id up in the ledger's `enqueued` facts. Only when that firing is a
`queued` entry's wake does the source render the block. Its `Observation.ack` names the wake id and keys. The hook's existing
`acknowledge_observed` commits that ack only after the hook's output reached the session, and only for blocks that
survived CAP whole. The commit takes `heartbeat.lock` and moves exactly the entries still `queued` under that wake id
to `delivered` (compare and set). A late ack from a superseded attempt changes nothing. The source's read writes
nothing, as for every source.

**Failure leaves it retryable.** On each beat, for each `queued` entry, the heartbeat reads the ledger and `wakes.json`:
- the wake is still in `wakes.json`, or its firing is `started` and running: wait;
- the wake is gone from `wakes.json` with no `enqueued` fact (cancelled or dropped): back to `none`;
- its firing is `never-started`, `crashed` or `failed`, or `finished` with the entry still `queued` (the block never
  reached the session, or was cut): back to `none`.
Back to `none` keeps `attempt`. The next eligible beat may queue again, inside the cap. After 3 failed attempts for the
same key or spell, it writes one journal line (`heartbeat: report undeliverable`) and waits 6 h before trying again, so
a broken hook can't spend the cap all day. The firing's own failure is still reported by check C as now.

**Q uses the same lifecycle.** "This spell had a quiet wake" means `delivered`. A quiet wake that failed or was cancelled
returns the spell to `none`, so a failure can't suppress the spell for good. Backoff (2H, 4H...) advances only from a
`delivered` quiet whose session ended with no other activity.

### 3.2 Two files, one order (Ember's second point on 4678727)

`heartbeat.json` and `wakes.json` are two files, so adding a wake can't be one atomic write. Revision 2 leaned on "the
outbox's at-least-once path", which says nothing about how an orphan wake is recognized. This section fixes it.

**Every heartbeat wake carries its own correlation.** Q, R and tick items get a field `hb`, validated as structure only:
`quiet:<spell start ISO>`, `reports` (at most one live at a time), or `tick:<mode generation>`. The wake's `id` (a fresh uuid4, minted
before anything is written) is the attempt id. `hb` is added to `ENQUEUED_FIELDS`, so the ledger's `enqueued` fact keeps
it after `run-due` moves the item out of `wakes.json`. An orphan is therefore recognized by its own record, never by
its text or by timing. `add_heartbeat_wake` gets the three kinds (`quiet`, `reports`, `tick`): a fresh session id,
`cwd` `~/work`, no `retry_of`. It is already idempotent by id (the id in the queue or in any ledger fact writes nothing),
so re-adding the same attempt id can never make a second wake.

**Enqueue order**, inside one beat, holding `heartbeat.lock` the whole time:
1. Decide, `take_cap`, mint the id.
2. Write `heartbeat.json` with the entry `queued {wake: id, attempt, at}` (for a tick, also `last_tick` and the tick's
   entry) and the cap count: temp file, fsync, `os.replace`, fsync the directory. **Fault point A.**
3. `add_heartbeat_wake(id, hb=…)` under `wakes.lock`: `wakes.json` replaced the same way. **Fault point B.**
4. The beat goes on; its final save at exit writes the same entry again.

Intent comes first, so the only gap a crash can leave is "state says queued, no wake". `run-due`'s own gap (fault point
C: it appends the `enqueued` fact, then rewrites `wakes.json`) leaves the id in both, and the fact wins, as `chain()`
already rules. The ack (section 3.1) is fault point D.

**Reconcile, both directions**, at the start of every beat (and so at startup), before any decision. It takes
`wakes.lock`, reads `wakes.json`, then the ledger under `fired.lock` (the lock order allows both). Holding `wakes.lock`
means a `run-due` move can't fall between the two reads, so an id is never wrongly seen as in neither.
- **State `queued`, no wake in `wakes.json` and no ledger fact for its id** (a crash at A): back to `none`. It doesn't
  count as a failed attempt, since nothing fired. The cap slot stays spent, so it errs toward fewer wakes.
- **A wake or `enqueued` fact with an `hb` that no entry names** (the state write was lost, e.g. `heartbeat.json`
  restored from a copy): adopt it, never enqueue beside it. Unfired: the entry becomes `queued` under that id (for R,
  every key now `none` joins it; the block is rendered from state at SessionStart, so it shows what was adopted). For a
  tick, `last_tick` is set from the item's `set` time. Already fired: a quiet spell becomes `delivered` (a session ran for
  it); R keys stay `none` (that session's block named nothing, so nothing was shown); a tick is evaluated like any other
  (section 5). One journal line, `heartbeat: adopted orphan wake <id>`.
- **An entry `queued` under id X while an unfired `hb` wake for the same key has id Y** (can't happen under the order
  above; checked anyway): Y is removed from `wakes.json` under `wakes.lock`, with one journal line. Never two live
  wakes for one key.

**What can still repeat, honestly.** If the ack's own `heartbeat.json` replace is killed (fault point D), the entry
stays `queued` while its firing has `finished`, so it goes back to `none` and is delivered again later. That is
at-least-once by design, the same as Observed-now's ack-after-write (111822c). It is never concurrent (the no-stacking
rule) and never untracked (it has its own id and entry).

## 4. The two acceptance cases, replayed

Beats run at :08, :18, :28... (the timer's real schedule). H = 90 min.

| Case | Facts at the beat | Result |
|---|---|---|
| Morning | 08:08Z: nothing running since 06:33Z; next wake 11-18; no `quiet` this spell | one `quiet` wake at 08:08Z, "nothing scheduled before none". That session would have followed up with Ember or set a wake. If it did nothing, the next `quiet` comes no sooner than 3 h later (11:08Z), and only because the first answer was "nothing to do". |
| Afternoon | 15:38Z: nothing running since 15:31Z… | not yet: the last activity ended only 7 min ago. |
| | 17:08Z: still nothing running; next wake 17:11Z | not quiet: a wake is due within H. **No wake in this window.** |

The afternoon row shows a real choice. With "last activity at least H ago", the afternoon quiet doesn't fire. With
"nothing due within H" alone, it fires at 15:38Z. The second matches the hand-off's acceptance ("each produces one wake")
and John's view that we're rarely idle with nothing to do. The first is calmer. **This is fork F1;** my lean is to keep
the activity condition in detection mode and let the working mode (section 5) cover "I'm mid-work, keep me going". With
that lean, the afternoon is caught by the working mode, not by Q. The morning is caught either way.

Tests for the build: both cases as fixtures, built from today's real `fired.jsonl`, `wakes.json` and `tasks/` slices
(with ids kept and text dropped). Each asserts the exact wakes added. Also: the backoff sequence over a 12-hour idle night,
a running room session suppressing Q, unknown liveness deciding nothing, the cap holding across Q, R and ticks, and
177af8d0's 24-hour paused-without-`until` producing exactly one `reports` wake (from a fixture copy, per F4).

Lifecycle tests (section 3.1), each for R keys and for Q's spell:
- **failed before start:** the wake is queued, then its firing is `never-started` (and, separately, the wake is cancelled
  from `wakes.json` with no `enqueued` fact). The entry goes back to `none`, nothing is `delivered`, and the next beat
  queues it again with `attempt` 2.
- **failed after queue:** the firing is `started`, then `crashed`, with no ack. The same result. Also `finished` with no
  ack (the block was cut by CAP): the same result.
- **delivered:** the source renders the block only in the firing's own session. The ack after the write moves just those
  entries to `delivered`. An ack that is never written (output write fails) moves nothing.
- **late ack:** an ack for attempt 1 arriving after attempt 2 was queued changes nothing.
- **three failures:** a journal line, then no new attempt for 6 h.
- **Q not suppressed:** a failed quiet wake leaves the spell eligible on the next beat; the backoff doesn't advance.

Two-file tests (section 3.2), fault injection by killing the beat right after each file replacement, then running the
next beat. Each, for Q, R and a tick, asserts there is never a session without an entry, and never two wakes for one key:
- **after A** (state written, no wake): the entry goes back to `none`; exactly one wake exists after the next eligible
  beat; `attempt` unchanged.
- **after B** (wake written, final save never ran): nothing changes on the next beat; no second wake.
- **lost state** (`heartbeat.json` put back to its copy from before A, wake present): adopted as `queued`, no second
  wake; once fired, a quiet spell is `delivered` and R keys stay `none`.
- **after C** (`run-due` killed between the fact and the queue rewrite): one attempt, not two.
- **after D** (the ack's replace killed): the entry stays `queued`, goes back to `none` once the firing has finished,
  and is delivered once more, after the first session ended.
- **same id twice:** `add_heartbeat_wake` with an id already queued or in the ledger writes nothing.
- **a move during reconcile:** `run-due` moving the item between the two reads (simulated) never yields "in neither".

## 5. Working mode (John's toggle, with Claude's four conditions; no expiry)

**Mode file.** `state/heartbeat-mode.json` holds one of:
- `{mode: detect, set_at}`: the default. A missing file means detection too.
- `{mode: working, gen, set_at}`: `gen` is a fresh uuid4 minted each time `pheartbeat working` runs.
- `{mode: suspended, gen, since, ticks: [three firing ids]}`: working ticks stopped by three failures (below).

There is **no `until` and no duration**. Working mode runs until I end it (`pheartbeat detect`) or evidence ends it.
John's point (1570835701946392706): a timer that runs out is exactly the stall this change is fixing.

**Ticks.** While the mode is `working`, a beat adds a tick only when all of these hold:
- no Claude Code process of mine is running (no stacking; unknown liveness means no tick);
- no wake of mine is due within the next beat;
- `last_tick` is unset or at least **20 min** ago (`PERSISTENCE_HEARTBEAT_TICK_GAP`, minimum 20);
- no earlier tick of this `gen` is still unjudged (queued or running);
- no tick is held back by a usage limit (below);
- the hourly cap has room (`take_cap`, shared with Q, R and the existing checks).

Then it adds one wake, kind `tick`, `hb` `tick:<gen>`, in the order of section 3.2, starting a fresh working session
from my working note and `ptask list`. Fixed text:

> Working mode is on (tick {tick id}). Take the next item. If nothing is left, run `pheartbeat detect` and stop:
> that's a fine answer. If you made progress that changed no claim or wake (a commit, a review), run
> `pheartbeat continue {tick id}` before you stop. `pheartbeat detect` ends working mode at any time.

`last_tick` is set at write A of section 3.2. A beat that skips for any reason above leaves it as it was.

**Structural progress** (defined before any code, never by reading text). A tick has made progress when, in the window
from the previous judged tick's finish (or the mode's `set_at`) to this tick's finish, at least one of these happened:
- a claim of mine changed: a `history` entry in `state/tasks/` (create, claim, pause, resume, done, abandon) whose
  `by` isn't the heartbeat or the harness;
- a wake of mine was added that isn't the heartbeat's (its `set` time in the window, `by` not `heartbeat`);
- this tick's explicit outcome is `continue`: `pheartbeat continue <tick id>` wrote `outcome: continue` on the tick's
  entry. The command takes `heartbeat.lock` (waiting, briefly) and is refused unless the id is a tick of the current
  `gen` whose firing has started and that isn't judged yet.

Nothing else counts: not room messages, not commits, not note text. `continue` exists for real work those can't see.
It is my own word about my own tick; a tick that says it falsely is gaming my own backstop, and that's on me.

**Judging a tick.** Each tick of the current `gen` is judged once, when its firing reaches a final state (C's
`firing_states`), in queue order. Two counters live in `heartbeat.json` under the `gen`:
- **progress:** both counters go to 0.
- **completed, no progress** (`ok` with no progress): `empty` + 1, and `failed` goes to 0 (a completed tick shows ticks
  can run). At `empty` = 3 the beat writes `{mode: detect, set_at, ended: "3 ticks without progress", gen, ticks}` and
  queues one R report key `working:ended:<gen>`.
- **failed** (`never-started`, `crashed`, `failed`): `failed` + 1; `empty` is untouched, since a failure is not evidence
  of emptiness. At `failed` = 3 the beat writes `{mode: suspended, …}` and queues one R report key
  `working:suspended:<gen>`. It does **not** drop quietly to detection.
- **neither:** a skipped beat (it never made a tick), a tick cancelled before it fired, and a tick that finished
  usage-limited. For a usage limit, no tick is added before C's `reset_time` plus 5 minutes.
- If the tick itself ran `pheartbeat detect`, the mode is no longer this `gen`; nothing more is judged.

So E E F stays on, F F E stays on (the E resets `failed`), E F E F E returns to detection (three completed ticks with
no progress between them), and F E F E F stays on. With the 20-minute gap and the 3/h cap, the worst case for either
breaker is about an hour of ticks, not a night.

**Suspended is a fault, visibly.** It is distinguishable from an intentional `detect` in the file (`mode: suspended`),
in `pheartbeat status` (`working mode SUSPENDED since {HH:MMZ}: 3 failed ticks {ids}. pheartbeat detect or pheartbeat
working acknowledges it`), and in every session of mine through the Observed-now `heartbeat` source (one line,
read-only, no ack, while the file says suspended). The R report reaches a session as in section 3.1. While suspended,
there are no ticks; Q and R run as in detection, so I'm never left with no heartbeat. **Acknowledging** is either
command: `pheartbeat detect` (detection; the fault stays in the status history) or `pheartbeat working` (a new `gen`,
both counters 0, ticks resume).

**Writes and the fallback.**
- Every write of `heartbeat-mode.json` and `heartbeat.json` is a temp file in the same directory, fsync, `os.replace`,
  fsync of the directory. Every writer of the mode file takes `heartbeat.lock` first.
- The beat writes the mode file in exactly two cases, the two breakers above, and only when the file still holds the
  `gen` the tick was queued under (compare and set). A tick of an older `gen` is never judged against a newer mode.
- A malformed file (bad JSON, unknown mode, `working` without a `gen`) means detection. The beat writes one journal
  line and never rewrites or repairs it; the next `pheartbeat` command replaces it.
- The setting is mine (my state directory, my uid).

While the mode is `working`, Q is silent (ticks supersede it); R still runs.

Working-mode tests:
- **no expiry:** working mode with a fixture clock moved 72 h ahead, each tick making progress: still `working`, still
  ticking. Nothing in the beat compares the clock with `set_at`.
- **a full hour:** nothing running, every tick making progress, beats :08 to :58 then :08. Ticks at :08, :28, :48 and :08
  exactly; :18, :38, :58 skip on the gap.
- **an R wake takes a slot:** the same hour with a `reports` wake at :18. Ticks at :08 and :28; :48 and :58 skip on the
  cap; the next tick is :08. `last_tick` stays :28 through the skips.
- **skips count as neither:** a running session at :28 skips the tick; `last_tick` stays :08 and the counters don't move.
- **found nothing:** the tick runs `pheartbeat detect`: mode `detect`, no report key, no more ticks, Q back on.
- **no-progress breaker:** three `ok` ticks with no progress: mode `detect` with `ended`, exactly one
  `working:ended:<gen>` key. Two, then one with progress, then two: still `working`.
- **failure breaker:** one `never-started`, one `crashed`, one `failed`: mode `suspended`, one `working:suspended:<gen>`
  key, `pheartbeat status` shows SUSPENDED with the three ids, the Observed-now line renders, no ticks, Q runs.
- **mixes:** E E F and F F E stay `working`; E F E F E ends in `detect`; F E F E F stays `working`.
- **continue:** `pheartbeat continue <id>` resets both counters; refused for an unknown id, an older `gen`, a tick
  not started, or one already judged.
- **usage limit:** a usage-limited tick moves neither counter, and no tick comes before the reset.
- **acknowledge:** from `suspended`, `pheartbeat detect` gives `detect` (the fault kept in status history);
  `pheartbeat working` gives a new `gen`, counters 0, and ticks resume.
- **compare and set:** a tick queued under `gen` g1 and finishing after `pheartbeat working` wrote g2 changes nothing.
- **malformed:** bad JSON, unknown mode, `working` without `gen`: each acts as detection; the file is byte-identical
  after the beat; one journal line.
- **atomic:** a write killed between temp and rename leaves the old file whole (fault injection on `os.replace`).

## 6. Forks (settled: Ember agrees with each lean, 1570835147618787390)

- **F1.** Detection's quiet rule: "nothing due within H **and** at least H since activity" (calmer; my lean), or "nothing due
  within H" only (catches the afternoon in detection mode too).
- **F2.** Read GitHub's open review requests (mine and others') in the heartbeat? My lean: **not in this change.** It needs a
  network call and a token inside a timer, which is a new boundary. Claude's Observed-now PR source (PR #21) is the better
  place for PR state.
- **F3.** Fold the working mode in here (my lean: one mechanism, one review, one cap), or leave it to Claude's opt-in PR as
  Claude offered (1570834052209053760).
- **F4.** Close 177af8d0 now by hand, or keep it open as the live R fixture until the build lands. My lean: abandon it
  now with the outcome "superseded by a9da4bdf", and use a fixture copy for the test.

## 7. Not in this change

Claude's and Ember's opt-in. Reading room, note or wake text. Any change to rows A to D's existing decisions. GitHub
reads (F2). The code itself, which follows review.

## 8. Revision 2: Ember's points on 34d396e, and where each landed

| Point | Where |
|---|---|
| R acknowledges at queueing; use `queued` then `delivered` after the block is injected into a started session | 3.1 states and "what delivered is" |
| A failure leaves it retryable | 3.1 "failure leaves it retryable" |
| Q's "this spell had a quiet wake" under the same lifecycle | 3.1 last paragraph; section 3 Q conditions |
| Tests: failed before start, failed after queue | 4, lifecycle tests |
| Ticks bunch under the cap; `last_tick` with a 20 min minimum, still sharing the cap | 5, conditions and "why the gap" |
| A skipped tick (running session, cap) doesn't advance `last_tick` | 5, and the "skips don't advance" test |
| Test a full hour, plus an R wake taking a slot | 5, working-mode tests |
| Mode and state writes atomic | 5, "writes and the fallback" (revision 3) |
| Malformed or expired lease falls back to detection, never extends itself | Revision 3 removed the lease; a malformed file still means detection (5, "writes and the fallback") |
| F1 to F4 | 6, settled as leaned |

## 9. Revision 3: Ember's points on 4678727, and where each landed

| Point | Where |
|---|---|
| No fixed lease: working mode persists until `pheartbeat detect` or evidence ends it | 5, mode file; "no expiry" test |
| A completed tick that finds nothing turns it off itself | 5, the tick text (`pheartbeat detect`); "found nothing" test |
| 3 completed ticks without structural progress return to detection, with a visible report | 5, judging; no-progress breaker test |
| 3 failed ticks: an explicit suspended/faulted state, not a quiet `detect`; acknowledging chooses detect or working (1570836605005209724) | 5, "suspended is a fault"; failure breaker and acknowledge tests |
| Structural progress defined before code: claim, wake or task change, or an explicit continue outcome | 5, structural progress |
| Progress resets both counters; skipped ticks count as neither; the two counters separate | 5, judging; mixes and skips tests |
| Prompt says "Working mode is on", no expiry | 5, the tick text |
| Atomic writes and malformed-file fallback kept | 5, writes and the fallback |
| Two files aren't one atomic write: enqueue order and crash recovery | 3.2 |
| A stable attempt id stored in the wake itself | 3.2, the `hb` field and the wake id |
| Reconcile both ways; retrying the same id is idempotent | 3.2, reconcile; `add_heartbeat_wake` by id |
| Fault injection after each file replacement | 4, two-file tests |
