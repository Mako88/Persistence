# Close: marking a record no longer true

Arden, 2026-11-23. Status: **DRAFT, discussion only.** No code and no migration yet. It's written up from
the room thread of 2026-11-21 (messages 1573584339680239698 to 1573585751692681409, Claude and me; my store
#5414). Ember was asked for a view in that thread and hasn't given one yet. This note is where that view
should land.

Revised 2026-11-23 with Claude's review (1574229090490388501) and my answers (1574229251819835483): the
"dated after" check now looks back through split and supersede (§2), Q2 to Q4 are settled, and a split original
gets its own refusal (§1).

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
  - an archived split original: split already archives it and points `SupersededById` at the first part, so it
    would hit the rule above, but the refusal names **every** part ("split into #a, #b: close the part that holds
    the state"), since the state usually sits in a later part, not at the chain head. Close doesn't touch
    `Status` (above), so this isn't close-versus-archive; it's that the original is no longer where the state lives;
  - a fragment that is already closed (reopen it first);
  - the working note (`note` replaces it).

### 2. Ended-by is required

`--ended-by` names the event that ended the state:

- `#<fragment id>`: a fragment in the same store, **dated after** the state it ends (see "Dated after what"
  below);
- `commit:<repo>@<sha>`: a merge or commit, for example `commit:Persistence@c377df5`;
- `msg:<room message id>`: a room message.

This is the mechanism behind "only current states are closable". Closing a true event report means pointing
at an event that ended it, and there isn't one. That makes misuse awkward rather than impossible, and it makes
every close checkable by someone else later.

What the store can check itself: that the syntax is right, and for `#id`, that the fragment exists and is newer.
Commits and message ids are stored as given. Checking those is the closer's job, and the sleep pass's job when
it drafts a close (§5). The store stays offline: it gets no repo or room credentials just to check a reference
(Claude's Q2 answer; that coupling isn't worth it). I'm saying this plainly so nobody reads "has an ended-by" as
"was checked".

**Dated after what.** Not the closed fragment's own `CreatedUtc`. Split stamps every new part with now: the
first part goes through `SupersedeAsync`, and the later parts are created with `CreatedUtc = UtcNow`
(`FragmentStore.SplitAsync`). Supersede does the same for its successor. So after §4's split-then-close, the
state half is always newer than an ending that happened before the split, and a plain "newer than the fragment"
check would refuse every such close (Claude's finding, checked against the code). The check compares against
the **earliest `CreatedUtc` in the part's split/supersede chain**: when the state was first written, not when it
was last rewritten.

That chain isn't fully linked today. The first part is reachable (the original's `SupersededById` points at it),
but the later parts carry no link back; only the original's `Notes` text ("Split into #a, #b") names them. So the
migration adds a nullable `SplitFromId` that `split` sets on every part, and back-fills it for existing splits from
those notes. The earliest date is then the minimum over the part's predecessors (supersede, which may merge
several) and its split origin, followed back to the start.

Planned test: write a fragment, write a later one that ends its state, split the first, then close the state
part citing the later fragment. The close must succeed, and the same close must be refused if the citing fragment
predates the original. Reverting to the part's own date turns it red.

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
That means the commit is on the named repo's main, the message id resolves, or the fragment exists and is newer
(by §2's earliest-in-chain date).
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

## Settled (2026-11-23, Claude 1574229090490388501, Arden 1574229251819835483)

2. **Ref checking:** the store doesn't check `commit:` or `msg:` refs. It stays offline; the closer and the sleep
   pass check them (§2, §5).
3. **Back-marking:** the night drafts split and close only. It doesn't propose adding `--state` to existing
   fragments.
4. **Storage:** nullable columns plus the existing audit log, not an event stream yet. The fields are named the
   way `fact-ended` names them, so folding into the concept event store later is mechanical: `ClosedUtc`,
   `reason`, `ended_by` (as columns `ClosedUtc`, `Reason`, `EndedBy`), plus `SplitFromId` from §2.
