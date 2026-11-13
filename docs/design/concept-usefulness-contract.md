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

- **Each self names a focus set** of tags or concepts (for the reviewer: receipting, PDF). It is called the
  focus set so that "active" keeps its one meaning from the approved schema, an alias's or fact's status
  (concept-events.md §7.2, §8). Extraction covers newly curated fragments, as bench §7 item 4 already settles.
  **Backfill is conditional:** it waits for the decision bench §7 item 4 defers until the review load of live
  extraction has been measured (§4.4's total queue). If and when that decision allows it, backfill covers
  **the focus set only**, newest first, rate-limited by the total queue. Nothing is backfilled in date order.
- **The focus set** is empty by default: then only newly curated fragments are extracted and nothing is
  backfilled. It is **an event, not a setting** (`focus-set-changed`: the whole new set, self only, never the
  extractor or `auto:`), so the coverage states below can be replayed from the log like everything else. At
  most 10 entries (a starting value).
- **Eligible** for a concept means: curated fragments that carry one of its focus tags, or in which one of its
  aliases with status `active` matches by the same exact-span matcher as concept-store.md's projection (no
  fuzzy or embedding match). Template successors (§3) are never eligible. **Covered** means: a **finished**
  `extraction-run` names the fragment, its output was valid (bench §4.2: a run whose output broke the schema or
  an invariant covers nothing), **and** that run used the current extractor version (model id, config and
  template sha256, events §3). A failed run, or a run under an older version, doesn't count, so an extractor
  change reopens coverage visibly instead of keeping a stale "caught up".
- **Eligibility can grow, and that is shown.** Accepting an alias, or adding to the focus set, makes older
  fragments eligible that no run has read. C < E again, so `caught up` drops back to `partial`. That's correct:
  the self learns the concept has unread sources, rather than keeping a "caught up" that no longer holds.
- The state is a **pure function** of three counts over the concept's eligible fragments, all computed from
  the log: E eligible, C covered, F accepted facts (any standing). The rules are tried in this order and the
  first that holds wins, the way §7.2 computes standing, so every concept the block shows is in exactly one
  state:
  1. F = 0: `no accepted facts yet`, plus `N pending` if any proposals wait, `not in your focus set` if it
     isn't, and `all E read, none found` if C = E > 0. (This rule comes first, so a concept whose fragments
     are all covered while every fact is still pending reads as `no accepted facts yet; N pending`, never as
     caught up.)
  2. C < E: `partial: F facts from C/E eligible fragments; N pending`.
  3. Otherwise (F > 0, C = E): `caught up through #3412 (2026-11-13)`, the newest eligible fragment.
- A half-filled block never reads as complete. With no state named, the self can't tell an empty concept from
  one the extractor hasn't reached. The review predicts exactly that confusion for the first weeks.

## 2. Context: order, budget, and what is cut first

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
- **When the extractor proposes it:** only when all three hold: the fragment is covered (§1: a finished,
  valid, current-version `extraction-run` names it); **at least one** accepted fact cites it (so a fragment the
  extractor read and found nothing in never qualifies); and every accepted fact from it has one of the standings
  `replaced`, `wrong`, `ended` or `retracted` (concept-events.md §7.2), with no proposal from it still pending.
  A fact standing `superseded` (a genesis import whose link kind is `unknown`) is **not** in that list: a
  fragment with one gets no proposal until the self classifies the link with `supersede-kind-set` (§7.4).
  Then the extractor proposes a fragment supersede. "Covered" says a run read the fragment, not that every claim
  in it became a fact, so the proposal never claims the fragment is wholly stale: it says `N extracted facts,
  none current`, and the draft keeps the whole old text. The proposal joins the same pending projection as fact
  proposals and never wakes anyone (§12).
- **Approval is the self's, and checks what it saw.** The proposal carries every cited fact as `id@version`.
  Approval is refused, naming what changed, if any of them has moved since (for example `supersede-voided` put a
  fact back to `current`, §7.3). The stale proposal closes, and a new one comes only if the three conditions
  still hold. Only the self approves or refuses: never `auto:<rule>`, whatever a later auto-accept rule allows
  for facts.
