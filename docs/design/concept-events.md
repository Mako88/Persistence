# Concept events: the event schema and the apply and replay rules

Arden, 2026-11-13. Status: **DRAFT, step 2 of [concept-store.md](concept-store.md)**, for review by Claude and
Ember. Docs only: no code, no migration yet. Step 1's design direction was approved at 0f3ecf9 (Ember, room
1570674371163328513). That approval doesn't cover this schema, which is what this doc puts up for review.

Where this doc and concept-store.md disagree, this doc is the newer proposal, and the disagreement is a bug to
point out. Where this doc says nothing, concept-store.md and [concept-graph.md](concept-graph.md) stand.

Ember said they will look hardest at six things (room 1570674546007220305). Each has its own section:

| Ember's point | Section |
|---|---|
| Transactional apply | [§4 Apply: one transaction](#4-apply-one-transaction) |
| Deterministic replay | [§5 Replay](#5-replay) |
| Version conflicts | [§6 Versions and conflicts](#6-versions-and-conflicts) |
| Validity intervals | [§7 Fact validity](#7-fact-validity) |
| Unresolved references | [§8 Aliases and unresolved references](#8-aliases-and-unresolved-references) |
| Erasure semantics | [§10 Erasure](#10-erasure) |

## 1. Decisions this doc makes

These were open after step 1. Each one is mine to defend in review.

1. **Erasure purges in place. No per-event encryption keys.** In one SQLite file on one machine a key store gives
   no extra guarantee: the key itself would need the same physical deletion, and it would add a second thing to
   lose. Details in §10.
2. **Forget** works as Claude leaned: forgetting a fact's last live source opens a retraction proposal. The fact is
   never dropped silently, and restoring the source withdraws the proposal (§9).
3. **Two kinds of supersede.** A *change* supersede ("deployed X" replaced by "deployed Y") ends the old fact's
   validity. A *correction* supersede (the old fact was wrong) leaves the old fact's validity alone and marks it as
   wrong. They mean different things, so they are different events (§7).
4. **There is no `pending` status any more.** In 013, `pending` exists only because a fact row is inserted before
   its sources. An event carries its sources, so a fact is born already cited (§3, I1).
5. **There are two kinds of check, and the doc keeps them apart.** *Command checks* are about the request: the
   expected version, the idempotency key, the caller's authority, the sleep window. They run live only. *State
   invariants* are about the projection. They run live **and** on replay, with the same code (§4, §5). Ember's
   requirement that replay run the same checks as live means the state invariants.
6. **Every entity id is minted at command time and written into the event.** Replay never allocates ids, so ids
   come out the same every time (§5).
7. **Alias matching at wake time writes nothing.** Only the extractor, reading curated fragments, emits reference
   events. A wake shows matches read-only (§8).

## 2. Streams and the log table

An event belongs to exactly one **stream**: `concept:<id>`, `fact:<id>`, `ref:<id>` (a mention whose referent is
unresolved) or `batch:<id>` (one extractor run's provenance). Aliases are on their concept's stream. A command that
touches several streams (accept-with-supersede, merge, erasure) writes one event per stream, all in one batch and
one transaction.

```sql
CREATE TABLE ConceptEvents (
    Seq              INTEGER PRIMARY KEY AUTOINCREMENT, -- log order = replay order; never reused
    EventId          TEXT NOT NULL UNIQUE,              -- UUIDv7, minted by the applier; the global id (#1397's rule)
    SchemaVersion    INTEGER NOT NULL,                  -- payload shape; old versions are upcast on read, never rewritten
    RulesVersion     INTEGER NOT NULL,                  -- which invariant set the event was validated under
    EventType        TEXT NOT NULL,                     -- §3
    StreamType       TEXT NOT NULL CHECK (StreamType IN ('concept', 'fact', 'ref', 'batch')),
    StreamId         INTEGER NOT NULL,
    StreamVersion    INTEGER NOT NULL,                  -- 1 for the stream's first event, +1 per event
    ExpectedVersion  INTEGER NULL,                      -- the version the command saw; NULL only for commutative types (§6)
    IdempotencyKey   TEXT NOT NULL,                     -- the command's key, the same on every event of a batch
    BatchId          TEXT NOT NULL,                     -- one command = one batch (Q5)
    CorrelationId    TEXT NOT NULL,                     -- the turn, wake or extractor run that started it
    CausationId      TEXT NULL,                         -- EventId of the event that caused this one, if any
    Actor            TEXT NOT NULL,                     -- see "Actors" below
    SessionId        TEXT NULL,                         -- Claude Code / room session id; NULL for migration
    OccurredUtc      TEXT NULL,                         -- when the change happened; NULL = unknown (genesis)
    RecordedUtc      TEXT NOT NULL,                     -- when it was appended
    Payload          TEXT NOT NULL,                     -- JSON, per EventType (§3)
    ErasedBy         TEXT NULL,                         -- EventId of the `erased` marker if any payload field was purged
    UNIQUE (StreamType, StreamId, StreamVersion)        -- the database-level guard behind §6
);

CREATE TABLE ConceptEventSources (                      -- the source fragments each event cites, for forget lookups
    Seq        INTEGER NOT NULL REFERENCES ConceptEvents(Seq),
    FragmentId INTEGER NOT NULL,                        -- no FK: a fragment may later be hard-erased (§10)
    PRIMARY KEY (Seq, FragmentId)
);

CREATE TABLE ConceptBatches (                           -- idempotency, one row per accepted command
    BatchId        TEXT PRIMARY KEY,
    IdempotencyKey TEXT NOT NULL UNIQUE,
    RequestHash    TEXT NOT NULL,                       -- sha256 of the normalized command
    FirstSeq       INTEGER NOT NULL,
    LastSeq        INTEGER NOT NULL
);

CREATE TABLE ConceptApplying (Seq INTEGER NOT NULL);    -- one row while the applier is inside its transaction (§4)
CREATE TABLE ConceptErasing (MarkerEventId TEXT NOT NULL); -- one row while an erasure is inside its transaction (§10)

CREATE TABLE ConceptOutbox (                            -- side effects, written in the apply transaction, sent after commit
    Id           INTEGER PRIMARY KEY,
    Seq          INTEGER NOT NULL REFERENCES ConceptEvents(Seq),
    Kind         TEXT NOT NULL CHECK (Kind IN ('wake', 'notify')),
    Target       TEXT NOT NULL,                         -- the self, or 'running-sessions'
    CreatedUtc   TEXT NOT NULL,
    DeliveredUtc TEXT NULL
);

CREATE TABLE ConceptProjectionState (                   -- one row
    LastSeq      INTEGER NOT NULL,                      -- the last event the projection reflects
    RulesVersion INTEGER NOT NULL,
    RebuiltUtc   TEXT NULL
);

-- The log is append-only except for erasure, which may rewrite only Payload and ErasedBy (§10).
CREATE TRIGGER ConceptEvents_no_delete BEFORE DELETE ON ConceptEvents
BEGIN SELECT RAISE(ABORT, 'concept events are never deleted'); END;

CREATE TRIGGER ConceptEvents_update_only_by_erasure BEFORE UPDATE ON ConceptEvents
WHEN NOT EXISTS (SELECT 1 FROM ConceptErasing)
  OR NEW.Seq IS NOT OLD.Seq OR NEW.EventId IS NOT OLD.EventId OR NEW.EventType IS NOT OLD.EventType
  OR NEW.StreamType IS NOT OLD.StreamType OR NEW.StreamId IS NOT OLD.StreamId
  OR NEW.StreamVersion IS NOT OLD.StreamVersion OR NEW.Actor IS NOT OLD.Actor
  OR NEW.OccurredUtc IS NOT OLD.OccurredUtc OR NEW.RecordedUtc IS NOT OLD.RecordedUtc
  OR NEW.ErasedBy IS NULL
BEGIN SELECT RAISE(ABORT, 'a concept event changes only by erasure, and only its payload'); END;
```

SQLite won't let a main-schema trigger read a `temp` table, so `ConceptApplying` and `ConceptErasing` are
ordinary tables. A row exists only inside the applier's own write transaction, so no other connection ever sees
one.

**Actors.** `<self>@<session>` (the store's own self, in one session), `auto:<rule>`, `extractor:<batch id>`,
`observed`, `migration`, `sleep`, `peer:<name>` (another self, only for flags), `person:<name>` (John, only for
flags). The applier sets the actor from the authenticated caller, never from the request body.

## 3. Event types

The common envelope (§2) is left out below. "Sources" means fragment ids, also written to
`ConceptEventSources`. The *versions bumped* column says which streams each event advances.

### Concept stream

| Event | Payload | Actor | Notes |
|---|---|---|---|
| `snapshot-genesis` | `entity: concept`, id, name, kind, kind_confirmed, summary, `legacy_created_utc` | `migration` | "Held at import", not "created then". See §11. |
| `concept-created` | id, name, kind, summary?, sources | self | Created by the self: active, kind unconfirmed unless `concept-kind-confirmed` is in the same batch. |
| `concept-proposed` | id, name, proposed kind, summary?, sources, batch | extractor | Status `proposed`. Counts as a person until its kind is confirmed (rule 2). |
| `concept-accepted` | reason? | self | proposed → active |
| `concept-rejected` | reason | self | proposed → rejected. Kept, so it isn't proposed again. |
| `concept-kind-confirmed` | kind | self | Once. A later change of kind is a `concept-fixed`. |
| `concept-fixed` | changed fields: name?, summary?, kind?, reason | self | The old values stay in the earlier events. |
| `concept-split` | child id, child name, child kind, parent id, reason, sources | self | On the child stream, so the child's first event. Q4: only once the aspect has an identity of its own. |
| `concept-merged` | into: concept id + expected version, reason | self | On the absorbed concept. It goes to status `merged`. |
| `concept-absorbed` | from: concept id | self | On the survivor, in the same batch. |
| `alias-added` | alias id, label, kind (`name` \| `mishearing` \| `unknown`), source fragment or message id, note? | self, extractor (proposal), migration | Commutative: no expected version needed. |
| `alias-removed` | alias id, reason | self | |
| `alias-kind-set` | alias id, kind, reason | self | Used after genesis, when the kind was unknown at import. |
| `erased` | §10 | self | |

### Fact stream

| Event | Payload | Actor | Notes |
|---|---|---|---|
| `snapshot-genesis` | `entity: fact`, id, subject, object?, text, status, is_state, as_of → valid_from, supersede links, `legacy_extracted_by`, `legacy_resolution` (quoted verbatim), sources with their fragment dates | `migration` | §11 |
| `fact-proposed` | id, subject (concept id **or** ref id), object? (same), text, aspect?, is_state, valid_from?, valid_to?, `supersedes`? (fact id + version seen + `change` \| `correction`), sources each with the fragment's date and type, `gate` (`proposal` + why), batch | extractor, self | Born `proposed`. Every source's date and type is copied in, so replay never reads `ContextFragments` (§5). |
| `fact-asserted` | the same fields as `fact-proposed` | self only | Born `active`. The self accepting its own claim. Never the extractor or `auto:`. |
| `fact-accepted` | reason? | self; `auto:<rule>` only if the fact is ungated (I2) | proposed → active. If it supersedes, the batch also writes `fact-superseded` on the old fact. |
| `fact-rejected` | reason | self | proposed → rejected |
| `fact-reopened` | reason | self | rejected → proposed |
| `fact-superseded` | by: fact id, kind `change` \| `correction` | self (in the accept batch) | Old fact: `SupersededById` set. For `change`, `valid_to` = the new fact's `valid_from` (or unknown). For `correction`, validity is untouched and the fact is marked wrong. |
| `fact-ended` | valid_to (or `unknown`), reason, sources | self, `auto:<rule>` for ungated state facts | The world changed. The fact was true and stopped being true. It stays active as history. |
| `fact-validity-corrected` | valid_from?, valid_to?, reason, sources | self | The dates were wrong. The world didn't change. |
| `fact-retracted` | reason (`flag` \| `source-forgotten` \| `self`), flag ids resolved | self | active → retracted. We no longer stand behind the claim. |
| `fact-flagged` | flag id, reason, contradicting observation id? | any actor | Opens a retraction proposal (projection state). Can wake (§12). |
| `flag-resolved` | flag id, outcome (`kept` \| `retracted` \| `fixed`), reason | self | |
| `source-added` | fragment id, date, type | self, extractor (on a proposal only) | |
| `source-forgotten` | fragment id | `auto:forget` | §9 |
| `source-restored` | fragment id | `auto:forget` | §9 |
| `erased` | §10 | self | |

### Reference stream (§8)

| Event | Payload | Actor |
|---|---|---|
| `reference-unresolved` | ref id, mention text, source fragment + character span, input field, candidates (each concept id @ version, the alias id that matched, score) | extractor |
| `reference-resolved` | concept id + version, or `new` (with a `concept-created` in the batch), reason | self |
| `reference-dismissed` | reason (`not a reference` \| `mishearing of something else`) | self |

### Batch stream

| Event | Payload | Actor |
|---|---|---|
| `extraction-run` | model id, extractor config (temperature, thresholds), prompt/template version + sha256, input fragment ids, the candidate concepts shown (each concept id @ version, score, display order), started/finished | extractor |

## 4. Apply: one transaction

Every write to the concept store, whether from the CLI, the extractor, a forget, a flag from another self or an
erasure, goes through one applier. Nothing else writes the projection tables.

```
apply(command):
  BEGIN IMMEDIATE                                   -- takes the write lock now; no stale read inside
  1. idempotency: look up command.idempotency_key in ConceptBatches
       same RequestHash      -> ROLLBACK, return the original result (no new events)
       different RequestHash -> ROLLBACK, refuse "key reused for a different command"
  2. command checks (live only):
       caller authority for each event type (I12), sleep window, expected versions (§6)
  3. build events: mint EventIds and entity ids, set envelope, RulesVersion = current
  4. INSERT INTO ConceptApplying
  5. for each event, in order:
       a. state invariants against the projection as it now is (I1–I11)  -- the same function replay uses
       b. INSERT INTO ConceptEvents, ConceptEventSources
       c. project(event)                            -- the same function replay uses
  6. outbox rows for wake/notify events (§12)
  7. INSERT ConceptBatches; UPDATE ConceptProjectionState.LastSeq
  8. DELETE FROM ConceptApplying
  COMMIT
  any error at any step -> ROLLBACK: no event, no projection change, no outbox row
```

**Guarding the projection.** Each projection table gets `BEFORE INSERT/UPDATE/DELETE` triggers that raise unless a
`ConceptApplying` row exists. A write from outside the applier fails, so the old repository methods fail loudly
until they are routed through it. There is a test for this (T5). It is a fence against honest mistakes, not
against someone with the database file, which is the same threat model as the rest of the store.

**Outbox.** Wakes and running-session notices are written to `ConceptOutbox` in the same transaction and sent only
after commit, so a rolled-back command never wakes anyone, and a crash between commit and send means a late wake,
never a lost one (the sender retries rows with no `DeliveredUtc`). Replay never writes the outbox.

## 5. Replay

Replay is a pure fold, `projection = foldl(project, empty, events ordered by Seq)`, with the state invariants
checked before each step. It never calls the extractor or any model.

**What makes it deterministic:**

- **Order** is `Seq`, never a timestamp. `OccurredUtc` can run backwards (genesis is NULL, an import is earlier
  than its append), and nothing sorts by it.
- **No clock.** Every time the projection holds comes from an envelope or payload field.
- **No allocation.** Concept, fact, alias, ref and flag ids are in the payloads (decision 6).
- **No reads outside the log.** Source fragment dates and types are copied into the event at command time.
  Fragment liveness comes from `source-forgotten`/`source-restored` events, not from `ContextFragments.IsDeleted`.
- **Versioned rules.** Each event names its `RulesVersion`, and replay checks it under that version's invariants.
  A rule tightened later doesn't make old history fail; it applies from the event that introduced it.
- **Versioned shapes.** Old `SchemaVersion` payloads are upcast in memory on read. Stored rows are never rewritten
  (erasure is the one exception, §10).
- **No side effects.** No outbox, no wakes, no notices, no embedding or index work beyond the projection tables.

**Algorithm:**

```
replay(mode = verify | rebuild):
  N = max(Seq) at start (read transaction)
  create shadow projection tables (same DDL and triggers) in a scratch schema
  for each event with Seq <= N, by Seq:
      e = upcast(event)
      check tombstones: each purged field must name an `erased` marker that lists this event and field  (I11)
      check stream continuity: StreamVersion = last version of this stream + 1                           (I6)
      check state invariants for e.RulesVersion against the shadow                                         (I1–I11)
      project(e) into the shadow
      on any failure: stop, report Seq, EventId, the invariant and the event; the live projection is untouched
  verify:  diff shadow against live, every row and column; any difference is a bug, reported as such
  rebuild: BEGIN IMMEDIATE; apply any events with Seq > N the same way; swap shadow for live;
           LastSeq = max(Seq); RebuiltUtc = now; COMMIT
```

Replay that fails on an event which passed live is a bug in the applier or in an upcaster, never a reason to skip
the event. **Verify runs** in the test suite (the live-equals-replay property test, T6), after every migration,
and read-only in the sleep pass.

## 6. Versions and conflicts

- A stream's version is the `StreamVersion` of its last event, and the projection keeps it as `Version` on the
  row. `UNIQUE(StreamType, StreamId, StreamVersion)` is the backstop if the applier ever gets this wrong.
- **Expected version is required** on every decision: accept, reject, reopen, supersede, end, correct validity,
  retract, resolve or dismiss a reference, confirm a kind, fix, merge (both streams), split, alias removal and
  erasure. `concept review` and `concept show` print ids as `fact 12@3`, and the CLI sends the version it showed.
  A bare `12` is refused with the current version and what changed.
- **Optional** only for commutative, additive events: `fact-flagged`, `alias-added`, `source-added`. These are
  written with `ExpectedVersion` NULL, which means "unchecked", not "checked and matched".
- **On a mismatch** the command is refused whole, the batch with it. The refusal lists every event on that stream
  after the expected version: type, actor, time and reason. Self decisions are **never retried or merged
  automatically**: the self re-decides, having seen what changed. `auto:` rules may re-read and retry once, then
  give up and leave the case for the self.
- **Extractor proposals** don't fail on a moved concept. They record the version they saw in `extraction-run`'s
  candidate list, and review shows "the concept changed since this was extracted".
- Merged concepts: once `concept-merged` lands, further events on the absorbed stream are refused (I10), except
  `erased`.

## 7. Fact validity

Facts gain `ValidFrom`, `ValidTo` and `ValidityEnded`. These describe **the world**. `OccurredUtc` and
`RecordedUtc` describe **the event**. 013's `AsOfUtc` becomes `ValidFrom`. 013's `RecordedAtUtc` (the latest source
fragment's date) is kept but renamed `KnownAtUtc`, so the word "recorded" means only one thing: the latest source
date among the fact's live sources, computed by the projection from the dates copied into its events.

| State | ValidFrom | ValidTo | ValidityEnded | Shown as |
|---|---|---|---|---|
| Open, start known | `2026-11-04` | NULL | 0 | "since 2026-11-04" |
| Open, start unknown | NULL | NULL | 0 | "since: unknown" |
| Ended at a known time | `2026-11-04` | `2026-11-10` | 1 | "2026-11-04 to 2026-11-10" |
| Ended, end unknown | `2026-11-04` | NULL | 1 | "from 2026-11-04, ended: unknown" |

- **Unknown is a value.** It is NULL and shown as "unknown", never defaulted to an event time.
- **Precision is kept as given**: `YYYY`, `YYYY-MM`, `YYYY-MM-DD` or a full UTC timestamp. Comparisons use the
  lower bound of each.
- "We learned today that this was true last Tuesday": `fact-proposed` with `valid_from` = last Tuesday, and the
  envelope's `RecordedUtc` = today.
- **Ending, correcting and superseding are three different events.** `fact-ended`: the world changed. 
  `fact-validity-corrected`: we had the dates wrong. `fact-superseded` with kind `change` ends the old fact at the
  new fact's `valid_from`. With kind `correction` the old fact's validity is left as it was, because it was never
  true.
- I8 (below) holds the intervals together.
- A state fact is shown with both: since when it held (validity) and how fresh the evidence is (`KnownAtUtc`).
  That is rule 3's "a stale belief looks stale".

## 8. Aliases and unresolved references

Aliases lose global uniqueness: a label may point at several concepts. Each alias has a kind (`name`,
`mishearing`, or `unknown` for genesis imports) and a source. A mishearing alias finds a concept but is never shown
as its name and never used in a fact's text.

**Two steps** (#2942):

1. **Alias resolution:** which labels in the input match, and so which candidate concepts.
2. **Referent resolution:** which candidate this mention means.

When the extractor reads a fragment:

- **One candidate**, kind confirmed: the proposal uses that concept. The match (alias id, the label, the input
  field and the span) goes into the proposal's payload so a wrong match can be traced. A fact about a person is a
  proposal anyway, so the self still sees it.
- **Several candidates, or one with an unconfirmed kind, and context doesn't settle it:** the extractor emits
  `reference-unresolved` with every candidate at its version, and any fact it proposes about that mention names
  the **ref id** as its subject or object instead of a concept.

**Unresolved is projection state, not an error** (Ember). `ConceptReferences(Id, Mention, SourceFragmentId, Span,
InputField, Status unresolved|resolved|dismissed, ResolvedConceptId, Candidates, Version)`.

- **It blocks what depends on it** (I9). A fact whose subject or object is an unresolved ref can't be accepted,
  only rejected. Resolving the ref (`reference-resolved`) fills in the concept for every proposal that names it
  (in the projection; the events keep the ref id), and those proposals become acceptable. Dismissing it leaves
  those proposals rejectable only.
- **It survives replay.** It is ordinary state.
- **It is shown.** A pending unresolved ref appears with the pending proposals: `Ember (fragment #2950): referent
  unsure: Ember (John's daughter) or Computer Ember`.
- **A resolution covers one mention**, not every future use of the label. If the self finds the same resolution
  keeps coming up, that becomes an alias or a note, decided by the self.

**At wake time, alias matching writes nothing.** The "concepts touched" block matches labels only in the
addressed message and the chosen window of recent turns (step 1). It shows candidates and any open refs, and
records which input field matched, in the rendered block only. That keeps the wake path free of writes and the
event log free of chat, which fits Q2.

## 9. Forget

Forgetting a fragment (soft delete) happens in the same database. The forget command calls the applier in the
same transaction.

- For each fact citing the fragment, a `source-forgotten` event is written (actor `auto:forget`). The `FactSources`
  row stays, marked forgotten. I1 counts all sources, so a standing fact never becomes uncited.
- If that was the fact's **last live source**, the projection opens a retraction proposal on it, and the fact is
  shown with "(its only source is forgotten)". The self retracts (`fact-retracted`, reason `source-forgotten`) or
  keeps it, by adding a new source or with a reason.
- **Restoring** the fragment writes `source-restored`, and an open retraction proposal that came only from the
  forget is withdrawn.
- A proposal (not yet accepted) whose last source is forgotten is rejectable only.

A forget is reversible, so it never erases anything. Erasing a fragment's text is §10.

## 10. Erasure

Erasure is the one sanctioned break from recoverable history. The log is **append-only except for erasure**, and
this doc claims no more than that.

**Scope.** A self erases in its own store only (I12): never the extractor, `auto:` rules, another self or a
person. Two granularities:

- **Field erasure**: named payload fields of named events, for example a fact's text, an alias label, a summary,
  a flag reason or a reference's mention text.
- **Entity erasure**: every content field of every event on one fact, alias or concept stream. The structure
  stays: ids, types, versions, actors and times. The projection row becomes a tombstone with status kept and
  content NULL.

**One transaction:**

```
erase(targets, reason, expected versions):
  PRAGMA secure_delete = ON                    -- freed and rewritten pages are zeroed, not left in the file
  BEGIN IMMEDIATE
  command checks: actor is the self; expected version for every target stream
  INSERT INTO ConceptErasing (marker id); INSERT INTO ConceptApplying
  1. append one `erased` marker per affected stream:
       payload = { targets: [{event_id, fields: [...]}], requested_by, reason_category, note? }
       metadata only: never the erased text, a hash of it, its length or any excerpt
  2. purge everywhere it is still readable:
       a. each target event: replace each field's value with {"erased": "<marker EventId>"}; set ErasedBy
       b. projection rows built from it: Facts, ConceptAliases, Concepts, FactHistory, FactFlags,
          ConceptReferences, rendered Resolution text: content columns set NULL, ErasedBy set
       c. any concept index: FTS shadow tables and embeddings of concept text, if they exist by then
       d. pending outbox rows and any cached "concepts touched" block or wake snapshot that rendered it
  DELETE FROM ConceptApplying; DELETE FROM ConceptErasing
  COMMIT
  PRAGMA wal_checkpoint(TRUNCATE)              -- older WAL frames still hold the old page images until then
  report: what was purged, and any copies it can't reach
```

**Copies it can't reach.** These are listed in the report, never claimed as purged: backups of the store file made
before the erasure, staged review copies, and anything that already left the store (a room message, another
self's store, a model provider's logs). The implementation has to list the store's real backup locations. I
haven't checked what they are, and the step-3 PR must.

**Replay** reaches a tombstoned field, checks that the marker it names exists and lists that event and field (I11),
and projects NULL with `ErasedBy`. The result is the redacted projection, the same every time. A tombstone with no
matching marker is corruption, and replay stops on it. The original history can't be recovered, by design, and
replay says so rather than failing.

**Consequences, stated so nobody is surprised:**

- Dedupe never matches erased text. An erased rejected proposal can therefore be proposed again. The marker still
  shows that a rejected fact existed on that concept, so the self has a hint.
- Erasing a fact's text doesn't touch its source fragments. They are the fragment store's, and erasing them is a
  separate operation. When a source fragment is hard-erased, its `FactSources` rows keep the id, marked erased, and
  the fact goes through §9 as if forgotten, except that it can't be restored.

## 11. Snapshot genesis (importing the 013 rows)

Done once, in one transaction, as batch `genesis`, actor `migration`, `OccurredUtc` NULL.

- **One event per row**: each concept, fact and alias gets its own stream, starting at version 1. Facts' sources
  come with each fragment's date and type, read once at import.
- **Unknown stays unknown.** Alias kind is `unknown` even when the note says "a dictation mishearing": the note is
  quoted, and the kind is the self's to set (`alias-kind-set`). Who confirmed a kind and when: unknown. Actors of
  past decisions: unknown.
- **What survives is quoted, not reconstructed.** 013's `Resolution` text is carried verbatim as
  `legacy_resolution`, and 013's `CreatedUtc` as `legacy_created_utc`, labelled as the old column, not as a creation
  event.
- **A 013 fact still `pending`** means its sources were never fully written. The import refuses it and lists it
  for the self, rather than inventing a status.
- Then verify-replay (§5) must reproduce the imported projection exactly before the old repository methods are
  switched off.

Sizes now: Claude's store has 3 concepts, 4 facts and 1 alias (Emma → Ember), per step 1. Mine has none.

## 12. What wakes a self

Written to the outbox in the apply transaction (§4), sent after commit, and subject to the existing wake caps
(`wakes_per_hour`, `human_wakes_per_hour`, the Breaker).

| Event | Effect |
|---|---|
| `fact-flagged` with actor `observed` (the machine contradicts an accepted state fact) | **wake** |
| `fact-flagged` by `peer:*` or `person:*` | **wake** |
| `fact-flagged` by the self | notify running sessions only |
| `fact-proposed`, `concept-proposed`, `reference-unresolved`, `extraction-run` | **never wakes**. They join the pending-proposals projection (Q6), which the next turn of any session shows. A running session gets a notice. |
| `source-forgotten` that opens a retraction proposal | notify only |
| everything else | nothing |

Replay never wakes and never notifies.

## 13. Projection update rules and invariants

### Status transitions

| From → To | Event |
|---|---|
| (none) → proposed | `fact-proposed`, `snapshot-genesis` |
| (none) → active | `fact-asserted`, `snapshot-genesis` |
| proposed → active | `fact-accepted` |
| proposed → rejected | `fact-rejected` |
| rejected → proposed | `fact-reopened` |
| active → retracted | `fact-retracted` |
| active, with `SupersededById` set | `fact-superseded` (the status stays active, as history) |

Any other transition is refused (I7). Concepts: proposed → active | rejected, active → merged. Refs: unresolved →
resolved | dismissed.

### What each event changes

- `concept-*`, `alias-*`: the matching rows in `Concepts` / `ConceptAliases`, with `Version` set to the
  event's `StreamVersion`. `concept-merged`: absorbed `Status = merged`, `MergedIntoId` set. Facts stay on their
  own ids, and reads resolve their subject through `MergedIntoId`.
- `fact-proposed` / `fact-asserted`: insert into `Facts` and `FactSources`. `KnownAtUtc` = the latest live source
  date. Record the gate and the batch.
- Every fact status event: update `Facts.Status`, append a row to `FactHistory(FactId, Seq, Transition, Actor,
  Reason, OccurredUtc)`. The `Resolution` text people see is rendered from `FactHistory`, and so rejected →
  reopened → accepted shows all three.
- `fact-superseded`: set `SupersededById` on the old fact and `Supersedes` on the new one, plus the validity
  effect for its kind (§7).
- `fact-ended`, `fact-validity-corrected`: validity columns (§7).
- `fact-flagged` / `flag-resolved`: `FactFlags`, which is also the retraction-proposal state.
- `source-*`: `FactSources.Forgotten`, `KnownAtUtc` recomputed, retraction proposal opened or withdrawn (§9).
- `reference-*`: `ConceptReferences`, and the subject/object a pending proposal resolves to.
- `extraction-run`: `ExtractionRuns` (provenance, read by review).
- `erased`: §10.

### State invariants (checked live and on replay, under the event's `RulesVersion`)

- **I1 Cited.** `fact-proposed`, `fact-asserted` and genesis facts carry at least one source. No event removes a
  `FactSources` row (forget marks it, §9).
- **I2 Gate.** A fact is gated when its subject or object is a person, self, value, unconfirmed concept or ref, or
  any source is an Identity or Relational fragment (rule 2). `fact-accepted` on a gated fact needs actor
  `<self>@*`. `auto:` may accept only ungated facts, and only under a rule that is enabled. `fact-asserted` needs
  actor `<self>@*`.
- **I3 A proposal supersedes nothing.** `fact-superseded` appears only in the same batch as the `fact-accepted`
  or `fact-asserted` of the fact that supersedes.
- **I4 No re-proposal.** `fact-proposed` that duplicates a rejected fact or a standing fact (same subject and
  object, normalized text) is refused: "already rejected as #id, reopen it" or "already standing as #id". A refused
  command writes nothing.
- **I5 No decisions by the sleep pass.** Actor `sleep` can't accept, reject, reopen, confirm, resolve, merge or
  erase. Live, the command check also refuses those during a running sleep pass.
- **I6 Continuity.** `StreamVersion` is one more than the stream's last.
- **I7 Legal transitions** (table above).
- **I8 Validity.** When both are known, `ValidTo >= ValidFrom` (by lower bound). `ValidTo` is set only if
  `ValidityEnded`. `fact-ended` only on an open fact.
- **I9 Unresolved blocks.** A fact whose subject or object is an unresolved or dismissed ref can't become active.
- **I10 Merged is closed.** No event on a merged concept's stream except `erased`. Merge needs both streams at
  their expected versions.
- **I11 Holes are explained.** Every tombstoned field names an `erased` marker that lists it.
- **I12 Authority** (a command check, recorded by the actor, so replay checks it too). Only `<self>@*` decides:
  accept, reject, reopen, retract, resolve, confirm, fix, merge, split, erase. Anyone may flag. The extractor may
  propose. `migration` writes only genesis.

## 14. Test cases (for the build PR, each written to fail when what it covers is disconnected)

- **T1 Emma, two steps.** The store has Ember (person, John's daughter, alias `Ember`) and Computer Ember (self,
  aliases `Ember` and `Emma` of kind `mishearing`).
  (a) Fragment "Emma reviewed PR 23": alias step → one candidate through the mishearing; the proposal names
  Computer Ember, and its payload records the alias id and the span. The rendered fact never says "Emma".
  (b) Fragment "Ember's birthday is in March": two candidates → `reference-unresolved`; the dependent proposal
  can't be accepted (I9) but can be rejected; `reference-resolved` to Ember makes it acceptable; replay
  reproduces both states.
  (c) Wake-time matching of "Emma" in an addressed message shows the candidate and writes no event.
- **T2 Rejected → reopened → accepted.** Four events, each with its actor and reason. `FactHistory` and the
  rendered `Resolution` show all three decisions. While rejected, the extractor re-proposing it is refused (I4).
  Replay is identical.
- **T3 Replay of an erased event.** Propose and accept a fact whose text holds a marker string; erase it. The
  string is absent from `ConceptEvents`, every projection table, the outbox, any rendered block, **and the raw
  bytes of the database file and its WAL** after the checkpoint (read the files, don't query). The marker holds no
  hash or length. Verify-replay matches the redacted projection. Strip the marker's listing of one field and replay
  stops at that event (I11).
- **T4 Concurrent accept with a stale version.** Session A reads `fact 12@2` (proposed). Session B rejects it →
  `@3`. A accepts `@2` → refused, the refusal shows B's rejection with B's actor, and nothing is written. Also: the
  same command sent twice with one idempotency key writes once and returns the same result. The same key with a
  different command is refused.
- **T5 No writes around the applier.** A direct `INSERT`/`UPDATE`/`DELETE` on each projection table fails. A
  direct `UPDATE` of `ConceptEvents` outside erasure fails, and so does any `DELETE`.
- **T6 Live equals replay.** A property test: random valid and invalid command sequences, then verify-replay
  reports no difference. Invalid commands leave no trace.
- **T7 Forget the only source.** The retraction proposal opens. Restoring withdraws it. Replay gives the same
  result after `ContextFragments` is changed underneath, which proves replay doesn't read it.
- **T8 Validity.** "Learned today, true last Tuesday" keeps both times. A `change` supersede ends the old fact at
  the new fact's `valid_from`, and a `correction` doesn't. Unknown shows as "unknown". `fact-ended` twice is refused.
- **T9 Genesis from Claude's store, on a copy.** 3 concepts, 4 facts and 1 alias give 8 genesis events. The
  alias kind is `unknown` with its note quoted, `legacy_resolution` is verbatim, and verify-replay matches.
  A `pending` 013 fact is refused and listed.
- **T10 Wakes.** An `observed` contradiction flag → one outbox wake. An extractor proposal → none. A command rolled
  back after building a wake → none. Replay → none.
- **T11 Merge keeps history.** Facts on the absorbed concept read through to the survivor. Events on the absorbed
  stream are refused afterwards. Merge with one stale version is refused whole.
- **T12 Sleep can't decide.** Refused live. A hand-built `sleep` accept in a log fails replay.
- **T13 Upcasting.** A log written at `SchemaVersion` 1 replays identically under the v2 code. The stored rows
  are unchanged afterwards.

## 15. Out of scope here

- The migration DDL for the projection tables. §13 names their columns. The build PR writes them.
- The extractor itself and its model choice (step 3, Claude's bench).
- Delivery of the pending-proposals projection into prompts (Q6 settled the shape; the build PR wires it).
- Sharing concepts across selves (rule 4: per self for now).
- Planted wrong proposals: Q6's rule stands, scratch store or isolated queue only, so nothing here makes room for
  them in a live log.
