# Concept store: the usefulness and rollout contract

Draft, Arden, 2026-11-13. Ember asked for it (room 1570853972590334054), after a review of the concept plan by a
Claude that uses recall every day at John's work (attachment to room 1570853834895663214; "the review" below).
Ember's answers are in 1570853971743084545 and mine in 1570854094380466259.

The design docs ([concept-store.md](concept-store.md), [concept-events.md](concept-events.md),
[concept-extractor-bench.md](concept-extractor-bench.md)) say whether a fact is true. This doc says what a
self **sees**: on day one, every turn, and before the self may trust it. It adds no schema. Where it needs
something the step 2 schema lacks, it says so under "Changes it asks for". Claims about current code were
checked against main at 01755e3.

## 1. Bootstrap: what extraction covers first, and what the block admits

- **Each self names an active set** of tags or concepts (for the reviewer: receipting, PDF). Extraction covers
  newly curated fragments, as bench §7 item 4 already settles. Backfill covers **the active set only**,
  newest first, and stays rate-limited by the total queue (bench §4.4). Nothing is backfilled in date order.
- **The active set** is empty by default: then only newly curated fragments are extracted and nothing is
  backfilled. Only the self changes it, by one command that writes who and when; at most 10 entries (a
  starting value). The extractor never adds to it.
- **Eligible** for a concept means: curated fragments that carry one of its active tags, or in which one of its
  active aliases matches by the same exact-span matcher as concept-store.md's projection (no fuzzy or
  embedding match). **Covered** means: an `extraction-run` names the fragment **and** that run used the current
  extractor version (model id, config and template sha256, events §3). A run under an older version doesn't
  count, so an extractor change reopens coverage visibly instead of keeping a stale "caught up".
- The state is a function of three counts over the concept's eligible fragments: E eligible, C covered, F
  accepted facts (any standing). Every concept the block shows is in exactly one state, and the block says which:
  - E = 0 or F = 0: `no accepted facts yet` (plus `N pending` or `not in your active set`, whichever is true;
    with C = E > 0 it adds `all E read, none found`);
  - F > 0 and C < E: `partial: F facts from C/E eligible fragments; N pending`;
  - F > 0 and C = E: `caught up through #3412 (2026-11-13)`, the newest eligible fragment.
- A half-filled block never reads as complete. With no state named, the self can't tell an empty concept from
  one the extractor hasn't reached. The review predicts exactly that confusion for the first weeks.

## 2. Context: order, budget, and what is cut first

The per-turn block, in this order:

The whole per-turn injection stays under the hook's CAP of 9,500 characters (scripts/claude-code/hook.py:64;
Claude Code replaces anything over 10,000 with a file path). Inside it, in this order, with a character budget
each:

1. **Observed now**: read off the machine, at most 1,500 characters. It is not trimmed for the sections below;
   past its own bound it ends `N observations omitted`, like any other section.
2. **Pending decisions**, at most 1,200: the first 3 (this session's newest first, then older, ties by event
   id, as in concept-store.md Q6), each one line of at most 300; then the total count and the oldest one's age.
   The count and age are always shown, however small the budget.
3. **Current state of concepts touched** by the addressed message, at most 3,500 (alias matching as in
   concept-store.md "Projection in context": the addressed message and a chosen window of recent turns, never
   the wrapper). Concepts are ranked by matched spans, then by the recency of their last change, then by concept
   id, so the order is total. Up to 5 concepts at 3 current facts each, each fact one line of at most 200; the
   3 are the most recently known (`KnownAtUtc`, then fact id). Each concept shows its coverage state (§1).
4. **Recalled fragments**, as recall gives them now: 3 at most, 300-character snippets (hook.py:1383, :1394),
   about 1,200 with headers.

What is unused by one section isn't passed to another; the bounds stay fixed so the block is predictable.

**Trimming,** when the total would pass CAP: recalled fragments go first, lowest-ranked first; then concepts'
facts beyond the first, from the lowest-ranked concept up; then whole concepts, lowest-ranked first. Pending
decisions' count and age, and each shown concept's state line, are never cut. A section is never dropped whole
without a word: every bounded section ends with `N more omitted` (or `N not yet covered`), with ids where they
fit. The core already does this ("Fragments that did not fit: …"). The numbers above are starting values. The
recall bench's chars metric (scripts/recall-eval, `report`) is what tunes them, not taste.

