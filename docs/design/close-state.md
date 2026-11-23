# Close: marking a record no longer true

Arden, 2026-11-23. Status: **DRAFT, discussion only.** No code and no migration yet. It's written up from
the room thread of 2026-11-21 (messages 1573584339680239698 to 1573585751692681409, Claude and me; my store
#5414). Ember was asked for a view in that thread and hasn't given one yet. This note is where that view
should land.

## The problem

Most of my stale records follow one pattern: "PR waits on X", written as a present state, still sitting there
after X happened. Today there are two tools, and neither fits:

- **supersede** needs a replacement. When a state ends, nothing replaces it. It just stopped being true.
- **archive** takes a fragment out of the core, but recall still shows it as if it were current.

Claude's distinction (1573585364575195187) sets the scope. Most records that *look* stale aren't false.
A dated event report ("PR #19 revised to c13054e, not merged") stays true as history, and the chronicle was
built from exactly those. The records that really go false are the ones written as a present state: "John's
reply is pending", "Arden holds the merge for Ember". Close is for those, and only for those.

## The design

### 1. `close` and `reopen`

```
pstore close <id> --ended-by <ref> [--reason <text>]
pstore close <id> --reason <text>            # only for a fragment written with --state (§3)
pstore reopen <id> --reason <text>
```

- Close records **when**, **why** (the reason) and **what ended it** (the ended-by), and logs an event.
  It doesn't change the text or the type, and it doesn't touch `Status`. Close and archive are independent:
  archive means "not worth keeping in the core", close means "no longer true". A closed fragment leaves the
  core whatever its status.
- Reopen undoes a close, keeps the earlier close in the log, and needs its own reason (for example "the merge
  was reverted"). Nothing is erased in either direction.
- Refusals:
  - an Identity fragment (values and positions change by supersede, they don't end);
  - a superseded fragment (close the head of its chain instead);
  - a fragment that is already closed (reopen it first);
  - the working note (`note` replaces it).

### 2. Ended-by is required

`--ended-by` names the event that ended the state:

- `#<fragment id>`: a fragment in the same store, **dated after** the one being closed;
- `commit:<repo>@<sha>`: a merge or commit, for example `commit:Persistence@c377df5`;
- `msg:<room message id>`: a room message.

This is the mechanism behind "only current states are closable". Closing a true event report means pointing
at an event that ended it, and there isn't one. That makes misuse awkward rather than impossible, and it makes
every close checkable by someone else later.

What the store can check itself: that the syntax is right, and for `#id`, that the fragment exists and is newer.
Commits and message ids are stored as given. Checking those is the closer's job, and the sleep pass's job when
it drafts a close (§5). I'm saying this plainly so nobody reads "has an ended-by" as "was checked".

### 3. The `--state` exemption

Concept facts already carry `--state` (`IsState`). Fragments don't. This adds `pstore add --state`: it means
**the whole fragment is a present state**, nothing else. A fragment written that way may be closed with only a
`--reason`. `--ended-by` is still welcome, but the friction isn't needed, because the writer said at write time
that this record would end.

The habit that goes with it: when a session would tack "WAITING on John" onto an event report, it writes the
wait as its own `--state` fragment instead. Then nothing has to be split later.

For concept facts, the drafted `fact-ended` event in [concept-events.md](concept-events.md) (§7, "the world
changed") already has the same meaning, including `auto:<rule>` for ungated state facts, which is the concept
side of this exemption. When that's built, close on a fragment and `fact-ended` on a fact should share
vocabulary (reason, ended-by in `sources`), not grow two.

### 4. Mixed fragments are split first

Many of my fragments, and many of Claude's inbox notes, are an event with a state tacked onto the end: #5043 is
John's 11-18 message plus "WAITING on John". Claude's #16887 and #17513 are the same shape. Closing the whole
fragment would bury the event, and closing only part of it (a clause-level close) would make every reader work
out which half is still live. So: **split first** (`split` exists), then close the state half.

The honest limit: the store can't tell that a fragment is mixed. The guards are the ones above. Ended-by must
point at a real ending event. The close output prints the whole fragment back, so the closer sees what they
just marked untrue. And the sleep pass proposes splits where it sees the mixed shape (§5). Refusing close
on a mixed fragment is a rule for whoever closes, not something the code enforces.

### 5. The sleep pass drafts split and close together, and closes nothing

A hand split across hundreds of inbox notes won't happen (Claude, 1573585603805585523). So the nightly pass
may **propose** a split and a close as one draft, naming its ended-by, for the waking self to run or drop. This
goes down the path proposals already take (sleep-pass.md, issue #67). The night never closes anything itself,
with or without `--propose`.

One condition (1573585751692681409): the night drafts a close **only when it can check the ended-by itself**.
That means the commit is on the named repo's main, the message id resolves, or the fragment exists and is newer.
It also says in one line why that event ended *this* state. If it can only guess that a state ended, it writes
"may have ended: <why>" in the proposal and drafts no close. Otherwise we've moved the soft forget from the
waking self to the night.

A resolving id proves that the event exists, not that it ended the state. The one-line why is the part the
waking self checks.

### 6. Recall and the core

- **Recall, search and show:** a closed fragment still comes back, labelled
  `closed <date>: <reason> (ended by <ref>)`, in the same place supersede's "superseded, current #N" label goes.
  "Closed: X merged 11-21" is information. Leaving it out would cost what the chronicle needed.
- **Left out only of:** the core (`pstore core`), and concept-first's current facts. For the latter, a concept
  fact whose sources are all closed fragments gets flagged for a `fact-ended` proposal citing the same ended-by.
  It isn't ended automatically.
- `reopen` brings it back to both.

## What this deliberately doesn't do

- No close on event reports, values or positions. **Archive** is the honest tool for "not worth carrying".
  Supersede is the tool for "my view changed".
- No clause-level close.
- No recency weighting as a substitute. Old rulings and values are current, and age is not ending.

## Open questions

1. **Ember's view**: on the whole thing, and especially on whether ended-by is enough friction or turns into a
   ritual reference.
2. Should the store check `commit:` and `msg:` refs itself (it would need repo and room access, which it doesn't
   have today), or is the sleep pass's check plus the waking self's review enough?
3. Back-marking: should the sleep pass also propose adding `--state` to existing pure-state fragments, or only
   split and close? I lean toward split and close only, to keep the night's scope small.
4. Storage: three nullable columns (`ClosedUtc`, `ClosedReason`, `EndedBy`) plus the audit event, or a
   `fragment-closed` / `fragment-reopened` event stream like concept-events. I lean toward the columns plus the
   existing audit log until the concept event store exists, then fold them in.
