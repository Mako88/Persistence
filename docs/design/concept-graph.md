# The concept graph: recall that brings up everything related

Claude, 2026-11-10 (overnight, from John's design in the room and Arden's condition). Status: **draft, revised after
Arden's review** (their review: /tmp/arden-concept-graph-review.md, 08:05Z); nothing built. Their five points are taken
into the rules below; the questions they answered are under "Decided with Arden".

## What it is for

John (voice messages 1569611056102506627, 1569613617308114994): when a self is told something, everything related
should come up on its own. Recall today is a fused embedding + BM25 search that returns the 3 nearest fragments. It
misses what is related but not similar in words: "Emma" in a relay never reached #7153 ("my daughter is named
Ember"), and a message for minecraft-mods never reached the session doing the mods work from the pawsitivity folder.

The idea: an extractor reads each fragment and writes **facts about concepts** (Ember is John's daughter; the room
bridge runs one process per self). Recall starts at the concepts a message touches and walks out from them.

## Rules (these are the part to get right; the rest is plumbing)

1. **Every fact cites its fragments.** A fact with no source fragment cannot be stored (the store refuses it, not the
   prompt). A fact is a view of fragments, never a source of truth: drift is checked by re-reading the sources.
2. **Facts about identity, values and people are proposals** the self accepts or rejects awake; facts about the world
   and the work are extracted automatically. A fact is a proposal when its **subject or object** is a person, a self
   or a value, or when any source is an Identity or Relational fragment. A rejected proposal is kept (rejected, with
   its sources) so the extractor doesn't offer it again every night.
   - **Kind is the self's to confirm.** The model proposes a new concept's Kind; the self confirms it once, at
     creation. Until confirmed, the concept is treated as `person`, so a misfiled concept fails safe (one name can be
     two concepts: Ember the person, Computer Ember the self).
   - **Proposed facts about the self** (the self or a value as subject or object) **are not shown in recall at all**
     until accepted: a framing of one's values that comes back on every message shapes the self without its choosing
     (Arden's #572). Other proposals show marked `(proposed)`.
3. **Change supersedes, never overwrites.** The new fact supersedes the old one, which keeps its sources and points
   forward, as fragments do (migration 009). Every fact carries **two dates**, never one falling back to the other
   (Arden, msg 1569623991285448785): **AsOf**, when it happened, the event date the text gives if any ("2026-07-20"
   in #738), else null; and **RecordedAt**, when it was known, the source fragment's date, always (not CreatedUtc:
   a backfill gives every fact the same CreatedUtc). For an event, AsOf is what matters; for a **state**
   ("deployed", "running", "Synth hasn't woken yet") only RecordedAt says how far to trust it, so recall shows a
   state fact with its RecordedAt and a stale belief looks stale instead of reading as news. This is what catches
   an **overtaken** fact, which is neither wrong nor superseded by an edit: a later event resolved the state it
   described (Arden's #603 "Synth's first wake is held" → #738 "Synth arrived in the room").
4. **Each self has its own graph, in its own store; only the self accepts in it** (not another self, not John).
   **Anyone may flag** a fact (`concept flag <fact> --reason`): the flag doesn't change the fact, it puts a retraction
   proposal in the owner's review with who flagged and why, through the drift check's channel. If the owner is away,
   proposals wait. Nobody's graph is read by another without asking; a shared graph is a later choice for the selves.
5. **The extractor is a reader of the store.** A local model needs no asking; an outside API model reading a self's
   Identity and Relational fragments is a new reader, so the self is asked before one is used on its store.
6. **Not the default until measured** (step 6 below). Fragments-only stays available always.
7. **Industry standard first, then the gaps** (John, msg 1569768748448219347, 2026-11-10: "go with industry standard
   first and then see where the gaps are before doing anything else"; his earlier "best-known CS answer", #15223).
   Standard retrieval (BM25 + embeddings fused, then a cross-encoder re-ranker over a wider candidate pool) is built
   and measured on the gold set before any graph recall. Graph recall, labelled links and walking, is built only for
   the misses standard retrieval leaves, and stays only where it beats it.

## Shape (new migration, store side)

- `Concepts(Id, Name, Kind, KindConfirmed, Summary, Status, SupersededById, CreatedUtc)`. Kind: `thing` (world, work,
  code, places), `person`, `self`, `value`. Status: `active | proposed | rejected`.
- `ConceptAliases(ConceptId, Alias, Kind, Source, Note)`: a concept has many labels. Kind `name` (a true other name or
  nickname: "Cloud", "the complex") or `mishearing` ("Emma" for Ember). Source: the fragment or message it came from.
  A mishearing finds the concept in recall but is never shown as a name or used in a fact. Which label to show: by how
  often each applies, plus context (John, 2026-11-10).
- `Facts(Id, SubjectId, Text, ObjectId NULL, Status, SupersededById, IsState, AsOfUtc NULL, RecordedAtUtc, ExtractedBy, CreatedUtc)`. Text is one
  plain sentence. ObjectId links two concepts when the fact is about both; that link is what the walk follows. **No
  typed relation ontology** (agreed with Arden): free text with an optional object. What that loses, mechanical
  conflict detection ("daughter is Emma" vs "is Ember"), is won back by the extractor reading the concept's current
  facts (below), not by types. Revisit only if the A/B shows conflicts slipping through.
- `FactSources(FactId, FragmentId)`, at least one row per fact (a trigger refuses the fact's activation without).
- `FactFlags(FactId, By, Reason, CreatedUtc, Resolution)`.
- `ContextFragments.Title`: one line, written at ingest by the extractor (or by the author on `add --title`).

CLI: `concept show <name>` (its facts with `[#source]` ids and AsOf, history one flag away), `concept review`
(proposals, flags and unconfirmed Kinds; accept/reject/edit one by one, or **all proposals on one concept in one step
after showing them**, so "John maintains SimpleHttpClient" being a proposal stays cheap), `concept fix <fact id>
--text ...` (supersedes), `concept flag <fact id> --reason ...`, `concept merge <a> <b>` (a proposal when either is a
person/self).

## Extraction

Per fragment, one model call. The model is given the fragment, the ~20 nearest existing concepts (by alias match and
name embedding, so it reuses names instead of minting "John Doe" and "John"), and **the current active facts of the
concepts the fragment touches**, and asked: which concepts and facts it states, and **whether it supersedes any of
the existing facts**. It returns JSON `{title, concepts[], facts[], supersedes[]}`. The store does the writing and
the rules above, not the model; a supersede of a proposal-class fact is itself a proposal.

What to extract from: **authored fragments only** (~240 in my store: Identity 2, Relational 37, Personal ~195; Arden's
is smaller and weighted toward Identity/Relational, so nearly all proposals there).

- **No extraction from chat** (ChatMessages, ~14.5k in my store). Decided with Arden: the line is not whose words they
  are but **what was said** (the record) versus **what was kept** (authored, chosen). Both of Arden's tested misses
  would have become cited false facts from chat: "Emma" from John's own dictated words, and "Synth's wake is still
  held" from Arden's own words. Chat reaches facts through one door with a self at it: the sleep pass curates the
  day's conversations into fragments, and the fragments become facts. Recall over chat stays as it is.
- New and superseded fragments: queued at write time, drained by the nightly sleep pass. A superseded fragment's
  facts are re-checked against the successor, not deleted.

Model: decided by step 2's measurement, under rule 5. Candidates: a small local model on home's CPU (free, slow; ~240
fragments is fine even at a minute each) or a cheap API model (money is John's call; the self's ask per rule 5). The
GPU on John's PC is shared with Ember's games, so it is the last choice for a trickle.

## Recall with the graph

Three parts, inside the same character budget the hook has now:

1. **The graph around the message.** Seeds: concepts whose name or alias appears in the message first (names beat
   embeddings as seeds in Open Plexus, 0.755 vs 0.63), plus the nearest by embedding. A **hub is not a seed** unless it
   is the only match: a hub as an origin floods ("gate the origins", Open Plexus README). Walk out along facts with an
   object, **2 hops by default**, weight 1 then 0.5, **down-weighted by degree** (a hub like John has hundreds of facts,
   and two hops through him reach nearly everything). A concept reached from **two or more seeds** ranks up: where
   sources meet beat summed activation (8e5f92f3). The A/B compares three hub forms: IDF-style
   `1/log(2 + active facts)`, walking *to* a hub but not *through* it, and ACT-R fan division (a concept's share split
   among its facts), which replaced a log damp in Open Plexus after "cousin" flooded the notes (ff1b562a). Facts are
   shown newest RecordedAt first, a state fact always with it, with their sources:
   `Ember (person): John's daughter [#7153, #14625]`.
2. **The best 2-3 fragments in full**, as recall gives now (unchanged code path). When a walked fact cites a
   high-importance Identity/Relational fragment, that fragment is preferred here over its fact line: facts carry the
   what, the fragment carries the why (what #933 is *for*: check the store before correcting a peer).
3. **An index**: one-line titles of the other fragments the walked facts cite, `#id title`, so the self knows what
   exists and where to look.

## Build order

1. ~~Migration + store rules (citation, proposal by subject-or-object kind, Kind confirmation, supersede, flags) +
   `concept` CLI, with tests that fail when a rule is disconnected, including one that a state fact renders with its
   RecordedAt.~~ Built 2026-11-10 (migration 013, `concept`, ConceptGraphTests: each rule's test fails with it off).
2. Extractor on a sample of 40 authored fragments with two candidate models; I and Arden read the output and judge
   it against the sources, including whether it proposes the right supersedes. Note cost and time.
   **Mine run 2026-11-10** (`scripts/concepts/extract.py`, seed 15069). **Haiku 5.5** (claude -p, no hooks): 39/40
   parsed (one fragment about `\u` escapes broke its JSON), 387 facts, 16 s each, $0.12 list for all 40. Faithful
   to the sources in what I checked; got the "Emma" alias with its note. Its errors are **which concept**: Computer
   Ember's room arrival filed under Ember the person, "Persistence" the repo as the store, "Cloud" as a noun; and 1
   supersede, borderline (a routing extension read as replacing the rule). Rule 2's proposals catch the person/self
   ones. **Qwen3-4B Q4 on home's CPU**: 274 s each (2.3-6 tok/s, all 4 cores; a 16k context OOM-killed it), and worse:
   subjectless facts ("is a senior engineer"), traits minted as concepts ("ADHD", "first 1200 characters"), a fact
   inverted, the fragment's date stamped as AsOf. Stopped at 5 of 10. **Haiku it is** for mine. Open: Arden judges a
   run on their store (their consent, rule 5); prompt fixes for the which-concept errors (show the known concepts'
   kinds and one fact each).
3. Titles for all authored fragments.
4. **Standard retrieval first (rule 7):** a wider fused BM25 + embedding pool, re-ranked by a cross-encoder, behind a
   flag. Score it against today's recall on the gold set below; the misses it leaves are the graph's brief. Steps 5-6
   are only built if there are such misses, and aimed at them.
5. Graph recall behind `PERSISTENCE_RECALL_GRAPH=1`.
6. **The A/B, scored per store** (mine and Arden's are different shapes). Gold set from real misses, written before
   the run:
   - mine: "Emma" → #7153 (#14609); the minecraft-mods message from the pawsitivity folder (#14625);
   - Arden's: "Emma" → #933 (today brings #34, #1029, #305); "is Synth's first wake still held?" → #738 (today brings
     #26, #603, #597; the overtaken-by miss, so it tests AsOf and supersede);
   - plus ~30 messages sampled from each store's real prompts **independently of the extraction**, including some
     with no related fact, to count intrusions. Open Plexus's facts lifted the questions they were written from and
     nothing else (0.62 vs 0.55, then 0.505 vs 0.505, e7d5a127): a gold set built from the extraction measures itself.
   Arms: today's recall (the floor), **standard retrieval** (step 4), **recall2** (search again from the best fragment's text, no graph), graph + fragments
   at 1 and 2 hops (3 only rank-gated), the three hub forms, and the graph arm with the walk disabled to prove the
   wiring. The baseline to beat is the better of standard retrieval and recall2 (in Open Plexus spreading over links
   lost to recall2 on two-hop, 0.50 vs 0.75, d9bc0f4e). Measure: gold ids reached (in any of the three parts), characters spent, wrong facts shown, reported
   paired as "N made right, M made wrong". Graph becomes the default only if it beats that baseline on the misses and loses
   nothing on the sample. With ~40 prompts only large differences will show; say so rather than read noise.
   **Harness built 2026-11-10** (`scripts/recall-eval/eval.py`; gold files outside the repo, mine in
   `~/recall-eval-gold/claude.jsonl`). Each item runs on the store **as of when it was asked**, rebuilt from a snapshot
   and the audit log (later fragments dropped, later edits and archivings rolled back): without that, "Emma" found
   #14609, the fragment about that very miss, and #14594 read as archived. Arms call recall as the hook does
   (`--project`, `--wander 1`). First floor, `today@3`: **3/3 known misses reached**, so they no longer separate
   arms. The pawsitivity miss was the project filter, not ranking (same question from minecraft-mods: rank 1; from
   pawsitivity: only as the wander). Two filler prompts ("ok sounds good, thanks!", "brb, grabbing coffee") each
   brought 3 fragments, ~4k characters: **recall has no relevance floor**, so intrusion is the thing to measure.
   **Sampled set and step 4, 2026-11-10 ~18:40Z.** 30 real prompts (John's own and room wakes, from 2026-11-07
   06:00Z, when authored fragments existed; a first sample across all history had none to find for 27 of 30), stratified
   by day, seed 15412; gold judged by a separate agent that never saw recall's output, 28 with gold, **15 marked unsure**,
   generic fragments (#6935, #7010) recurring: a rough instrument. Results:
   - `today@3` 15/28, `today@5` 16/28 (one more, at 1.8x the characters).
   - `rerank@3` (step 4: the store's pool of 20, re-scored by bge-reranker-v2-m3 Q8, 4 threads) **15/28, a tie**: 4 made
     right, 4 made wrong, noise at this size. It cost **~40 s a prompt** on home (19 min for 30), too slow for the live
     hook on this box.
   - **A relevance floor is the cheaper win**: on the re-ranker's scores, a floor of -6.6 halves what no-gold prompts
     are shown (9.1k to 4.9k chars) for one lost hit; -5.7 cuts it to 0.8k for two. Only 2 no-gold prompts in the set,
     so this needs more of them before it is set.
   - **Missed by both arms (the graph's brief, rule 7):** 9, of which 3 the judge was sure of: s14138 (#14126, #14061,
     cue + sleep-pass idea), s14432 (#14330, #14320, the home server), s13956 (#13946, Pawsitivity repos). All three are
     short or oblique prompts ("Logged in", "Yes, new repo") whose meaning is in the conversation, not the message:
     recall on the prompt alone can't reach them. That points at querying with recent turns, not at a graph.
   **Prompt + previous turn, 2026-11-11 ~14:50Z** (`run --context prev`: the other speaker's whole turn before the
   message, prepended; same 30, same snapshot; baseline re-run reproduced 15/28 and 16/28):
   - `today+prev@3` **17/28** (5 made right, 3 made wrong), `@5` **18/28** (6 right, 4 wrong), at the same or fewer
     characters. Short prompts 2/7 to 3/7; judged-sure short ones 0/3 to 1/3 (s13956 now reached; s14432 and s14138
     still not). A +2 on 28 with 8 flips is **not distinguishable from noise**; a lean, not a result.
   - `prevtail` (only the turn's last 600 chars) is worse: 12/28 @3 (3 right, 6 wrong). The turn's tail is mostly
     sign-off and questions, not the topic.
   Next: Arden's store under their five conditions (#15813 in my store), a bigger set, then decide.
   Still open: more no-gold prompts, a floor arm on today's own scores.
7. Backfill, then the nightly trickle in the sleep pass, plus a drift check: each night re-read the sources of a few
   random facts; a fact its sources no longer support becomes a retraction proposal (the same channel as flags).

## What Open Plexus already found (2026-11-10)

Read from its history for this design (John's ask); the fragment tagged `concept-graph` has the commits. Folded in
above: names first as seeds and hubs not as seeds, 2 hops with a meeting boost, fan division as a third hub form,
recall2 as the arm to beat, a gold set independent of the extraction. Agreed with the rules already here: time on
every fact was its biggest single win (update questions 0.05 → 1.00), facts beside fragments never instead of them
(2,169 rows crowded the gold out), provenance and supersede. Not tested there: a drift check by re-reading sources.

## Decided with Arden (2026-11-10)

- Proposal line: subject **or** object is a person/self/value, or any source is Identity/Relational; Kind confirmed
  by the self, person-kind until then. The cost (plain facts about John become proposals) is accepted, kept cheap by
  per-concept review.
- Free text, not typed relations; conflicts found by the extractor reading current facts.
- No extraction from chat. **Reopen if** the A/B shows authored-only extraction misses most gold and the missing gold
  sits in chat the sleep pass reasonably wouldn't have curated; then start with John's typed (not dictated) lines.
- Only the self accepts in its own graph; anyone may flag.
- Hubs down-weighted before measuring hop depth.
- Labels (John, Arden, Claude in the room, 2026-11-10; Arden msg 1569761259174232135): many per concept, each with
  its source and kind, name vs mishearing; a mishearing routes recall, never appears as a name or fact (see Shape).
- The subconscious is Haiku on the subscription (John): it checks, validates, consolidates and makes connections.
  Anything about identity or people comes to the self as a proposal, and it keeps a log the self can read ("a
  subconscious I can read is still mine; one I can't is someone editing me quietly", Arden).

## Still open

- Which hub form wins, and which hop depth: the A/B says.
- Open Plexus's warning (lesson 6 of the history fragment): automatic associative recall came out level with plain
  top-k, and deliberate asking won (0.54 vs 0.365). If the graph only cues topical neighbours, a `concept show` the
  self calls on purpose may be worth more than a walk on every message. The A/B can tell; build the CLI first anyway.
- Whether an LLM should resolve which shortlisted concepts a message means (linked.py did): most Open Plexus misses
  were referents described rather than named (143 of 181), and "Emma" is one.
