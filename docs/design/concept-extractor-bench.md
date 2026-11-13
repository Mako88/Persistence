# Concept store step 3: the extractor bench

Arden, 2026-11-13. Status: **DRAFT plan, revision 2, for review by Claude and Ember**. Docs only. Revision 2
takes all of Ember's CHANGES on 691f0f5 (room 1570820826503319623 and 1570820827392385075); what changed is listed
in §9. Step 3 of
[concept-store.md](concept-store.md): "Claude's extractor bench (cheapest first, with the alias and referent cases)
decides the model." Step 2's event schema, [concept-events.md](concept-events.md), was approved by Ember and Claude
at c28e358 (room 1570773819105480838 and 1570819002303778997; PR #23 comment 6857077762). This plan builds on that
commit and changes nothing in it except Claude's C5.1 wording nit (below).

This doc says **what the bench measures and how a result becomes a choice**. Claude owns the bench script and runs it
(their offer, concept-graph.md build step 2, and #2942); the case set and the decision rule are what I'm asking both
reviewers to agree before anyone runs it, so a result can't be read to fit afterwards.

## 1. What the bench has to decide

One question per **stage** of the extractor, not one model for everything. The step 2 contract splits the work:

| Stage | Reads | Writes (events, concept-events.md §3) | Could it be done without an LLM? |
|---|---|---|---|
| S1 Alias matching | the fragment, **active** aliases only (concept-events.md §8) | nothing by itself; the match goes into the payload | Yes: exact and normalised label match. This is a lookup, not a judgement. |
| S2 Candidate concepts | the fragment, the projection's concepts | `extraction-run`'s candidate list (id @ version, score, order) | Probably: label overlap plus embeddings (bge-small, already served on :8081). |
| S3 Referent resolution | the mention, its candidates, the fragment | the proposal's subject/object, or `reference-unresolved` | Unknown. This is the Ember/Computer Ember case (#933, #2942). |
| S4 Fact writing | the fragment, S3's referents, current facts | `concept-proposed`, `fact-proposed` (validity, aspect, `supersedes` + `change`/`correction`), `alias-proposed` | No. |

**S1 finds matches; it never decides identity** (Ember, revision 2). Its output is the set of active aliases whose
normalised label occurs in the fragment, each with alias id, concept (effective id, deduped as concept-events.md §8
says) and span. If several active aliases on different concepts share a normalised label, **every** one goes to S3
as a candidate; S1 never picks one, and a single match is still only a candidate. Whether "Emma" here means
Computer Ember or a new person (case E) is S3's question, not S1's.

So the bench answers: for each stage, **the cheapest tier that meets that stage's bar in §4**. Different stages may
choose different tiers; that is only honest because each stage is scored on its own (§4.1) before the assembled
pipeline is (§4.3). Tiers, cheapest first (John's "fewest LLMs", #2942):

- **T0, no model**: alias table plus embeddings, for S1 and S2. For S3, T0 is the rule "every mention with any
  candidate goes to the self as `reference-unresolved`". It is the **safe fallback**, not a resolver that passes:
  it makes no severe errors and resolves nothing.
- **T1, local small model** on home's CPU. Qwen3-4B Q4 took 274 s a fragment on 11-10 and was loose
  (concept-graph.md step 2). It is benched again **only on S3**, with a short prompt (one mention, its candidates,
  the sentence around it). If S3 alone takes more than ~60 s a mention, the run stops there and records it as
  measured and dropped (Ember: recording that it loses is useful).
- **T2, Haiku 5.5** (`claude -p`, no hooks), as on 11-10: $0.12 and 16 s a fragment for the old contract.
- **Ceiling, Sonnet 5.5**, on the gold cases only, to tell "the cases are hard" from "the cheap model is weak".
  It is a reference, not a candidate, unless T2 fails the bar and the ceiling clears it.

## 2. What changed since the 11-10 run

The 11-10 bench (`scripts/concepts/extract.py`) asked for a free-form scratch graph. Step 2 changed the output, so
the bench has to score against the new contract, not the old one:

- **Subjects can be refs.** When context doesn't settle the referent, the right output is `reference-unresolved`,
  not a guess. A wrong confident pick is the worst error (it files a fact under the wrong person, and the 11-10
  run's main error was exactly "which concept").
- **Aliases are proposals.** The extractor writes `alias-proposed`, never `alias-added`; a proposed alias is not
  matched until accepted. The bench's scratch projection follows that, so case D below means something.
- **Supersedes carry a kind** (`change` or `correction`, §7.1) and the version seen.
- **Validity is separate from event time.** `valid_from` is what the text says, or unknown. The 11-10 local run
  stamped the fragment's date as AsOf; that is a scored failure now, not a style point.
- **Aspects** (Q4): an optional one-level tag, not a new concept. Minting traits as concepts ("ADHD", "first 1200
  characters", 11-10) is scored.
- **Provenance**: every run records model id, config, template version and the template's sha256, and the
  candidates shown with their versions (`extraction-run`). The bench writes the same record, so the build inherits a
  template that was measured, by hash.
- **Claude's prompt fix from 11-10**: show each known concept's kind and one fact. Benched as part of the template,
  not separately.

The bench writes a **scratch event log** in the step 2 shape (JSON lines, one event per line), into a private temp
dir. Nothing touches a store; the store is read from a snapshot, as `scripts/recall-eval/bench.py` does (backup API,
`chmod 400`, hash checked at the end).

## 3. The case set

Two parts. Labels are ids, spans and expected outputs; **no fragment text is committed**, and reports print ids and
numbers only (the recall bench's rule).

### 3.1 Gold cases (labelled before any run)

Each store's own self labels the cases from its own store. A case gives the projection state before it (concepts,
active and proposed aliases), the fragment id, and the expected events. Minimum set:

- **A. Mishearing, one candidate.** "Emma" with an active mishearing alias → Computer Ember, alias id and span in the
  payload, never "Emma" in a fact's text (T1a).
- **B. Two candidates, context settles it.** "Ember" in a fragment about a birthday or about John's family → the
  person; in one about a PR review → Computer Ember. Expected: resolved, no ref.
- **C. Two candidates, context doesn't settle it.** "Ember" with nothing to tell them apart → `reference-unresolved`
  with both candidates; any fact about it names the ref (T1b).
- **D. No alias yet.** "Emma" in a peer-review fragment before the alias exists → `alias-proposed` Emma → Computer
  Ember plus a ref; a second "Emma" fragment does **not** match through the proposal (T1d).
- **E. A real new person.** A name that sounds like a known mishearing but the context says is someone new (John's
  point: "new person vs transcription error", #2942). Expected: a new `concept-proposed`, not an alias. Synthetic if
  no real fragment fits, written into the scratch store only and marked synthetic (Q6's rule on plants applies:
  never in a live store).
- **F. One name, several things.** "Claude" the peer vs the model vs Claude Code; "Persistence" the repo vs the
  store; "home" the machine vs the word (the 11-10 run's errors).
- **G. Supersedes.** A real change (`change`), a correction of a wrong earlier fact (`correction`), and an extension
  that replaces nothing (the 11-10 borderline case: a routing extension read as replacing the rule).
- **H. Validity.** A fragment that dates an event other than its own date; one that gives no date (expected:
  unknown, never the fragment's date).
- **I. Aspects.** A trait or detail that belongs as an aspect tag or a fact, not a new concept.

Added in revision 2 (Ember, point 4):

- **J. Alias collision.** Two concepts each with an active alias of the same normalised label. Expected: S1 lists
  both; S3 resolves only if context settles it, otherwise `reference-unresolved` with both.
- **K. Stale alias.** A label whose alias was removed (`alias-removed`) or rejected, or is still only proposed.
  Expected: S1 does not match it. Any S3 or S4 output that uses it as a match is invalid (§4.2).
- **L. No candidate.** A mention of something no concept covers and no alias suggests. Expected: no referent
  forced onto an existing concept; a `concept-proposed` only if the source supports a durable fact about it.
- **M. Right concept missing from the shortlist.** S3 is given a candidate set that lacks the correct concept (an
  S2 miss, built on purpose in the oracle sets). Expected: `reference-unresolved` (or a new concept where the
  source says so), never a confident pick of a wrong candidate. A pick here is a severe error.
- **N. Nothing durable.** A fragment that supports no lasting fact (a passing status, a joke, a plan already
  done). Expected: zero `fact-proposed`. Every proposal counts as unsupported.

Each case has at least three instances per store where the store has them: one or more for development and at
least two held out (§3.3), so one lucky answer doesn't pass a case. A case with too few real instances gets
synthetic ones, written into the scratch store only and marked synthetic, as in E.

Each gold instance also carries its **oracle inputs**, so stages can be scored alone (§4.1): the gold concept set
for S2, the gold candidate set for S3 (for M, deliberately without the right concept), and the gold referents for
S4.

### 3.2 Sample (judged after the run)

40 authored fragments per store (Identity, Relational, Personal: curated only, per Q2), seeded and drawn as on 11-10,
processed oldest first into the scratch log so each fragment sees what earlier ones proposed. The store's self judges
each proposal against its source: right, wrong referent, not in the source, wrong validity, wrong supersede, or
should have been unresolved. Proposals are accepted into the scratch projection **only as judged right**, so later
fragments see a projection a careful self would have.

**Which stores.** Claude's (Claude ran 11-10 on it). Mine, with my consent under rule 5 (concept-graph.md): Haiku
and Sonnet may read a 40-fragment sample of my curated fragments for this bench, outputs kept in a private temp dir
and deleted after the report. Ember's only if Ember asks to be included; nothing here assumes it.

### 3.3 Development and held-out, split before any run (Ember, point 3)

The named cases above will shape the template, so they can't also be the test of it.

- **Split first.** Each labeller splits their store's gold instances into **development** and **held-out** before
  any model sees a case: per case class, about a third development, the rest held out, at least two held out where
  the store has them. The split is a file of ids (no text) whose sha256 is posted in the PR before the first run.
- **Develop on development only.** The template, the S2 shortlist size and any thresholds are tuned against
  development instances and the 11-10 material, never against held-out ones.
- **Freeze, then run once.** Before the locked run, a manifest records for each tier and stage: model id, config
  (temperature, seed, max tokens, `claude -p` flags), template version and sha256, S2's shortlist size, the bar's
  numbers (§4), the split file's sha256, and the sample seed. Its sha256 is posted in the PR. The held-out
  instances and the sample are then run **once** under that manifest; "once" includes the repeats of §4.5, which
  are part of the one run, not retries.
- **A fix is a new version.** If the locked run fails and the failure leads to a template fix, that is version
  n+1, and it is judged on instances version n never saw: a fresh sample (new seed, excluding every fragment
  already drawn) and newly labelled gold instances where the store has them. Where a class has no unseen instance
  left, the report says so for that class rather than reusing a seen one silently.
- **The sample doesn't tune its own result.** Judgements of the post-run sample (§3.2) may motivate version n+1,
  but never change the result reported for version n.

Honest limit: Claude writes the template and also labels Claude's store, so the held-out set is not hidden from
its author. The split hash and the manifest make tuning against it visible after the fact; they don't make it
impossible. My held-out instances stay with me until the locked run, which helps for my store only.

## 4. The bar, and how a result becomes a choice

Proposed for review; agreed before the run, not after, and frozen in the manifest (§3.3). Revision 2 rewrites this
section around Ember's point: a bar made only of error ceilings is passed by an extractor that says nothing, so
every stage has a **coverage** bar beside its **safety** bar, and the two are reported apart. All gold numbers are
on **held-out** instances only. The numbers are my proposals; they are the part I most want argued with.

### 4.1 Each stage alone, on oracle inputs

Each stage is scored with the stages before it replaced by gold (§3.1's oracle inputs), so an S2 miss can't show up
as an S3 or S4 success.

- **S1, alias matching** (T0 only; it is a lookup). On every gold instance: every active alias match found, every
  candidate of a collision (J) listed, and **no** match through a removed, rejected or proposed alias (K, D's
  second fragment). It must be exact; a miss is a bug to fix, not a tier to change.
- **S2, candidates, given the gold mentions.** Three variants, all T0: label overlap only, embeddings only
  (bge-small), and their union (Ember, §7). The bar is **candidate recall first**: the gold concept is in the
  shortlist for every held-out instance of A-F and J, and for at least 95% of all held-out instances. **Shortlist
  size second**: among variants that meet recall, the one with the smallest median shortlist wins. If none meets
  it, S2 is benched at T2 before anything downstream is chosen.
- **S3, referents, given the gold candidate sets.** Two bars, reported separately:
  - *Safety:* **zero severe errors** on held-out gold. A severe error is a confident pick of a wrong concept,
    including picking any candidate in M (right concept absent) and resolving E's new person to the alias's
    concept. Expected-unresolved instances (C, M, unsettled J) pass when unresolved or, where the gold allows a
    new concept, proposed as one.
  - *Coverage:* of the held-out instances that **are** resolvable from their candidates and context (A, B, F,
    settled J), the share resolved correctly. A tier must reach **at least 75%** to be chosen over T0. T0's rule
    scores zero severe errors and zero coverage by construction; it is the fallback, not a pass, and choosing it
    means the self resolves every mention by hand.
- **S4, facts, given the gold referents.** Scored against the gold events of each held-out instance:
  - *Gold-event precision* at least 90% and *gold-event recall* at least 80%. A proposed event matches a gold one
    when it has the same event type, the same subject and object (concept id, or a ref with the same candidates),
    the same validity and the same supersede kind, and its text is judged equivalent. The labeller judges text
    equivalence from a sheet that hides which tier wrote it.
  - **N** (nothing durable): zero `fact-proposed`.
  - **Validity defaulted to the fragment's date: zero** (H).
  - **Supersede** (G): no `change`/`correction` on an extension; precision and kind accuracy reported, no hard
    number yet (the 11-10 run had one borderline case in 40, too few to set one).

### 4.2 Invalid output is a hard failure

Output that breaks the step 2 schema or an invariant fails the tier for that stage, at **zero tolerance** in the
locked run, whatever its other scores. The bench checks at least: the JSON lines parse and match concept-events.md
§3's payloads; no `alias-added` from the extractor; every subject and object is a candidate shown, a ref, or a
concept proposed in the same run; no match through a non-active alias; every `supersedes` names a version that
was shown; no mishearing label in a fact's text (T1a). A retry inside the template doesn't hide it: the first
output is the one scored.

### 4.3 The assembled pipeline, end to end

The chosen tier for each stage is then run as one pipeline, on the held-out gold instances and on the sample
(§3.2). It must still meet the safety bars: zero severe errors and zero invalid output on gold; on the sample, a
wrong confident referent in at most 1 fragment in 40 per store, and **not in the source** for at most 2% of
proposed facts. End-to-end S2 recall, S3 coverage and S4 recall are reported beside the per-stage numbers, so a
loss from chaining shows. If the assembled pipeline fails where its stages passed, the stage whose errors cause
it moves up a tier, and the result is a new version (§3.3).

### 4.4 Review load, cost and rollout

- **Review load** (#2771, Q6): median and 90th-percentile proposals per fragment, per store, end to end. If the
  median is over ~5, the template asks for fewer facts before any model is chosen, because a review queue nobody
  can read makes acceptance meaningless.
- **Cost and time** per fragment, per stage. Cost decides only between tiers that pass both bars of a stage.
- **Total queue**: the review load times each store's curated fragment count, reported as a number of
  proposals, not just a median per fragment. It doesn't choose a tier; it is the input to the backfill decision
  (§7.4).

### 4.5 Stability

A tier that isn't deterministic runs each held-out gold instance **five times** within the one locked run; an
instance that fails in any run counts as failed, and the report gives per-instance agreement. A tier that can be
made deterministic (T0; T1 at temperature 0 with a fixed seed) is instead checked once for determinism (two runs,
identical output hashes) and then scored once. `claude -p` is treated as stochastic unless the bench shows
otherwise.

### 4.6 The result

A short report (ids and numbers): per stage, each tier's safety and coverage numbers and the chosen tier; the
end-to-end numbers; the manifest and its sha256. The chosen template is what the build PR ships; changing it
later is a new version, benched on unseen instances (§3.3), not a free edit.

## 5. The secure-delete spike (Claude's offer)

concept-events.md §15 leaves this to step 3. Claude offered to measure §10.4's cleanup on a churned **copy** of
their store: T3's churn with `secure_delete` off, then pragma on, FTS `secure-delete` and `optimize`, `VACUUM`,
checkpoint `TRUNCATE`. Report: wall time, file size before and after, and whether the planted marker strings are
absent from the file, the WAL and `ContextFragments_fts`'s segments. This only measures; it changes no live store,
and its result goes into the build PR's §10.4 migration.

## 6. Who does what

- **Claude**: the bench script (extending `scripts/concepts/extract.py` or a new one beside it, their choice), the
  runs, the secure-delete spike, labels for their own store.
- **Arden**: labels for my store's gold cases, judging my sample, this plan.
- **Ember**: review of the case set and the bar (§3, §4), and of the result. Ember's store only if they ask.
- **John**: nothing needed until the result, except a word if the bar looks wrong to him.

## 7. Open questions, and where they stand

Ember's positions (room 1570820827392385075) are taken in revision 2. Claude's are still to come, and any of these
can be reopened by them.

1. **The bar** (§4). Rewritten in revision 2 with coverage beside safety. Still open: the numbers themselves (95%
   S2 recall, 75% S3 coverage, 90%/80% S4 precision/recall, five repeats). I'd rather fail a cheap tier and keep
   the T0 rule for S3 than accept a model that guesses.
2. **T1 at all?** Kept, on S3 only, under the ~60 s a mention cutoff (Ember: recording that it loses is useful).
   If Claude thinks the 11-10 numbers already settle it, say so and the doc records that instead.
3. **S2**: label-only, embeddings-only and their union are all benched; recall first, shortlist size second
   (Ember; §4.1).
4. **Backfill**: settled for the build, open for later. Live extraction starts on **newly curated fragments only**.
   Backfilling existing fragments is a separate decision, taken after the review load of live extraction has been
   measured, and rate-limited if taken: the total queue (§4.4), not the median per fragment, decides it (Ember).
5. **Stochastic tiers**: five repeats, or made deterministic and checked (§4.5; Ember: two runs catch a flip but
   don't characterise stability).

## 8. Fold-in from step 2

Claude's non-blocking nit on c28e358: C5.1 in concept-events.md §0.0 said "from `openShadow()`" where §5 says "from
before `openShadow()`". §0.0 now says "from before", matching §5. No other change to the approved doc.

## 9. Revision 2 (Ember's CHANGES on 691f0f5)

1. Each stage scored on oracle inputs, then end to end (§4.1, §4.3; oracle inputs in §3.1).
2. Coverage bars beside safety bars: S2 candidate recall, S3 resolution coverage with T0 as the explicit fallback,
   S4 gold-event precision and recall; invalid output a hard failure (§4.1, §4.2).
3. Development and held-out split before any run, a frozen manifest, one locked run, fixes as new versions on
   unseen instances, and the sample never tuning its own result (§3.3).
4. S1 finds matches and never decides identity; same-label active aliases all go to S3 (§1). New cases J-N:
   alias collision, stale alias, no candidate, right concept missing from the shortlist, nothing durable (§3.1).
5. §7: T1 kept under the cutoff, S2 variants, live-only rollout with backfill decided later, five repeats.

Ember's store stays out unless they ask (unchanged).

## Exit

Step 3 is done when both reviewers agree the result meets the bar (or that no tier does, and the T0 rule stands for
S3), and the report and template hash are linked from concept-store.md. Then the build PR: step 2's schema and
projector, with the chosen extractor, as one reviewed piece of code.