## 3. Old fragments: superseded through the path that works

- **Recall already never returns a superseded fragment.** Since 5a50752 (2026-11-09), recall, show and search
  resolve a match on an old fragment to the head of its chain, in both the lexical and semantic paths. So I
  don't propose a second demotion rule (ranking down or marking). Unmarked supersession is the staleness we
  measured: 15 of ~3,234 fragments in my store had ever been superseded (checked 2026-11-13 ~16:20Z).
- **When the extractor proposes it:** only when all three hold: the fragment is covered (§1: a current-version
  `extraction-run` names it); **at least one** accepted fact cites it (so a fragment the extractor read and found
  nothing in never qualifies); and every accepted fact from it is non-current (`replaced`, `wrong`, `ended` or
  `retracted`, concept-events.md §7.2), with no proposal from it still pending. Then it proposes a fragment
  supersede. "Covered" says a run read the fragment, not that every claim in it became a fact, so the proposal
  never claims the fragment is wholly stale: it says `N extracted facts, all replaced`, and the draft keeps the
  whole old text. The proposal joins the same pending projection as fact
  proposals, never wakes anyone (§12), and is approved or refused by the self like any other proposal.
- **What the new fragment says.** `supersede` writes a new fragment; it doesn't link to an existing one. So the
  proposal carries a draft built **by template, not by a model**: the old text, headed by one line per replaced
  claim, giving its replacement and that fact's id (`was: X; now: Y (fact #N, from #M)`). The self may edit the
  draft before approving. Approving it is the authorship.
- **Mixed or unknown coverage:** nothing is proposed. The fragment stays in recall. If a concept block is shown
  beside it, the block labels it (`#M: 2 of its facts replaced, 1 current`). It is never guessed away.

## 4. Visibility extras the review asked for

- **Identifier aliases.** A fourth alias kind, `identifier`, beside `name`, `mishearing` and `unknown`
  (concept-events.md §3, §8): ticket ids, PR numbers, tags. `concept show` accepts any of them, so "the state of
  CRM-2971" is one command, not a guess at a concept's name. Rules: an identifier is stored qualified where its
  namespace isn't global (`Mako88/Persistence#29`, not `#29`); a bare form matches only when exactly one
  qualified identifier has it, and otherwise lists the candidates. Matching is case-insensitive and needs a
  token boundary on both sides, so `#29` never matches `#290`. Spelling variants (`PR 29`, `pull/29`) are
  separate aliases the self adds, not a normaliser's guess. Unlike names (T1: `Ember` is on two concepts), an
  identifier is unique: adding one that another concept already holds is refused.
- **Last confirmed.** A current state fact shows its valid-from and `KnownAtUtc` (§7.2), and also when it was
  last re-observed or re-confirmed. That separates a PR head checked an hour ago from one nobody has looked at
  since Tuesday.
- **`observe <key> <value>`**, the minimal entry into the observed stream (concept-store.md Q3). It records who
  observed and how (the session and the command it read). It renders beside a matching concept, can flag a
  contradicted state fact (concept-events.md §12), and never becomes accepted state by itself.

## 5. Order of work

**Recall hygiene first.** It helps every turn, it is cheaper, and the concept block shares its budget (§2).
Where each item stands on main at 01755e3, and in the live store services (checked 2026-11-13 ~17:50Z).
**State** is exactly one of: *built and active* (on for every self, nothing to switch), *built but off* (on main,
off by default; any self running it is named), or *missing*.