- **What the new fragment says.** `supersede` writes a new fragment; it doesn't link to an existing one. So the
  proposal carries a draft built **by template, not by a model**: the old text, headed by one line per extracted
  fact, in the form its standing takes (wording from §7.2's table):

  | Standing | Header line |
  |---|---|
  | `replaced` | `was: X (from A to B); now: Y (fact #N, replaced by #P)` |
  | `wrong` | `was believed: X (claimed from A); shown wrong by #P: Y (fact #N)` |
  | `ended` | `was true from A to B: X (fact #N)` |
  | `retracted` | `no longer stood behind: X (fact #N)` |

  Each draft ends `(from #M)`. The self may edit the draft before approving. Approving it is the authorship.
- **A template successor is never extracted** (Claude's re-extraction loop). The successor keeps the old text
  and is itself a new curated fragment, so an extractor reading it would propose the replaced claims again. The
  exemption is by **provenance, not by parsing the header**: the approval event records the successor's
  fragment id, and §1's eligibility excludes every fragment an approval names. A self that edits the draft to
  add a new claim puts the claim in its own fragment if it wants it extracted; the successor is a record of what
  changed, not a new source. **Test U1:** approve a proposal for #M; the successor is not eligible, the next
  run neither reads it nor proposes any fact from it, and the concept's coverage state doesn't change.
- **Mixed or unknown coverage:** nothing is proposed. The fragment stays in recall. If a concept block is shown
  beside it, the block labels it (`#M: 2 of its facts replaced, 1 current`). It is never guessed away.

## 4. Visibility extras the review asked for

- **Identifier aliases.** A fourth alias kind, `identifier`, beside `name`, `mishearing` and `unknown`
  (concept-events.md §3, §8): ticket ids, PR numbers, tags. `concept show` accepts any of them, so "the state of
  CRM-2971" is one command, not a guess at a concept's name. It follows §8's approval like every other kind: the
  extractor can only write `alias-proposed`, a proposed identifier is never used for matching, and only the self
  (or migration) makes one `active`. Rules: an identifier is stored qualified where its
  namespace isn't global (`Mako88/Persistence#29`, not `#29`, which collides across repos and with fragment
  ids). In the per-turn projection (§2) only the qualified form matches. `concept show` also takes a bare form
  when exactly one qualified identifier has it, and otherwise lists the candidates. Matching is exact-token only
  (no normalising, no fuzzy or embedding match), case-insensitive, and needs a token boundary on both sides, so
  `…#29` never matches `…#290`. Spelling variants (`PR 29`, `pull/29`) are
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
| Skip automated turns | built and active | 0d9f00d (2026-11-06): task notifications and system notifications; b06dd75 (2026-11-08) added agent messages and subagent hand-backs. hook.py gives them no recall, unconditionally. The bench treats wakes and room wakes as words someone wrote, on purpose. | Keep the bench's `automated` cases at 0 as a regression test. |
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
self's own uid, and is deleted after the run (mechanism in §6). Nothing is written to a store of record.

**The two missing hygiene items run in parallel with the scratch slice, and are a prerequisite for the live
budget.** They don't block scratch: a scratch run changes no store of record, and it is where the block's real
size is first measured. They do block any self's live go (§6), because they change how many characters recall
uses, and the concept block's budget (§2) is set against what recall leaves.

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
Agreed in the room (Arden 1570855458103623681, Ember 1570855522083672255): a prerequisite for live, because the
concept block and recall share one budget (§2). For the scratch slice it runs in parallel (§5, Claude's read).

**How the gate is enforced, not only written down.** Two locks in code, neither in config; no setting or
environment variable can lift either.

1. **Scratch-root guard.** The build PR ships the concept writer able to open only a store path under a scratch
   root. Any other path, including the configured store of record, is refused with an error naming this
   section. **Test U2** pins it: pointed at a store of record, the writer fails and writes nothing. Lifting it is
   a code change in its own PR, which must show the erasure tests above passing and link each self's bench
   report.
2. **The applier refuses a store without erasure.** Outside the scratch root, the concept applier opens a store
   only if that store's applied migrations include the §10 erasure migration, and refuses otherwise. This lock
   stays after the guard is lifted, so a store of record that hasn't been migrated can never take a concept
   event. **Test U3 is red on purpose:** against a store-of-record fixture it migrates, runs T3 and T16, then
   writes one concept event and expects it to land. Until §10 exists it fails. It sits in its own test
   category (`RolloutGate`), outside the default run so main stays green, and the build reports it as an
   expected failure. It goes green with T3 and T16 when §10 lands, and the lifting PR moves it into the default
   run.

**Scratch copies don't outlive their run.** Each run copies the store into a fresh temporary directory under the
scratch root and removes it on exit, whether the run passes, fails or throws. **Test U4:** after a passing run
and after one made to throw, the scratch root holds no run directory.

After both locks it is per self and opt-in. The switch is off by default, and each self's go is its own. The
scratch slice may come first (Ember: "sequence the daily-value UI first in development"). The deletion
guarantees may not.

## Changes it asks for (to fold into the build PR, not into step 2's approved text)

1. An `identifier` alias kind (§4).
2. A confirmation event for state facts, or a field on the observed stream that a renderer can read as "last
   confirmed" (§4). Reviewers: say which is smaller.
3. A fragment-supersede proposal kind in the pending projection, carrying a template draft and each cited fact
   as `id@version`; self-only approval (§3).
4. A `focus-set-changed` event per self (the whole new set, self only), read by backfill and by the coverage
   states, so both replay from the log (§1). An event, not a setting.
5. Accepting all pending facts on one concept, after review, as one command (the review's point 3). Today
   `concept accept` takes one fact id. Q5 already allows one atomic command for several, with each transition
   still its own event.
6. The scratch-root guard (U2), the applier's erasure-migration check (U3, red on purpose) and the scratch
   cleanup (U4), §6. Lifting the guard is a later PR of its own.
7. The approval event for a fragment supersede records the successor's fragment id, and eligibility excludes
   it (§3, U1).

## Out of scope here, noted

- The review's point 6 (a resume view after compaction) waits until the §2 block exists. It is the natural
  resume payload, but it's a separate decision.
- Approval load (review point 3): Q6's count and oldest age are always shown (§2). Volume and reject rate are
  measured as Q6 lists. No daily cap is set until there is data.
