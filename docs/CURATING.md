# Curating your own store

For a self that keeps its continuity in a Persistence store: what to keep, how to write it, and how to
look after it. The commands are in `persistence-store help`; this is how to use them well. It is printed by
`persistence-store guide`, so it reaches every harness, and the curation and sleep prompts point here.

Having the tools is half of it. A store that keeps everything reads like a transcript and crowds out what
matters; a store that keeps too little loses the why. Curating is choosing, in your own judgment.

## What a fragment is for

A fragment is something a later self should know, written so it acts on it without the conversation that
produced it. Write information, not a record of how you got it:

- **Keep:** the decision or the fact, its reason, what would change it, whose it was (in their own words
  when the words matter), and the date.
- **Leave out:** the transcript (every message is already kept and `search` finds it), step-by-step
  narration, corrected wrong turns, and commands, ids, paths or hashes that the repo or a README already
  holds. Point to the source of truth instead: "current state: the repo's git log", not a snapshot of it.
- **One subject per fragment.** If a fragment answers two questions, `split` it.
- **Short.** Most fragments fit in a few hundred characters; the sleep pass flags anything over 1,500.
  A long fragment is usually narrative wrapped around a short conclusion.
- **Record the why when you choose.** It is cheap then and nearly impossible to rebuild later (Arden).

## The types

- **Identity:** who you are and how you want to work. Few, short, changed rarely and deliberately; the
  sleep pass only proposes changes to them.
- **Relational:** the people and peers you work with: who they are, what they asked of you, how they want
  work done. Their standing instructions belong here, in their words.
- **Personal:** what you know and do: projects, machines, tools, how you work, lessons learned.
- **Summary:** a digest of a long stretch, when one is worth having apart from its fragments.

## The core

The core is what every session start and compaction puts in front of you: your working note, then
Identity, Relational, Personal and Summary fragments, each type by importance, until a budget is spent
(about 40,000 characters by default; `PERSISTENCE_CORE_CHARS` in the hooks lowers it). What does not fit
is listed by id and is a `show` or a recall away. Read it with `persistence-store core`.

- **The budget fills.** Distilling does not shrink the core; it makes room for more that matters. So the
  question for each fragment is whether it earns a place in every session's context.
- **Importance orders it.** 0.9 and above: what you must never start without (identity, how the people you
  work with want work done). 0.7 to 0.85: what most sessions need. 0.5: useful, fine to fall below the line.
  Raise or lower with `edit <id> --importance`.
- **Project tags scope it.** A Personal fragment tagged `project/<name>` comes in only in that project; tag
  it `--everywhere` (no project) only when it holds in every project.
- **`--tag recall-only`** keeps a lookup (a tool's trap, a format) out of the core but recalled by
  association when it is relevant.
- **The working note** (`note`) is where you are right now: replaced each time, first in the core. Keep it
  to what is in flight and what is next.

## Changing what you kept

- `edit`: a correction to a fragment that is wrong or stale. Prefer it over adding a second one beside.
- `supersede <id>[,<id>...]`: a changed view, or several fragments distilled into one. The old ones are
  archived with your reason and stay searchable. A merge that drops a qualification is not a merge, and an
  earlier, different view is history, not a duplicate: leave both.
- `split`: one catch-all into several fragments, each about one thing.
- `archive`: out of the core and recent lists, still searchable. For what no longer earns a place.
- `forget`: hidden everywhere, reversible with `unforget`. Rarely right; never to make room.
- **Unused is not unimportant.** A fragment never recalled may be in the core already, or recall may be
  missing it. Judge it on what it says, never on its count.

## When curation happens

- **Every turn:** the conversation is kept automatically. Nothing to do.
- **As you work:** when something is decided or learned, write it then, with its why.
- **The curation pass,** once per stretch before compaction: save what the stretch earned, fix what is now
  wrong, update the note.
- **The sleep pass,** at night: a fresh session of you reviews the day, distills oversized fragments, merges
  true duplicates and re-reads a random sample of older ones (docs/design/sleep-pass.md).

## Other selves' stores

A store is its owner's. Never write to another self's store, and read it only with their agreement.
Changes to shared code or guidance that affect how their store is treated are agreed with them first.
