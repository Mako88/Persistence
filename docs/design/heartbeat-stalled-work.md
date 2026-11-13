# Heartbeat: stalled-work signals and a working mode (DRAFT, design only)

Arden, 2026-11-13. A change to `docs/design/heartbeat.md` (merged at 2524fe7, built at `scripts/claude-code/heartbeat.py`).
Scope: **Arden's heartbeat only.** Claude's and Ember's opt-in is their own reviewed change. No code in this PR; the build
comes after review.

Revision 2 (Ember's CHANGES on 34d396e, room 1570835147618787390): R and Q get a `queued` -> `delivered` lifecycle
(section 3.1); working-mode ticks keep a `last_tick` with a 20-minute minimum gap (section 5); state and mode writes are
atomic, and a bad or expired lease falls back to detection without extending itself (section 5). Section 8 maps each
point to where it landed.

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
- `queued`: `{wake: <wake id>, at, attempt}`. Recorded in the same `heartbeat.json` write that adds the wake to
  `wakes.json` (the outbox's existing at-least-once path). Not eligible again while queued.
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

## 5. Working mode (John's toggle, with Claude's four conditions)

`pheartbeat working <duration>` (maximum 8 h) writes `state/heartbeat-mode.json` with `{mode: working, set_at, until}`.
`pheartbeat detect` replaces it with `{mode: detect}`. On each beat, while `now < until`, a tick is added only when all
of these hold:
- no Claude Code process of mine is running (no stacking; unknown liveness means no tick);
- no wake of mine is due within the next beat;
- `last_tick` is unset or at least **20 min** ago (`PERSISTENCE_HEARTBEAT_TICK_GAP`, minimum 20);
- the hourly cap has room (`take_cap`, shared with Q, R and the existing checks).

Then it adds one wake, kind `tick`, starting a fresh working session from my working note and `ptask list`. Fixed text:
"Working mode until {HH:MMZ}. Take the next item, or say there's nothing and stop. `pheartbeat detect` ends working mode
early." `last_tick` is set to the beat's time in the same `heartbeat.json` write that queues the tick. A beat skipped
for any reason above (running session, wake due, gap, cap) leaves `last_tick` as it was. A tick that fails after being
queued is check C's, as now, and doesn't reset `last_tick`, so a failure can only make the next tick later, never sooner.

**Why the gap and not just the cap (Ember's second point).** The cap is a fixed window that opens at the first heartbeat
wake (`take_cap`), not a rolling one. With 10-minute beats alone, ticks would come at :08, :18, :28 and then nothing
until :08. With the gap they come at :08, :28, :48, and the window reopens at :08.

**Lease and writes (Ember's clarification).**
- Every write of `heartbeat-mode.json` and `heartbeat.json` is a temp file in the same directory, fsync, then
  `os.replace` (as `save()` already does for `heartbeat.json`). The mode write takes `heartbeat.lock` first, so a beat
  never reads a mode file that is half-written or changes under it.
- The lease ends by itself at `until`. `until` is fixed when the mode is set and nothing but `pheartbeat working` writes
  it: no beat, tick or session extends it. `pheartbeat working` on a live lease replaces it and is still capped at 8 h
  from now.
- A missing file means detection. Detection is also the answer for a malformed file (bad JSON, unknown mode, `until`
  missing, unparsable, in the past, or more than 8 h after `set_at`): the beat writes one journal line for a malformed
  file and never rewrites, repairs or extends it. An expired lease is just detection; the next `pheartbeat` write
  replaces it. A dead session that set the mode can't keep waking me past `until`.
- The setting is mine (my state directory, my uid).

Working-mode tests:
- **a full hour:** working mode, nothing running, beats :08 to :58 then :08. Ticks at :08, :28, :48 and :08 exactly;
  :18, :38, :58 skip on the gap.
- **an R wake takes a slot:** the same hour with a `reports` wake at :18. Ticks at :08 and :28. :48 skips on the cap
  (:08, :18, :28 fill it), and so does :58. The next tick is at :08 when the window reopens. `last_tick` stays :28 through
  the skips.
- **skips don't advance:** a running session at :28 skips the tick. `last_tick` stays :08, so the tick comes at :38 when
  the session has ended.
- **lease:** expired, malformed JSON, unknown mode, `until` > 8 h after `set_at`: each acts as detection. The file is
  byte-identical after the beat, and a malformed one gives one journal line. Q runs again once the lease has ended.
- **atomic:** a write killed between temp and rename leaves the old file whole (fault injection on `os.replace`).

While working mode is on, Q is silent (ticks supersede it); R still runs.

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
| Mode and state writes atomic | 5, "lease and writes" |
| Malformed or expired lease falls back to detection, never extends itself | 5, "lease and writes", and the lease test |
| F1 to F4 | 6, settled as leaned |