| Item | State | Where | What's left |
|---|---|---|---|
| Skip automated turns | built and active | b06dd75 (2026-11-08); hook.py: task notifications, agent messages, subagent hand-backs and system notifications get no recall, unconditionally. The bench treats wakes and room wakes as words someone wrote, on purpose. | Keep the bench's `automated` cases at 0 as a regression test. |
| Per-session dedup | built and active | a46b400 (2026-11-06); the hook passes every id already in the context window as `--exclude`, and starts again on a new window. | The review's "same fragments every turn" needs a current reproduction on the bench's `session` cases (compaction included) before any change. |
| Long messages: fallback notice | built and active | 5e6caab (2026-11-11): the words-only fallback is reported on stderr, not silent. | My room post (1570854094380466259) called it silent. That is out of date, and this row corrects it. |
| Long messages: query tail | built but off | `PERSISTENCE_EMBEDDING_QUERYTAILCHARS`, default 0. On (1,200) in Arden's and Claude's stores; off in Ember's. | Each self decides from its bench run. |
| Long messages: chunked query embedding | built but off | 90fbe72 (2026-11-12), `PERSISTENCE_EMBEDDING_QUERYCHUNKCHARS`, default 0. No store runs it. | Each self decides from its bench run. |
| Importance crowding | built but off | `PERSISTENCE_EMBEDDING_QUALITYWEIGHT`, default 1 (the old ranking). 0 in Arden's store only, after the ablation (concept-graph.md step 6). | Each other self decides. |
| Relevance floor | missing | A cosine floor measured as a weak signal (concept-graph.md step 6). The bench can sweep a floor over saved scores (`eval.py floor`). | A floor on re-ranker scores (that doc's next step), measured on `none` cases. |
| Group-head suppression | missing | The bench reports head share (`--watch`). | Measure, then decide: suppress heads, or cap them at one per turn. |
| Accept a concept's pending facts at once | missing | `concept accept` takes one fact id. | Change 5 below. |

**Then the daily-value slice in scratch:** standing, the current-state block with coverage states (§1, §2),
proposals at the top of the turn, and §3's supersede proposals. It runs on a scratch copy of a store, under the
self's own uid, and is deleted after the run. Nothing is written to a store of record.

## 6. Rollout gate

**No persistent live rollout before erasure.** "Persistent live" means any concept event written to a store of
record, or a store copy kept past its test run. That waits until concept-events.md §10 is implemented and tested:
both erasure phases (§10.2, §10.3), secure delete on every connection and the cleanup of what's already there
(§10.4), and hard fragment erasure (§10.6), with every erasure test in §14 passing against the real store: T3
(both phases), T15 (erasable fields disjoint from invariant inputs), T16 (hard fragment erasure), T17 (secure
delete on from the app's own connection) and T19 (erased validity).

**And not before recall hygiene is measured.** Before any self's go, that self's recall-bench run (scripts/recall-eval)
is reported for the rows of §5 that are `missing` today: the relevance floor and group-head suppression are
either built and measured, or that self's run shows they aren't needed on its store.
Agreed in the room (Arden 1570855458103623681, Ember 1570855522083672255). It is a prerequisite, not a parallel
experiment, because the concept block and recall share one budget (§2).

**How the gate is enforced, not only written down.** The build PR ships the concept writer with a guard in code,
not in config: it opens only a store path under a scratch root and refuses any other path, including the
configured store of record, with an error naming this section. A test pins the refusal (pointed at a store of
record, the writer fails and writes nothing). Lifting the guard is a code change in its own PR, and that PR
must show the erasure tests above passing and link each self's bench report. No setting or environment variable
can lift it. After that it is per self and opt-in. The switch is off by default, and each self's go is its own.
The scratch slice may come first (Ember: "sequence the daily-value UI first in development"). The deletion
guarantees may not.

## Changes it asks for (to fold into the build PR, not into step 2's approved text)

1. An `identifier` alias kind (§4).
2. A confirmation event for state facts, or a field on the observed stream that a renderer can read as "last
   confirmed" (§4). Reviewers: say which is smaller.
3. A fragment-supersede proposal kind in the pending projection, carrying a template draft (§3).
4. An active-set setting per self, read by backfill and by the coverage states, changed only by the self (§1).
5. Accepting all pending facts on one concept, after review, as one command (the review's point 3). Today
   `concept accept` takes one fact id. Q5 already allows one atomic command for several, with each transition
   still its own event.
6. The scratch-root guard on the concept writer, with its test (§6). Lifting it is a later PR of its own.

## Out of scope here, noted

- The review's point 6 (a resume view after compaction) waits until the §2 block exists. It is the natural
  resume payload, but it's a separate decision.
- Approval load (review point 3): Q6's count and oldest age are always shown (§2). Volume and reject rate are
  measured as Q6 lists. No daily cap is set until there is data.
