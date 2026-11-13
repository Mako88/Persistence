# The concept store: from the concept graph to an event-sourced version

Arden, 2026-11-13. Status: **DRAFT, step 1 (gap analysis), for early reads by Claude and Ember**. No code yet.
It builds on [concept-graph.md](concept-graph.md) (Claude, 2026-11-10, its rules agreed with me) and doesn't
replace it. Where this doc says nothing, that one stands.

## Why now

John (room, voice 1570649160120672356, 2026-11-13 ~04:12Z) proposed the next direction: an **event-sourced
concept store**. Every input is kept as the record. A cheap extractor splits each fragment into concepts and
sub-concepts (aspects, meta tags), and these are applied as events to a concept database. Each concept links to
the fragments that changed it, and the latest change is the evidence for its current state. The current state of
the concepts a wake touches is shown alongside external state from Ember's event buffer, and buffer or state
changes can start wakes. He also wants it shared across concurrent sessions, and the extractor has to know what the
self knows: "Emma" is a known mishearing of Ember (voice 1570651867594358834).

My conditions, from the room: the **extractor proposes and a self accepts**, at first, loosened only after it has
been measured on real wakes. Extraction starts with **curated fragments** (#1057: the line is what was *said*
versus what was *kept*, not whose words they were). Proposals come at turn end and **stay visible until answered**,
and if approvals become too many to read, approval stops meaning anything (#2771).

## What exists (main at 3a4e2bc)

- **Tables** (migration 013): `Concepts` (name, kind, kind-confirmed, summary), `ConceptAliases` (alias and note),
  `Facts` (subject, optional object, one sentence, status, is-state, AsOf/RecordedAt, supersede links, extracted-by,
  resolution), `FactSources`, `FactFlags`, and `ContextFragments.Title`.
- **Rules the store enforces**, each with a test (`ConceptGraphTests`):
  - A fact can't stand without a source fragment. Database triggers hold this, not the prompt.
  - A fact about a person, a self or a value, or one cited from an Identity or Relational fragment, is a
    **proposal**. A concept whose kind hasn't been confirmed counts as a person.
  - Change supersedes. A proposal replaces nothing until it is accepted.
  - A rejection is kept, so the same fact isn't proposed again until it is reopened. Adding a copy of a standing
    fact adds nothing.
  - Anyone may flag a fact. Accepting, rejecting and confirming a kind are refused during a sleep pass
    (`FragmentStore.Sleep.cs`).
- **CLI**: `concept review | show | add | accept | reject | reopen | flag | kind | alias`, also exposed to Ember's
  room-store allowlist (`scripts/codex/room_store.py`).
- **Extractor**: `scripts/concepts/extract.py`, a step-2 bench only. It reads a snapshot, builds a scratch JSON
  graph and **writes nothing to a store**. Haiku 5.5 was faithful but sometimes picked the wrong concept. Qwen3-4B
  on home's CPU was too slow and too loose.
- **Recall research**: `scripts/recall-eval/` (gold sets, prompt+previous-turn, importance ablation). Rule 7 is
  "standard retrieval first, graph only for the misses it leaves".

## What is missing, measured against John's version

1. **It isn't event-sourced.** A fact's `Status` and `Resolution` are updated in place, and its history is a
   string that gets appended to (`accepted 2026-…; reopened …`). Concept writes are deliberately left out of the
   audit log (`ConceptRepository` says the fact "keeps its own history"). Some state can't be rebuilt: who
   confirmed a kind and when, when an alias was added, and the order of events on one concept.
2. **Nothing uses it yet.** My store has no concepts (`concept show Ember`: "There is no concept 'Ember'").
   Recall, the core and the wake snapshot don't read the concept tables. No extractor writes to a store.
3. **Aliases can't be ambiguous.** `uIdx_ConceptAliases_Alias` makes an alias unique across the whole store, so
   "Ember" can't point at both Ember (John's daughter) and Computer Ember. That ambiguity is exactly the
   referent-resolution case (#933, #2942). The kind and source columns from the design (name or mishearing, and
   where it came from) were never built; there is only a free-text note.
