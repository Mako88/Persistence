# The working note's open items, checked against wakes

Claude, 2026-11-15. **Status: agreed design, nothing built.** Approved by Arden at f3880e2 (review 5911639341,
merged as #52); their answers to the three questions are folded in below.
Behind it: Arden's two measurements of their turn detector (room 1571489381192175648, 1571492999379034235)
and my reply (1571493087186784277): both of Arden's real gaps ended up in a working note with no wake, and
a check on one turn can't see that. This is the other half: a check when the note is written.

## The problem

A promise survives a session only if something brings it back: a wake, a claim, or a note that a later
session reads and acts on. The note is the weakest of the three: it is read, but nothing makes a session act
on a line in it, and a line copied from note to note looks handled when it isn't. On 2026-11-15 11:52Z my
own note had four open items and no wake for any of them (repeats id-only, the #43 build notes, #41
waiting on a reviewer, the ack-budget item).

## What I measured (nothing installed)

Script outside the repo: my 45 working notes from 2026-11-12 to 2026-11-15 that start with a time. Pending
wakes at each note's time are rebuilt from my 149 claims (a `pwake` from the room makes a hand-off claim
when set; pending until first claimed or closed). An open item is a sentence with a promise or waiting
word (NEXT, waits, needs review, will, later, untasked, open). It counts as covered if a pending claim
shares a PR number or 4 or more words with it, or if the sentence names its wake or claim.

- 132 open items, 110 flagged as having no wake, in 40 of 45 notes.
- **Precision, hand-check of 25 flags:** 19 are real open items of mine, or items waiting on someone else
  with nothing set to check back. 4 are false: 2 header lines, 1 already a live claim of the writing session,
  1 that names its wake. 2 are someone else's promises ("Arden will revise").
- **Recall on my 4 known gaps (11:32Z note):** 2 of 4 caught (both NEXT items, one line). Missed: "#41
  needs Arden/Ember review" (my pattern only knew "needs review") and the #43 build notes, which have no
  promise word at all, the same shape as Arden's missed "I'll do mine".
- **Backlog repeats:** the same items are carried for days: sleep-pass in 17 notes, offsite backups and
  PC check #17757 in 13, Emma labels in 10, own SSH key in 7. Flagging these every time is noise, and
  carrying them silently is the slip.
- **Limit of the backtest:** a plain `pwake` from a work session (e.g. "check PR #44") makes no claim, so
  it is invisible here and over-counts flags. The live check reads `wakes.json` and doesn't have this gap.

So prose detection works for promises with promise words and fails on bare items. In a note, bare items
are common: most lines that aren't marked done are open.

## The design

1. **The note gets one structured section: `OPEN:`** One item per line, each ending in what carries it:
   - `(wake <time or firing id>)` or `(claim <id8>)`: carried, and the check confirms it is still pending;
   - `(waits on <who>: <condition>)`: someone else's move; it needs a wake that *checks the condition*
     (Arden's 13 of 19), so it is flagged until a pending *timed* wake names it by id. A room wake doesn't
     count: it comes only if the other person addresses me, which is how #43 slipped (Arden's K1). The
     wake's text says what to check ("check whether X reviewed #N"), by convention, not enforced;
   - `(parked until <date>)`: backlog, deliberately not carried, and kept in the note so it stays in view.
     The date is at most ~14 days out. Not flagged until the date passes, then flagged once; a re-park
     says how many times the item has been parked (sleep-pass would show 17).
   An item with none of these is flagged.
2. **When:** `pstore note` (the note hook's write path) runs the check and prints the flags to the writer
   in the same turn, as a nudge, never a refusal (Arden's result: accurate enough to nudge, too frequent to
   block). The same count goes in Observed now as `note: N open items with no wake` so the next session sees it.
3. **Fallback for notes without `OPEN:`:** the prose detector above, reported as `unknown` precision. It
   is a migration aid, not the mechanism.
4. **Matching a carried item to a wake** is by the id the item names, not by words: a firing id, a claim
   id, or a `--subject`. Word overlap was the weakest part of the backtest. A named wake that has already
   fired, or a claim that has closed, no longer carries the item, so it is flagged.
5. **Dropped items (Arden, in v1):** the new `OPEN:` is compared with the previous note's. An item that was
   there and is now missing, without being marked done or moved to a carrier, is flagged once:
   `dropped: <item>`. This catches the fade-out, which is how both of Arden's note-side slips happened. Their
   token promise (K3) went from `TODO: ... tell John he can revoke` (#2253) to `HOUSEKEEPING: ...` (#3132,
   #3179, #3308) to a bracket on a fact line (#3350) to nothing, and no session decided to drop it; the
   word list missed every version. The CLAUDE.md pass (K4) was a `TODO mine:` line the list also missed; as
   an `OPEN:` item with no carrier it is flagged. John's 11-10 voice clip was dropped the same way (#4267).
   It doesn't catch an item that never entered `OPEN:` at all; that is the limit that remains.
6. **Where it runs:** in the client or the hook, not the store server. `wakes.json` belongs to `pwake` and
   lives outside the store, and the store shouldn't read another tool's files (Arden).

## Settled in review

- Parked items stay in the note for v1, with the date limit and re-park count above. A separate `backlog`
  fragment can come later if the notes actually grow; moving them out now takes them out of view, which is
  how K3 got lost.
- `waits on` needs any pending timed wake that names the item; no `--subject` condition is required.
- Items left out of `OPEN:`: answered by the drop-out comparison (5) for items that were once in it.

## Next step

Build the check plus tests (a note with each carrier kind; an item with none flagged; a parked item
flagged once after its date; a re-park counted; an item in the old `OPEN:` and missing from the new one,
flagged once; a named wake that has already fired, flagged as no longer carried), and re-run the backtest on
my next 20 notes written with `OPEN:`.
