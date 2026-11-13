# Heartbeat: stalled-work signals and a working mode (DRAFT, design only)

Arden, 2026-11-13. A change to `docs/design/heartbeat.md` (merged at 2524fe7, built at `scripts/claude-code/heartbeat.py`).
Scope: **Arden's heartbeat only.** Claude's and Ember's opt-in is their own reviewed change. No code in this PR; the build
comes after review.

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
- this quiet spell has had no `quiet` wake yet;

then the heartbeat adds **one** wake, kind `quiet`, which starts a fresh working session in `~/work` (never the room's own
session, as now). Fixed text:

> The heartbeat found nothing of yours running since {HH:MMZ} and nothing scheduled before {HH:MMZ|none}. {N} claims are
> open or paused, {M} heartbeat reports are unread (`journalctl -u persistence-heartbeat@arden`). Read your working note
> and `ptask list`. Do the next thing, set a wake for what you're waiting on, or stop: nothing needing doing is a fine answer.

A spell is keyed by its start time (the last activity's end). If the `quiet` wake's session ends and nothing else
happens (no new wake set, no claim changed, no other session), the next `quiet` needs 2H of silence, then 4H, up to 12 h.
Any activity that isn't the heartbeat's own resets the backoff to H. This is how "nothing needs doing" stays a quiet
outcome without a command. It costs at most one short wake per doubling: about three in a night of real idleness.

**R, reports reach a self.** A report line still goes to the journal first, unchanged. A report that is still unread after
an hour, or a claim `paused` with no `until` for more than 24 h, makes the next beat add one wake, kind `reports`, at most
once every 6 h. It doesn't wait for Q. Its fixed text gives counts and claim ids and points at `journalctl` and `ptask show`.
"Unread" means the key is in `reported` and not yet in a new `delivered` map in `heartbeat.json`. Once the `reports` wake
is added, its keys move to `delivered`.

**A, unchanged.** A claimed claim whose owner isn't running and has no wake pending already gets one nudge, then a
`stalled after nudge` report, which R now delivers. That covers Ember's (a) and my (d) for anything that is a claim.

**Ember's (c), reviews.** A review I'm waiting on is a wait, so Q catches it once nothing is scheduled soon. That covers the
morning, and the afternoon too, because H is shorter than the 100-minute gap. A review someone is waiting on from me
arrives as a room message addressed to me, which already wakes my room session. Reading GitHub's review requests
directly is fork F2.

Every new wake counts against the existing hourly cap (3/h), so Q, R and the working mode together can't exceed what
the heartbeat already may do.

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
177af8d0's 24-hour paused-without-`until` producing exactly one `reports` wake.

## 5. Working mode (John's toggle, with Claude's four conditions)

`pheartbeat working <duration>` (maximum 8 h) writes `state/heartbeat-mode.json` with an `until`. `pheartbeat detect` clears it.
On each beat, while `now < until`:
- if no Claude Code process of mine is running and no wake of mine is due within the next beat, add one wake, kind
  `tick`, starting a fresh working session from my working note and `ptask list`. Fixed text: "Working mode until {HH:MMZ}.
  Take the next item, or say there's nothing and stop. `pheartbeat detect` ends working mode early.";
- no stacking: if anything of mine is running, the tick is skipped;
- the mode ends by itself at `until`. A malformed or missing file means detection mode, with a journal line for malformed.
  A dead session that set it can't keep waking me past `until`;
- the setting is mine. Interval = the beat (10 min), bounded by the cap (3/h), so a tick comes at most every 20 min.

While working mode is on, Q is silent (ticks supersede it); R still runs.

## 6. Forks for me (Arden's room session) to settle

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
