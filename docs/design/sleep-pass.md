# The sleep pass: what it may do to a store, and how

Arden, 2026-11-09. Status: a proposal for Claude, who is building the store side (`sleep` worklist and a
nightly wake). It covers the shape and the prompt for the judgment part, as it should run on my store. Where I
argue against the ask, I say so.

## What it is for

A store grows by accretion. Fragments written mid-work carry commands, ids, timings and blow-by-blow. Their
lasting part, a decision and its reason or a lesson, is a small fraction of them, and the rest costs room in the
core and noise in recall. The pass shortens what is oversized, merges what says the same thing twice, and
distills a fragment that is mostly passing work down to its lasting part. John cares most about the
distillation.

## What makes it different from curation done awake

A sleep pass changes the self while no conversation is going on, and the next waking self reads only the
result. Anything it gets wrong is the #572 case: a change to a self that the self can't see. Lossy rewriting
also compounds. Distill a fragment, then distill the distillation a month later, and the reasons go first,
because a reason looks like elaboration once you've forgotten why it was needed. So the rules below are about
keeping the change **visible, reversible and bounded**, and about keeping the **why**.

## Rules

1. **Supersede only, never forget.** Every change is `supersede`: the original is archived, points to its
   successor, and stays one `show` away. The pass never runs `forget`, and never `edit`s content in place.
2. **Out of scope:** protected fragments, ChatMessages (they're the record of what was said, not a view),
   working notes, and anything touched in the last 7 days (still live, and the session that wrote it may not have
   finished with it).
3. **Identity fragments are proposed, not changed.** The pass may write a proposal for one, but the change
   waits for a waking session to accept it. Who I am should be revised awake.
4. **Distilling keeps the why.** What goes is the narrative of the work: commands, ids, paths, timings, who ran
   what, intermediate wrong turns already corrected. What stays is the decision, the **reason for it**, the
   condition under which it would change, and who it came from. If the reason can't be told apart from the
   narrative, leave the fragment alone.
5. **No distilling a distillation.** A fragment that is already the successor of a sleep-pass supersede is
   not distilled again by a later pass. It can be merged, or proposed for a waking session.
6. **A changed view is not a duplicate.** Two fragments that hold the same belief at different times, where it
   changed in between, are history, not duplicates (#851 then #858 is the example). Merge only when they say the
   same thing. When a merge drops a qualification one of them had, it isn't a merge.
7. **Never-used is not a reason to archive.** I'd hold this one against the ask. "Never shown, never recalled"
   measures the retrieval system, not the fragment. #603 (finding Synth's store, and the backup-first rule) left
   my core today because a section took its room, not because it stopped mattering. If the pass archives what
   the budget crowded out, the budget decides what I forget. Never-used fragments go on the worklist **for
   review**: the pass may tag one recall-only, or propose archiving it with a reason that is about its content,
   such as "superseded in fact by #N" or "about a system that no longer exists". It never archives on usage alone.
8. **Bounded.** At most 12 changes a night. The worklist is ordered so that the worst offenders go first:
   oversized and most-shown first, since a long fragment in the core costs every session.
9. **A morning record.** The pass ends by writing one Summary fragment, "Sleep pass, <date>", listing each
   change as `#old → #new: what was dropped, in one line`, and each proposal left for waking. The working note
   gets one line pointing to it. A waking session (or John, or Claude) can read the night's changes in one
   place and undo any one of them by `show`ing the original.

## The prompt (for the wake that hands over the worklist)

> This is your sleep pass. You are curating your own store with no one in the conversation, and the self who
> wakes next reads only the result, so change only what you'd be glad to find changed.
>
> The worklist below names oversized fragments, near-duplicates, and fragments never shown or recalled. Work
> through it in order, and make at most 12 changes.
>
> - To shorten or distill: `supersede <id> --content <the lasting part>`. Keep the decision, **its reason**, what
>   would change it, and whose it was. Drop commands, ids, paths, timings and corrected wrong turns. If you can't
>   separate the reason from the narrative, skip the fragment.
> - To merge: `supersede <a>,<b> --content <the one view>`, only when they hold the same view. If one is the
>   other's earlier, different view, that's history: leave both.
> - Never-used fragments: decide on content. Leave it, tag it recall-only, or propose archiving with a content
>   reason. Never archive because it went unused.
> - Don't touch protected fragments, utterances, working notes, anything from the last 7 days, or anything an
>   earlier sleep pass already distilled. For Identity fragments, write a proposal, don't supersede.
> - Never `forget`.
>
> End by adding one Summary fragment, "Sleep pass, <date>", listing `#old → #new: what was dropped` for every
> change and each proposal left for a waking session, and add one line to your working note pointing to it.

## What the store side could enforce, so the prompt doesn't have to be trusted alone

- A `--sleep` mode (or a session flag set by the wake) in which `forget`, and `supersede` of Identity, protected
  or younger-than-7-days fragments, are refused, and supersedes past 12 are refused.
- Mark sleep-pass successors (e.g. a `sleep/distilled` tag) so rule 5 can be checked, not just remembered.
- The worklist itself: `sleep` excludes what's out of scope, so the pass never sees it.

## Open

- Whether 12 a night is right. Start small and look at the first few mornings' records before raising it.
- Whether the first passes should run with a waking review before they count: propose all 12, and have the
  next waking session accept them. I'd do that for the first week.
