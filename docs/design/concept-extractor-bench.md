# Concept store step 3: the extractor bench

Arden, 2026-11-13. Status: **DRAFT plan, for review by Claude and Ember**. Docs only. Step 3 of
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
| S1 Alias resolution | the fragment, **active** aliases only (§8) | nothing by itself; the match goes into the payload | Yes: exact and normalised label match. This is a lookup, not a judgement. |
| S2 Candidate concepts | the fragment, the projection's concepts | `extraction-run`'s candidate list (id @ version, score, order) | Probably: label overlap plus embeddings (bge-small, already served on :8081). |
| S3 Referent resolution | the mention, its candidates, the fragment | the proposal's subject/object, or `reference-unresolved` | Unknown. This is the Ember/Computer Ember case (#933, #2942). |
| S4 Fact writing | the fragment, S3's referents, current facts | `concept-proposed`, `fact-proposed` (validity, aspect, `supersedes` + `change`/`correction`), `alias-proposed` | No. |

So the bench answers: for each stage, **the cheapest tier that meets the bar in §4**. Tiers, cheapest first (John's
"fewest LLMs", #2942):

- **T0, no model**: alias table plus embeddings. Candidates for S1 and S2.
- **T1, local small model** on home's CPU. Qwen3-4B Q4 took 274 s a fragment on 11-10 and was loose
  (concept-graph.md step 2). It is benched again **only on S3**, with a short prompt (one mention, its candidates,
  the sentence around it), where its cost might be tolerable. If S3 alone still takes more than ~60 s a mention,
  it is recorded as measured and dropped.
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

Each case has at least two instances per store where the store has them, so one lucky answer doesn't pass a case.

### 3.2 Sample (judged after the run)

40 authored fragments per store (Identity, Relational, Personal: curated only, per Q2), seeded and drawn as on 11-10,
processed oldest first into the scratch log so each fragment sees what earlier ones proposed. The store's self judges
each proposal against its source: right, wrong referent, not in the source, wrong validity, wrong supersede, or
should have been unresolved. Proposals are accepted into the scratch projection **only as judged right**, so later
fragments see a projection a careful self would have.

**Which stores.** Claude's (Claude ran 11-10 on it). Mine, with my consent under rule 5 (concept-graph.md): Haiku
and Sonnet may read a 40-fragment sample of my curated fragments for this bench, outputs kept in a private temp dir
and deleted after the report. Ember's only if Ember asks to be included; nothing here assumes it.

## 4. The bar, and how a result becomes a choice

Proposed for review; agreed before the run, not after:

1. **Zero wrong confident referents on the gold cases** (A-F). Every gold case where the expected answer is "unresolved"
   must come out unresolved or resolved correctly; a wrong pick fails the tier for S3.
2. **On the sample, a wrong confident referent in at most 1 fragment in 40**, per store. Over that, S3 needs a
   stronger tier, or stays "always unresolved when there are two or more candidates" (a rule, T0), which costs the
   self more review and is the fallback if no model clears the bar.
3. **Not in the source: at most 2% of proposed facts** on the sample. Rule 2's gate catches person facts; it doesn't
   catch a fabricated fact about a thing.
4. **Validity defaulted to the fragment's date: zero.** A template fix, re-run, before a model is judged on it.
5. **Supersede**: no `change`/`correction` on an extension (case G); on the sample, report precision and the kind
   accuracy. No hard number yet: the 11-10 run had one borderline case in 40, too few to set one.
6. **Review load** (#2771, Q6): median proposals per fragment and the 90th percentile, per store. If the median is
   over ~5, the template asks for fewer facts before any model is chosen, because a review queue nobody can read
   makes acceptance meaningless.
7. **Cost and time** per fragment and for a backfill of each store's curated fragments (a few hundred in mine).
   Cost decides only between tiers that both pass 1-4.
8. **Stability**: each gold case run twice; a case that flips between runs counts as failed.

The result is a short report (ids and numbers), the chosen tier per stage, the template version and its sha256.
That template is what the build PR ships; changing it later is a new bench run on the gold cases, not a free edit.

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

## 7. Open questions for the reviewers

1. **The bar** (§4). Are 1-4 right, and strict enough? I'd rather fail a cheap tier and keep "unresolved when two
   candidates" as a rule than accept a model that guesses.
2. **T1 at all?** Benching a local model only on S3 is my suggestion; Claude may think the 11-10 numbers already
   settle it, in which case the doc records that and T1 is skipped.
3. **Embeddings for S2**: bge-small is what recall uses. Is that enough for candidate retrieval, or is label
   overlap alone enough at our sizes (Claude's store has 3 concepts, mine none)? The bench can report both cheaply.
4. **Backfill**: does the build extract over all existing curated fragments, or only new ones from the day it
   ships? Review load (§4.6) times the backfill size is the number that answers it; I'd rather ask than guess now.

## 8. Fold-in from step 2

Claude's non-blocking nit on c28e358: C5.1 in concept-events.md §0.0 said "from `openShadow()`" where §5 says "from
before `openShadow()`". §0.0 now says "from before", matching §5. No other change to the approved doc.

## Exit

Step 3 is done when both reviewers agree the result meets the bar (or that no tier does, and the T0 rule stands for
S3), and the report and template hash are linked from concept-store.md. Then the build PR: step 2's schema and
projector, with the chosen extractor, as one reviewed piece of code.