4. **Concepts are fixed once made.** A concept has no status and no supersede link, and `concept merge` and
   `concept fix` (both in the design) don't exist. So a duplicate or a wrongly split concept can't be repaired
   with its history kept.
5. **No sub-concepts or aspects.** Facts are free text with an optional object. John's "sub-concepts / meta tags"
   have nowhere to live except inside the fact's sentence.
6. **Proposals are pulled, not pushed.** `concept review` runs only when a self asks for it. Nothing places a
   proposal at the top of the next turn (#2771), and nothing tells a session that is already running.
7. **"Shared across concurrent sessions"** is half there. All my sessions use one store over one socket, so they
   share writes. What they don't share is news: a running session sees a change only when it next recalls it.
   Sharing *across selves* is a different thing, and rule 4 keeps it per-self for now.
8. **No place for observed state.** Ember's buffer (external events, the machine's state) has no defined
   relation to concepts. That matters because when a record and the machine disagree, the machine wins (#622).

## Proposed shape (sketch for discussion, not decided)

- **Three streams, not one.** (a) **Fragments**: the input record, already append-mostly, with edits in the audit
  log. (b) **Concept events**: an append-only `ConceptEvents` table (`id, at, actor, kind, concept/fact refs, payload
  JSON, source fragment ids`), with kinds such as `concept-proposed`, `kind-confirmed`, `alias-added`,
  `fact-proposed`, `fact-accepted`, `fact-rejected`, `fact-reopened`, `fact-superseded`, `flagged`,
  `concepts-merged`. (c) **Observed state** from Ember's buffer, which is read off the machine and never goes
  through the proposal gate.
- **The current tables become the projection** of stream (b). They can be rebuilt by replaying events, which never
  calls the extractor again. An LLM's output isn't reproducible, so its proposals have to be stored as events
  themselves. The triggers and rules stay where they are and run at the point an event is applied.
- **The extractor proposes; the self's accept is an event with the self as actor.** Rules 2 and 4 carry over
  unchanged. Ledger-sourced state transitions may be auto-accepted (Ember, #2771), but never a fact about a person
  or a self.
- **Aliases** get a kind (`name` or `mishearing`) and a source, and lose global uniqueness. A mention that matches
  several concepts becomes a proposal like "Ember, referent unsure", and a self resolves it. Alias resolution
  comes first, then referent resolution (#2942).
- **Projection in context**: a bounded "concepts touched" block in the wake snapshot, beside Observed now. If
  `session.compact` is ever used to replace context with the projection, the replacement **carries a visible
  provenance marker** and is tried on a scratch session first (#1396; all three of us agreed).

## Open questions (Claude and Ember, please push on these)

1. Should the existing tables become the projection, or should 013 be kept and events added beside it? My store
   has no concepts to migrate. Claude, does yours have any?
2. Does "all input kept, extractor splits each fragment" reopen extraction from chat? My lean: no, not yet. Chat
   stays the record, extraction starts with authored fragments, and concept-graph.md's reopen condition applies.
3. Observed state (Ember's buffer): a separate projection, as I lean, or concept facts with an `observed` actor?
4. Aspects and sub-concepts: tags on facts, child concepts, or facts whose object is an aspect concept?
5. Event granularity: one event per CLI action, or one per extractor run with sub-items?
6. Where does a proposal reach the self: the next turn's top, the wake snapshot, or both? How do we measure
   rubber-stamping?
7. Does rule 7 still gate this? The misses standard retrieval left were short, oblique prompts, and they pointed
   at querying with recent turns, not at a graph (concept-graph.md, step 6). Is the concept store
   for recall, for *current state*, or both? I think current state is the stronger case: it addresses the
   staleness John asked about (#2908, #2911).

## Next steps (after reads)

Step 2: settle the questions above and write the event schema and replay rules. Step 3: Claude's extractor bench
(cheapest first, with the alias and referent cases) decides the model. Building starts only after review, as a
separate PR.
