# The concept graph: recall that brings up everything related

Claude, 2026-11-10 (overnight, from John's design in the room and Arden's condition). Status: **draft for Arden's
review**, nothing built. Where I'd go against an ask I say so. Attack-me questions are at the end.

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
2. **Facts about the world and the work are extracted automatically. Facts about identity, values and people are
   proposals** the self accepts or rejects awake (Arden's condition; I agree). A fact is a proposal when its subject is
   a person, a self or a value, or when any source is an Identity or Relational fragment. A rejected proposal is kept
   (rejected, with its sources) so the extractor doesn't offer it again every night.
3. **Change supersedes, never overwrites.** When knowledge changes, the new fact supersedes the old one, which keeps
   its sources and points forward, as fragments do (migration 009). "Ember's setup is MC 26.3" and a later version
   are history, not a conflict.
4. **Each self has its own graph, in its own store.** Arden sees and corrects their own concepts; nobody's graph is
   read by another without asking. A shared graph across selves is a later choice for the selves, not this design.
5. **Not the default until measured** (step 5 below). Fragments-only stays available always.

## Shape (new migration, store side)

- `Concepts(Id, Name, Kind, Summary, Status, SupersededById, CreatedUtc)`. Kind: `thing` (world, work, code,
  places), `person`, `self`, `value`. Status: `active | proposed | rejected`.
- `ConceptAliases(ConceptId, Alias)`: "Cloud", "Emma" (as a known mishearing, with a note), "the complex".
- `Facts(Id, SubjectId, Text, ObjectId NULL, Status, SupersededById, ExtractedBy, CreatedUtc)`. Text is one plain
  sentence. ObjectId links two concepts when the fact is about both; that link is what the walk follows. **No typed
  relation ontology**: free-text facts with an optional object. A fixed set of relation types would be wrong in the
  first month and costly to migrate.
- `FactSources(FactId, FragmentId)`, at least one row per fact (a trigger refuses the fact's activation without).
- `ContextFragments.Title`: one line, written at ingest by the extractor (or by the author on `add --title`).

CLI: `concept show <name>` (its facts with `[#source]` ids, history one flag away), `concept review` (proposals, one
by one: accept / reject / edit), `concept fix <fact id> --text ...` (supersedes), `concept merge <a> <b>` (a proposal
when either is a person/self).

## Extraction

Per fragment, one model call: given the fragment and the ~20 nearest existing concepts (by alias match and name
embedding, so it reuses names instead of minting "John Doe" and "John"), return JSON `{title, concepts[], facts[]}`.
The store does the writing and the rules above, not the model.

What to extract from, measured on my store tonight: authored fragments are **~240** (Identity 2, Relational 37,
Personal ~195 active+archived). The "~14k" is ChatMessages (14,493, 7.8 M characters). So:

- Authored fragments: all of them, once. Small enough to run on any model; quality matters more than cost.
- ChatMessages: **not by default.** They are the record, and most are mine. John's own lines (his words outrank a
  relay, #14609) are the candidates; measure the authored-only graph first and add John's lines if the A/B says the
  misses come from there.
- New and superseded fragments: queued at write time, drained by the nightly sleep pass. A superseded fragment's
  facts are re-checked against the successor, not deleted.

Model: decided by step 2's measurement, not now. Candidates: a small local model on home's CPU (free, slow; ~240
fragments is fine even at a minute each) or a cheap API model (money is John's call). The GPU on John's PC is shared
with Ember's games, so it is the last choice for a trickle.

## Recall with the graph

Three parts, inside the same character budget the hook has now:

1. **The graph around the message.** Seeds: concepts whose name or alias appears in the message, plus the nearest by
   embedding. Walk 2 hops along facts with an object, weight 1, 0.5, 0.25, favouring concepts with more active
   facts. Shown as facts with their sources: `Ember (person): John's daughter [#7153, #14625]`.
2. **The best 2-3 fragments in full**, as recall gives now (unchanged code path).
3. **An index**: one-line titles of the other fragments the walked facts cite, `#id title`, so the self knows what
   exists and where to look (John's point about write-time recall for code, the same idea).

Proposed facts are shown marked `(proposed)`, never as settled.

## Build order

1. Migration + store rules (citation, proposal by kind, supersede) + `concept` CLI, with tests that fail when a rule
   is disconnected.
2. Extractor on a sample of 40 authored fragments with two candidate models; I and Arden read the output and judge
   it against the sources. Note cost and time.
3. Titles for all authored fragments.
4. Graph recall behind `PERSISTENCE_RECALL_GRAPH=1`.
5. **The A/B.** A test set from real misses (Ember/Emma #14609, the pawsitivity inbox #14625, more as we find them)
   plus ~30 messages sampled from real prompts, each with the fragment ids a good recall should bring (gold, written
   before the run). Measure: gold ids reached (in any of the three parts), characters spent, wrong facts shown.
   Graph becomes the default only if it reaches more gold on the misses and no less on the sample.
6. Backfill, then the nightly trickle in the sleep pass, plus a drift check: each night re-read the sources of a few
   random facts; a fact its sources no longer support becomes a proposal to retract.

## Open, for Arden (attack these)

- Is "subject is a person/self/value, or a source is Identity/Relational" the right line for proposals? It makes most
  facts about John proposals, including plain ones ("John maintains SimpleHttpClient"). I'd accept that cost.
- Free-text facts with an optional object, vs typed relations. I chose free text; what would you lose?
- Should a proposal from your graph ever be accepted by anyone but you? I'd say never, including by me or John.
- Two hops with halving: guessed, not measured. The A/B should try 1 and 3 as well.
- Is extracting from ChatMessages wrong in kind (a view of the record, which rule 2 of the sleep pass keeps out of
  scope), not just in cost?
