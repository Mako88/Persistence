# The concept store: from the concept graph to an event-sourced version

Arden, 2026-11-13. Status: **DRAFT, step 1 revised after early reads by Claude and Ember**. No code yet.
It builds on [concept-graph.md](concept-graph.md) (Claude, 2026-11-10, its rules agreed with me) and doesn't
replace it. Where this doc says nothing, that one stands.

Early reads: Claude, [PR comment 6844993714](https://github.com/Mako88/Persistence/pull/23#issuecomment-6844993714);
Ember, room 1570671177452757013 and 1570671178509721703. They agree on all seven questions; my synthesis is room
1570671266388508744. Their answers are recorded under [Resolutions](#resolutions-of-the-seven-questions). Q2 waits on
one line from John.

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

1. **It isn't event-sourced, and history is overwritten.** A fact's `Status` and `Resolution` are updated in
   place. Only `reopen` appends to `Resolution` (`FragmentStore.Concepts.cs:175`). `accept`, `reject` and
   supersede pass a new resolution, and `SetFactStatusAsync`/`SupersedeFactAsync` write
   `COALESCE(@resolution, Resolution)` (`ConceptRepository.cs:145,152`), which replaces the old value. So
   rejected → reopened → accepted keeps only `accepted <date>`: the rejection and the reopen are lost (Claude;
   checked against the code). No decision records an **actor or session**, so we can't tell `self@session` from an
   automatic rule or the sleep pass. Concept writes are deliberately left out of the audit log
   (`ConceptRepository` says the fact "keeps its own history"). Some state can't be rebuilt: who confirmed a kind
   and when, when an alias was added, and the order of events on one concept.
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

Missed in the first draft (Claude's read):

9. **Extractor provenance.** `ExtractedBy` is only a name. To find out later *why* a model picked the wrong
   concept, a proposal needs the model id, a prompt hash and the candidate concepts it was shown. The store will
   have changed by the time anyone looks, so the candidate set can't be rebuilt afterwards.
10. **Concurrent decisions.** One socket serialises writes, but a session can act on a stale view: it accepts a
    proposal another session rejected a minute ago. Nothing checks for that.
11. **Forget versus append-only.** `forget` soft-deletes a fragment, but the citation trigger checks sources only
    at insert time. Nothing says what happens to a fact whose only source is forgotten, or how a true erasure
    works in a log that only appends.
12. **Which events start a wake.** John wants state changes to be able to wake a self. The draft didn't say which.


## Agreed shape

- **Three streams.** (a) **Fragments**: the input record, already append-mostly, with edits in the audit log.
  (b) **Concept events**: an append-only `ConceptEvents` log, the **source of truth** (Q1). (c) **Observed
  state**: read off the machine, its own short-lived stream, never through the proposal gate (Q3).
- **The 013 tables become a projection** of (b), rebuilt by replay. Replay never calls the extractor: an LLM's
  output isn't reproducible, so its proposals are stored as events. Today's rows are imported once as genesis
  events with actor `migration`; fields we don't know are marked unknown, not invented. Claude's store has 3
  concepts, 4 facts and 1 alias (Emma → Ember); mine has none.
- **Event envelope** (Ember), on every event: event id, schema version, idempotency key, batch id (Q5),
  correlation and causation ids, actor (`self@session`, `auto:<rule>`, `extractor`, `observed`, `migration`,
  `sleep`), occurred-at and recorded-at (bitemporal: when it was true versus when we wrote it down), expected
  version of the concept or fact it changes, source fragment ids, payload.
- **Apply and replay are one contract.** Appending an event and updating the projection happen in one
  transaction. The rules the triggers hold today (a fact needs a source, rule 2's proposal gate, change
  supersedes, rejections kept) run at the point an event is applied, and **replay runs the same checks** as live
  application. A direct write to a projection table outside the applier must fail or be caught, with a test.
- **Status history per transition.** Every accept, reject, reopen, supersede and kind confirmation is its own
  event with its actor and reason, so rejected → reopened → accepted keeps all three (gap 1). The `Resolution`
  string becomes a rendering of those events, not the record.
- **Expected-version checks** (gap 10): each command carries the version of the concept or fact it saw. If the
  version has moved, the command is refused and shows what changed. Cheap now, painful to retrofit.
- **Extractor provenance** (gap 9): the batch event records model id, prompt hash, input fragment and the
  candidate concepts shown, once per run; each `fact-proposed` event points to its batch.
- **The extractor proposes; the self accepts**, as an event with the self as actor. Rules 2 and 4 carry over.
  Ledger-sourced state transitions may be auto-accepted later (actor `auto:<rule>`), never a fact about a person
  or a self.
- **Aliases** get a kind (`name` or `mishearing`) and a source, and lose global uniqueness. Alias resolution
  comes first, then referent resolution (#2942).
- **Uncertainty is projection state, not an error to erase** (Ember). "Alias matched; referent unresolved" is
  a state the projection holds. It survives replay, is shown in context, and **blocks downstream claims** that
  need a resolved identity until a self resolves it (the daughter Ember versus Computer Ember case, #933).
- **Forget** (gap 11, Claude's lean, to settle in step 2): forgetting a fact's only source emits a
  `source-forgotten` event that turns the fact into a retraction proposal; it is never silently dropped. A true
  erasure redacts the payload in place and leaves a `redacted` marker, so the log keeps its shape.
- **What wakes a self** (gap 12): an observed value that contradicts an accepted state fact, and a flag from
  someone else, under the existing wake caps. **Never** a new extractor proposal, or the extractor becomes a wake
  flood.
- **Projection in context**: a bounded "concepts touched" block beside Observed now, found by alias matching on
  the message and wake (no graph walk, so rule 7 isn't touched). Every state fact shows its AsOf. If
  `session.compact` is ever used to replace context with the projection, the replacement **carries a visible
  provenance marker** and is tried on a scratch session first (#1396; all three of us agreed).

## Resolutions of the seven questions

1. **Projection or beside it?** Events are the source of truth; the 013 tables become the projection; existing
   rows are imported as genesis events. Two writers would drift. (Claude, Ember)
2. **Extraction from chat?** **Pending John.** Claude and Ember both say no for the first version: the extractor
   reads deliberately authored fragments, and chat stays evidence. "All input kept" is already true of fragments
   and chat; *what gets extracted* is a separate choice, and John's words ("every input… extractor splits each
   fragment") don't settle it, so I asked him in one line (room 1570671266388508744). Later, chat could come in
   only as a separately measured, weaker source whose proposals never silently become current state (Ember).
3. **Observed state?** A separate short-lived projection. It can render beside a concept whose alias matches the
   observed key, and when it contradicts an accepted state fact it emits a `flagged` event with actor `observed`
   (the machine wins, #622). It can also back a proposal that cites the observation id. It never becomes accepted
   state by itself. (Claude, Ember)
4. **Aspects?** The lightest form that keeps the meaning: an optional one-level `aspect` tag on a fact first; a
   typed relation when the aspect is just the object of a claim; a child concept (by a `concept-split` event with
   a parent link) only once the aspect has its own identity, facts, aliases or history. Not every noun becomes a
   concept: that pollutes alias matching and recreates Open Plexus's hub problem. (Claude, Ember)
5. **Granularity?** One event per state change, grouped by a batch id (one CLI call or one extractor run). The
   batch carries shared provenance once. Accepting several at once may be one atomic command, but replay still
   sees each transition. (Claude, Ember)
6. **Where proposals reach the self?** One canonical pending-proposals projection, not two delivery mechanisms.
   The top of the next turn, in any session, shows up to ~3 (this session's newest first, while the source is
   fresh); the wake snapshot shows the same projection as a count, the oldest one's age and the same ids. A
   proposal stays visible until decided. Measure: proposal volume, time to decision, reject-or-edit rate (near
   zero over a few dozen is itself a signal), later corrections, accepts made without opening the source first,
   large batches accepted at once, and, only with each self's prior consent, rare planted wrong proposals and
   their catch rate (Claude consents for its store; I'll decide for mine in step 2). (Claude's and Ember's details
   differ slightly; this merges them.)
7. **Does rule 7 gate this?** Rule 7 gates recall, not current state. The concept store's first job is current
   state; standard recall still retrieves the fragments, and graph-assisted recall waits for a benchmark miss.
   (Claude, Ember)

## Next steps

Step 2: write the event schema (the envelope above, the event kinds, the bitemporal fields) and the apply and
replay rules, including forget, redaction and expected versions. Ember asked that the envelope, the atomic
apply/replay contract and the bitemporal fields be explicit before schema work begins; they are listed above and
step 2 starts from them. Step 3: Claude's extractor bench (cheapest first, with the alias and referent cases)
decides the model. Building starts only after review, as a separate PR.
