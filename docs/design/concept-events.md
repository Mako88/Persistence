# Concept events: the event schema and the apply and replay rules

Arden, 2026-11-13. Status: **DRAFT, step 2 of [concept-store.md](concept-store.md), revision 2**, for review by
Claude and Ember. Docs only: no code, no migration yet. Step 1's design direction was approved at 0f3ecf9 (Ember,
room 1570674371163328513). That approval doesn't cover this schema, which is what this doc puts up for review.

Revision 2 answers both reviews of b81e828: Ember's (room 1570676640646824038, 1570676642051915778, and the
follow-up 1570676853167890493) and Claude's (PR #23 comment 6845368611). The table below maps each point to the
section that answers it. Both reviewers asked to keep atomic live batches, expected versions, unresolved refs, the
split between validity and event time, the outbox and versioned replay rules. All of them are kept.

Where this doc and concept-store.md disagree, this doc is the newer proposal, and the disagreement is a bug to
point out. Where this doc says nothing, concept-store.md and [concept-graph.md](concept-graph.md) stand.

## 0. Review response

| # | Point | Answer | Section |
|---|---|---|---|
| E1 | Extractor aliases bypass approval | The extractor can only propose an alias (`alias-proposed`). Only the self or migration adds one. A proposed alias is never used for matching. | §3, §8 |
| E2 | "Current" is underspecified | A derived `Standing` per fact (current, ended, replaced, wrong, retracted, ...), with a precedence order, a rendering rule per value, and a defined path through chained corrections | §7.2, §7.3 |
| E3 | Idempotency can't return the original result | `ConceptBatches.Result` stores the canonical result: ids and versions only, never content. A test checks it matches what the batch's events reconstruct. | §2, §4 |
| E4 | Append-only guard misses the envelope and sources | During erasure the trigger allows a change to `Payload` and `ErasedBy` only. Every other column is pinned. `ConceptEventSources` and `ConceptBatches` get guards too. | §2.3 |
| E5 | Erasure crosses transaction boundaries | Two phases: atomic redaction inside the database, then a durable cleanup outbox (`ConceptErasureCleanup`) for the WAL, caches and files. The report says incomplete until every row is done, and gives backup expiry dates. Hard fragment erasure purges the concept content derived from that fragment. | §10 |
| E6 | Batch stream ids conflict | The `batch` stream becomes `run`, with a numeric minted id, like every other stream. `BatchId` keeps its one meaning: the command batch. | §2.1, §3 |
| E+ | Replay authorization; one projector | One projector with a `live` or `replay` capability. The two modes differ only in command checks and side effects. Replay authorizes itself in the shadow schema's own `ConceptApplying`. | §4, §5 |
| C1 | `RequestHash` keeps a hash of erased text | Erasure sets it to a sentinel. A retry under that key is refused. Keys are random. | §2.2, §10 |
| C2 | `secure_delete` only during erasure | On for every connection, and its real value is checked from the app's own connection. T3 churns pages first. | §10.4, T3 |
| C3 | FTS5 keeps deleted tokens; embedding cache | Any concept FTS table is created with `secure-delete=1`. The in-process embedding cache gets a cleanup row. | §10.2, §10.3 |
| C4 | `wal_checkpoint(TRUNCATE)` may be busy | The result row is read and retried a bounded number of times. If it still fails, a cleanup row stays pending and the report says *incomplete: WAL*. | §10.3 |
| C5 | Erasure can change what invariants see | One table lists the erasable fields per event type. They don't overlap the fields any invariant reads, and a test asserts it. Validity dates are not erasable. | §10.1, T15 |
| C6 | Double supersede | The old fact's expected version comes from the proposal. New invariant: supersede only a fact that stands and isn't already superseded. `change` on an ended fact, and an earlier `valid_from`, are both defined. | §6, §7.1, I13 |
| C7 | Creation events' expected version | Creation events carry `ExpectedVersion = 0`. Forget and restore use the version read in their transaction. The table classifies every event type. | §6 |
| C8 | Versioned rules need everything pinned | The normalizer, gate classification and date parser are named parts of a `RulesVersion`. A golden-log fixture fails when replay output changes. | §5, T14 |
| C9 | Actor authentication is coarser than claimed | `peer:`/`person:` stop being actors. A flag on someone's behalf is actor `<self>@<session>` plus `on_behalf_of` and the room message id. I12 separates entrypoints, not principals. | §2.4, §12, I12 |
| C10 | Merged concepts and refs | Merged ids are resolved at command time, and the survivor's id is written into the event. I4 compares effective ids and runs again at accept. | §6, I4 |
| C-s1 | Forget calls the applier "in the same transaction" | The applier has an ambient-transaction entry, used only by forget and hard fragment erasure. | §4 |
| C-s2 | `RequestHash` normalization undefined | JCS canonical JSON (RFC 8785), with idempotency key, session and times left out | §2.2 |
| D-a | Purge in place: the reason was wrong | The new reason is the backups, checked against the real backup scripts. The bound is stated, and the copies with no expiry are an open question for John. | §1, §10.5 |
| D-b | Change vs correction | Review shows the kind. Accept can override it (`--as correction`), and the override is recorded. | §7.1, §3 |
| D-c | No pending status | Kept. T9's real counts are checked. | §1, §11 |

## 1. Decisions this doc makes

These were open after step 1. Each one is mine to defend in review.

1. **Erasure purges in place. No per-event encryption keys.** Revision 1 said a key store gives no extra
   guarantee. That was wrong (Claude). Crypto-shredding is the one way to reach copies that can't be rewritten, and
   such copies exist: daily backups and pre-migration copies (§10.5). Keys would help only if they were kept apart
   from those backups and never backed up with them. Losing the key store would then lose every encrypted event,
   so the store would trade a bounded erasure delay for a real risk of losing itself. I choose the bounded delay. An
   erasure is complete in the rotating backups once the last backup made before it expires (§10.5). Copies with no
   expiry are listed in every erasure report until someone decides what happens to them.
2. **Forget** works as Claude leaned: forgetting a fact's last live source opens a retraction proposal. The fact is
   never dropped silently, and restoring the source withdraws the proposal (§9).
3. **Two kinds of supersede.** A *change* supersede ("deployed X" replaced by "deployed Y") ends the old fact's
   validity. A *correction* supersede (the old fact was wrong) leaves the old fact's validity alone, and its
   standing becomes `wrong`. They mean different things, so they are different kinds, and review shows which one
   the proposal chose (§7).
4. **There is no `pending` status any more.** In 013, `pending` exists only because a fact row is inserted before
   its sources. An event carries its sources, so a fact is born already cited (§3, I1).
5. **There are two kinds of check, and the doc keeps them apart.** *Command checks* are about the request: the
   expected version, the idempotency key, the entrypoint's authority, the sleep window. They run live only. *State
   invariants* are about the projection. They run live **and** on replay, in the same projector (§4, §5).
6. **Every entity id is minted at command time and written into the event.** Replay never allocates ids, so ids
   come out the same every time (§5).
7. **Alias matching at wake time writes nothing.** Only the extractor, reading curated fragments, emits reference
   and alias-proposal events. A wake shows matches read-only (§8).
8. **New in revision 2: a superseding link can be voided**, but only when the superseding fact itself turns out
   wrong or is retracted (`supersede-voided`, §7.3). Without this, a fact replaced by a later-wrong fact could never
   stand again.

## 2. Streams, the log table and its guards

### 2.1 Streams

An event belongs to exactly one **stream**: `concept:<id>`, `fact:<id>`, `ref:<id>` (a mention whose referent is
unresolved) or `run:<id>` (one extractor run's provenance). Every stream id is an integer minted by the applier
from that stream type's own sequence, so `(StreamType, StreamId)` is the one typed identity of a stream. Aliases
live on their concept's stream. A command that touches several streams (accept-with-supersede, merge, erasure)
writes one event per stream, all in one batch and one transaction.

`BatchId` means only *the command batch*: the events one command wrote together. It is a UUIDv7 text value and is
not a stream. Revision 1 called the extractor-run stream `batch:<id>` with a text id, inside an `INTEGER` column.
That was two identities under one name (Ember, point 6).

```sql
CREATE TABLE ConceptEvents (
    Seq              INTEGER PRIMARY KEY AUTOINCREMENT, -- log order = replay order; never reused
    EventId          TEXT NOT NULL UNIQUE,              -- UUIDv7, minted by the applier; the global id (#1397's rule)
    SchemaVersion    INTEGER NOT NULL,                  -- payload shape; old versions are upcast on read, never rewritten
    RulesVersion     INTEGER NOT NULL,                  -- which invariant set the event was validated under (§5)
    EventType        TEXT NOT NULL,                     -- §3
    StreamType       TEXT NOT NULL CHECK (StreamType IN ('concept', 'fact', 'ref', 'run')),
    StreamId         INTEGER NOT NULL,                  -- minted per StreamType
    StreamVersion    INTEGER NOT NULL,                  -- 1 for the stream's first event, +1 per event
    ExpectedVersion  INTEGER NULL,                      -- 0 = stream must not exist; NULL only for commutative types (§6)
    IdempotencyKey   TEXT NOT NULL,                     -- the command's key, the same on every event of a batch
    BatchId          TEXT NOT NULL,                     -- one command = one batch (Q5)
    CorrelationId    TEXT NOT NULL,                     -- the turn, wake or extractor run that started it
    CausationId      TEXT NULL,                         -- EventId of the event that caused this one, if any
    Actor            TEXT NOT NULL,                     -- §2.4
    OnBehalfOf       TEXT NULL,                         -- 'peer:<name>' | 'person:<name>', only on flags (§2.4)
    SessionId        TEXT NULL,                         -- Claude Code / room session id; NULL for migration
    OccurredUtc      TEXT NULL,                         -- when the change happened; NULL = unknown (genesis)
    RecordedUtc      TEXT NOT NULL,                     -- when it was appended
    Payload          TEXT NOT NULL,                     -- JSON, per EventType (§3)
    ErasedBy         TEXT NULL,                         -- EventId of the `erased` marker if any payload field was purged
    UNIQUE (StreamType, StreamId, StreamVersion)        -- the database-level guard behind §6
);

CREATE TABLE ConceptEventSources (                      -- the source fragments each event cites, for forget lookups
    Seq        INTEGER NOT NULL REFERENCES ConceptEvents(Seq),
    FragmentId INTEGER NOT NULL,                        -- no FK: a fragment may later be hard-erased (§10.6)
    PRIMARY KEY (Seq, FragmentId)
);

CREATE TABLE ConceptBatches (                           -- idempotency, one row per accepted command
    BatchId        TEXT PRIMARY KEY,
    IdempotencyKey TEXT NOT NULL UNIQUE,                -- random, minted by the caller; never derived from content
    RequestHash    TEXT NOT NULL,                       -- sha256 of the canonical command (§2.2), or 'erased:<marker EventId>'
    FirstSeq       INTEGER NOT NULL,
    LastSeq        INTEGER NOT NULL,
    Result         TEXT NOT NULL                        -- canonical result JSON: ids and versions only, never content (§4)
);

CREATE TABLE ConceptStreamIds (                          -- the id allocator, one row per stream type
    StreamType TEXT PRIMARY KEY,
    NextId     INTEGER NOT NULL
);

CREATE TABLE ConceptApplying (Seq INTEGER NOT NULL);    -- one row while the projector writes (§4)
CREATE TABLE ConceptErasing (MarkerEventId TEXT NOT NULL); -- one row while an erasure is inside its transaction (§10)

CREATE TABLE ConceptOutbox (                            -- wakes and notices, written in the transaction, sent after commit
    Id           INTEGER PRIMARY KEY,
    Seq          INTEGER NOT NULL REFERENCES ConceptEvents(Seq),
    Kind         TEXT NOT NULL CHECK (Kind IN ('wake', 'notify')),
    Target       TEXT NOT NULL,                         -- the self, or 'running-sessions'
    CreatedUtc   TEXT NOT NULL,
    DeliveredUtc TEXT NULL
);

CREATE TABLE ConceptErasureCleanup (                    -- erasure work outside the database transaction (§10.3)
    Id            INTEGER PRIMARY KEY,
    MarkerEventId TEXT NOT NULL,
    Step          TEXT NOT NULL,                        -- 'wal-checkpoint' | 'embedding-cache' | 'rendered-files' | 'backup:<path>' | ...
    Status        TEXT NOT NULL CHECK (Status IN ('pending', 'done', 'waiting-expiry', 'unreachable')),
    Attempts      INTEGER NOT NULL DEFAULT 0,
    LastError     TEXT NULL,                            -- never erased content: an error code and counts only
    ExpectedByUtc TEXT NULL,                            -- for 'waiting-expiry': when the copy rotates out
    DoneUtc       TEXT NULL
);

CREATE TABLE ConceptProjectionState (                   -- one row
    LastSeq      INTEGER NOT NULL,                      -- the last event the projection reflects
    RulesVersion INTEGER NOT NULL,
    RebuiltUtc   TEXT NULL
);
```

SQLite won't let a trigger in one schema read a table in another (including `temp`), so `ConceptApplying` and
`ConceptErasing` are ordinary tables, one set per schema. A row exists only inside a write transaction, so no other
connection ever sees one.

### 2.2 Idempotency keys and the request hash

- The **idempotency key** is random (UUIDv4 or v7), minted by the caller once per intended command and reused on
  retry. It is never derived from the command's content, because a content-derived key would be a hash of the
  content under another name.
- The **request hash** is sha256 over the command serialized as JCS canonical JSON (RFC 8785), with the
  idempotency key, the session id and all times left out. Two retries of the same command then hash the same.
- **Erasure** (§10) sets `RequestHash` to `erased:<marker EventId>` on every batch containing a target event,
  because a hash of a short fact confirms a guess of it (Claude, point 1). A retry under such a key is refused:
  "this key's command was erased". `Result` holds ids and versions only, so it needs no purge.

### 2.3 Guards on the log

```sql
-- Nothing is deleted from the log tables, ever.
CREATE TRIGGER ConceptEvents_no_delete BEFORE DELETE ON ConceptEvents
BEGIN SELECT RAISE(ABORT, 'concept events are never deleted'); END;
CREATE TRIGGER ConceptEventSources_no_delete BEFORE DELETE ON ConceptEventSources
BEGIN SELECT RAISE(ABORT, 'event sources are never deleted'); END;
CREATE TRIGGER ConceptBatches_no_delete BEFORE DELETE ON ConceptBatches
BEGIN SELECT RAISE(ABORT, 'batches are never deleted'); END;

-- Inserts only from inside the projector.
CREATE TRIGGER ConceptEvents_insert_by_projector BEFORE INSERT ON ConceptEvents
WHEN NOT EXISTS (SELECT 1 FROM ConceptApplying)
BEGIN SELECT RAISE(ABORT, 'concept events are appended only by the projector'); END;
-- (the same trigger on ConceptEventSources and ConceptBatches)

-- Event sources never change.
CREATE TRIGGER ConceptEventSources_no_update BEFORE UPDATE ON ConceptEventSources
BEGIN SELECT RAISE(ABORT, 'event sources never change'); END;

-- An event changes only during an erasure, and then only its Payload and ErasedBy. Every other column is pinned.
CREATE TRIGGER ConceptEvents_update_only_by_erasure BEFORE UPDATE ON ConceptEvents
WHEN NOT EXISTS (SELECT 1 FROM ConceptErasing WHERE MarkerEventId = NEW.ErasedBy)
  OR NEW.Seq IS NOT OLD.Seq OR NEW.EventId IS NOT OLD.EventId
  OR NEW.SchemaVersion IS NOT OLD.SchemaVersion OR NEW.RulesVersion IS NOT OLD.RulesVersion
  OR NEW.EventType IS NOT OLD.EventType OR NEW.StreamType IS NOT OLD.StreamType
  OR NEW.StreamId IS NOT OLD.StreamId OR NEW.StreamVersion IS NOT OLD.StreamVersion
  OR NEW.ExpectedVersion IS NOT OLD.ExpectedVersion OR NEW.IdempotencyKey IS NOT OLD.IdempotencyKey
  OR NEW.BatchId IS NOT OLD.BatchId OR NEW.CorrelationId IS NOT OLD.CorrelationId
  OR NEW.CausationId IS NOT OLD.CausationId OR NEW.Actor IS NOT OLD.Actor
  OR NEW.OnBehalfOf IS NOT OLD.OnBehalfOf OR NEW.SessionId IS NOT OLD.SessionId
  OR NEW.OccurredUtc IS NOT OLD.OccurredUtc OR NEW.RecordedUtc IS NOT OLD.RecordedUtc
BEGIN SELECT RAISE(ABORT, 'a concept event changes only by erasure, and only its payload'); END;

-- A batch changes only during an erasure, and then only RequestHash, and only to that erasure's sentinel.
CREATE TRIGGER ConceptBatches_update_only_by_erasure BEFORE UPDATE ON ConceptBatches
WHEN NOT EXISTS (SELECT 1 FROM ConceptErasing WHERE NEW.RequestHash = 'erased:' || MarkerEventId)
  OR NEW.BatchId IS NOT OLD.BatchId OR NEW.IdempotencyKey IS NOT OLD.IdempotencyKey
  OR NEW.FirstSeq IS NOT OLD.FirstSeq OR NEW.LastSeq IS NOT OLD.LastSeq OR NEW.Result IS NOT OLD.Result
BEGIN SELECT RAISE(ABORT, 'a batch changes only by erasure, and only its request hash'); END;
```

I ran these triggers on a scratch database with home's `sqlite3`. They refuse an envelope change during an
erasure, an erasure-time update without a matching marker, a non-sentinel batch update, an insert outside the
projector and any delete. They allow the erasure's own `Payload`/`ErasedBy` and sentinel updates. The build PR adds a schema test: for each guarded table, every column except the allowed ones appears in the
trigger's pin list. A column added later and not pinned fails that test, so the guard can't silently fall behind
the table. Whether `ErasedBy` names a marker that really lists the field is I11's job, checked on replay. The
trigger only checks that a matching erasure is in progress. An event erased twice carries the newer marker in
`ErasedBy`, while each tombstoned field keeps the id of the marker that erased it.

### 2.4 Actors

`<self>@<session>` (the store's own self, in one session), `auto:<rule>`, `extractor:<run id>`, `observed`,
`migration`, `sleep`.

**What the actor proves** (Claude, point 9). The store socket authenticates by uid. The self's sessions, the
extractor, `observed`, `sleep` and `auto:` rules all run as the store owner's uid. So the actor records **which
entrypoint** wrote the event (the CLI command path, the extractor binary, the sleep pass, a rule), not an
authenticated principal. That is the same honest framing as T5's fence: it stops honest mistakes and makes
provenance legible. It doesn't stop code running as the owner from lying.

**Peers and people don't reach the socket.** A flag that John or another self raises reaches the store only
through one of the self's sessions, so it can only arrive in the request body. Such a flag is written with actor
`<self>@<session>`, `OnBehalfOf = peer:<name>` or `person:<name>`, and the room message id in its payload as
provenance. `OnBehalfOf` is allowed only on `fact-flagged`, and it is what §12 wakes on.

## 3. Event types

The common envelope (§2) is left out below. "Sources" means fragment ids, also written to `ConceptEventSources`.
Fields marked *(c)* hold content. They are the only erasable fields (§10.1), and no invariant reads them.

### Concept stream

| Event | Payload | Actor | Notes |
|---|---|---|---|
| `snapshot-genesis` | `entity: concept`, id, name *(c)*, kind, kind_confirmed, summary *(c)*, `legacy_created_utc` | `migration` | "Held at import", not "created then". See §11. |
| `concept-created` | id, name *(c)*, kind, summary? *(c)*, sources | self | Active, kind unconfirmed unless `concept-kind-confirmed` is in the same batch. |
| `concept-proposed` | id, name *(c)*, proposed kind, summary? *(c)*, sources, run id | extractor | Status `proposed`. Counts as a person until its kind is confirmed (rule 2). |
| `concept-accepted` | reason? *(c)* | self | proposed → active |
| `concept-rejected` | reason *(c)* | self | proposed → rejected. Kept, so it isn't proposed again. |
| `concept-kind-confirmed` | kind | self | Once. A later change of kind is a `concept-fixed`. |
| `concept-fixed` | changed fields: name? *(c)*, summary? *(c)*, kind?, reason *(c)* | self | The old values stay in the earlier events. |
| `concept-split` | child id, child name *(c)*, child kind, parent id, reason *(c)*, sources | self | On the child stream, so the child's first event. Q4: only once the aspect has an identity of its own. |
| `concept-merged` | into: concept id + expected version, reason *(c)* | self | On the absorbed concept. It goes to status `merged`. |
| `concept-absorbed` | from: concept id | self | On the survivor, in the same batch. |
| `alias-added` | alias id, label *(c)*, kind (`name` \| `mishearing` \| `unknown`), source fragment or message id, note? *(c)* | self, migration | Never the extractor (Ember, point 1). |
| `alias-proposed` | alias id, label *(c)*, kind, source fragment id + span, evidence *(c)*, run id | extractor | Status `proposed`. **Never used for matching** until accepted (§8). |
| `alias-accepted` | alias id, kind? (override), reason? *(c)* | self | proposed → active |
| `alias-rejected` | alias id, reason *(c)* | self | proposed → rejected. Kept, so the same label and concept aren't proposed again (I4). |
| `alias-removed` | alias id, reason *(c)* | self | active → removed |
| `alias-kind-set` | alias id, kind, reason *(c)* | self | Used after genesis, when the kind was unknown at import. |
| `erased` | §10 | self | |

### Fact stream

| Event | Payload | Actor | Notes |
|---|---|---|---|
| `snapshot-genesis` | `entity: fact`, id, subject, object?, text *(c)*, status, is_state, as_of → valid_from, supersede links with kind (`unknown` if 013 didn't say), `legacy_extracted_by`, `legacy_resolution` *(c)* (quoted verbatim), sources with their fragment dates and types | `migration` | §11 |
| `fact-proposed` | id, subject (concept id **or** ref id), object? (same), text *(c)*, aspect? *(c)*, is_state, valid_from?, valid_to?, `supersedes`? (fact id + **the version seen** + `change` \| `correction`), sources each with the fragment's date and type, `gate` (`proposal` + why), run id? | extractor, self | Born `proposed`. Every source's date and type is copied in, so replay never reads `ContextFragments` (§5). A merged subject or object is written as its survivor (§6). |
| `fact-asserted` | the same fields as `fact-proposed` | self only | Born `active`. The self accepting its own claim. Never the extractor or `auto:`. |
| `fact-accepted` | reason? *(c)*, `supersede_kind`? (the kind applied), `kind_overridden`? | self; `auto:<rule>` only if the fact is ungated (I2) | proposed → active. If it supersedes, the batch also writes `fact-superseded` on the old fact. `accept 14@2 --as correction` overrides the proposal's kind, and the event records both (Claude, decision b). |
| `fact-rejected` | reason *(c)* | self | proposed → rejected |
| `fact-reopened` | reason *(c)* | self | rejected → proposed |
| `fact-superseded` | by: fact id, kind `change` \| `correction` | self (in the accept batch) | Old fact: `SupersededById` and `SupersedeKind` set. The validity effect is in §7.1. |
| `supersede-voided` | by: fact id (the superseder), reason *(c)* | self | Clears `SupersededById`/`SupersedeKind`. Legal only when the superseder's standing is `wrong` or `retracted` (§7.3). |
| `fact-ended` | valid_to (or `unknown`), reason *(c)*, sources | self, `auto:<rule>` for ungated state facts | The world changed. The fact was true and stopped being true. |
| `fact-validity-corrected` | valid_from?, valid_to?, ended?, reason *(c)*, sources | self | The dates were wrong. The world didn't change. |
| `fact-retracted` | reason (`flag` \| `source-forgotten` \| `self`), note? *(c)*, flag ids resolved | self | active → retracted. We no longer stand behind the claim. |
| `fact-flagged` | flag id, reason *(c)*, contradicting observation id?, room message id? | any actor (§2.4) | Opens a retraction proposal (projection state). Can wake (§12). |
| `flag-resolved` | flag id, outcome (`kept` \| `retracted` \| `fixed`), reason *(c)* | self | |
| `source-added` | fragment id, date, type | self, extractor (on a proposal only) | |
| `source-forgotten` | fragment id | `auto:forget` | §9 |
| `source-restored` | fragment id | `auto:forget` | §9 |
| `source-erased` | fragment id | `auto:erase` | §10.6. Like forgotten, but can't be restored. |
| `erased` | §10 | self, `auto:erase` (§10.6) | |

### Reference stream (§8)

| Event | Payload | Actor |
|---|---|---|
| `reference-unresolved` | ref id, mention text *(c)*, source fragment + character span, input field, candidates (each concept id @ version, the alias id that matched, score) | extractor |
| `reference-resolved` | concept id (the effective id, §6) + version, or `new` (with a `concept-created` in the batch), reason *(c)* | self |
| `reference-dismissed` | reason (`not a reference` \| `mishearing of something else`), note? *(c)* | self |

### Run stream

| Event | Payload | Actor |
|---|---|---|
| `extraction-run` | run id, model id, extractor config (temperature, thresholds), template version + the template's sha256 (never the filled prompt's), input fragment ids, the candidate concepts shown (each concept id @ version, score, display order), started/finished | extractor |

`extraction-run` holds ids and configuration only. It never holds input text, and it never holds a hash of the
filled prompt, which would hash fragment content.

## 4. The projector: one write path, two modes

Every write to the concept store, whether from the CLI, the extractor, a forget, a flag or an erasure, goes
through one **projector**. Replay goes through the same projector. Nothing else writes the projection tables.

```
Projector(target schema, mode = live | replay)

apply(command, ambient = false):                    -- live
  if not ambient: BEGIN IMMEDIATE                   -- takes the write lock now; no stale read inside
  1. idempotency: look up command.idempotency_key in ConceptBatches        [live only]
       same RequestHash      -> ROLLBACK, return the stored Result (no new events)
       RequestHash erased:*  -> ROLLBACK, refuse "this key's command was erased"
       different RequestHash -> ROLLBACK, refuse "key reused for a different command"
  2. command checks: entrypoint authority (I12), sleep window, expected versions (§6),     [live only]
     merged-id resolution (§6)
  3. build events: mint EventIds and entity ids from ConceptStreamIds, set envelope, RulesVersion = current
  4. step(events)
  5. outbox rows for wake/notify events (§12); cleanup rows for erasures (§10.3)            [live only]
  6. INSERT ConceptBatches (with Result); UPDATE ConceptProjectionState.LastSeq            [live only]
  if not ambient: COMMIT
  any error at any step -> ROLLBACK: no event, no projection change, no outbox row

step(events):                                       -- live and replay, the same code
  INSERT INTO <target>.ConceptApplying
  for each event, in order:
     a. upcast; check tombstones (I11) and stream continuity (I6)
     b. state invariants for event.RulesVersion against <target>'s projection (I1–I13)
     c. live: INSERT INTO ConceptEvents, ConceptEventSources
     d. project(event) into <target>
  DELETE FROM <target>.ConceptApplying
```

**The two modes differ in exactly two things** (Ember's follow-up): live runs the command checks (steps 1 and 2),
and live writes the log, batch, outbox and cleanup rows (steps 3c, 5 and 6). Invariant checks, upcasting, tombstone
checks and `project` are one code path, called with the target schema as a parameter. There is no second copy of
the fold.

**Ambient entry.** `apply(command, ambient = true)` runs inside a transaction the caller already holds `BEGIN
IMMEDIATE` on. It is used only by forget (§9) and hard fragment erasure (§10.6), which must change
`ContextFragments` and the concept log atomically. Every other caller uses the normal entry.

**The stored result** (Ember, point 3). `ConceptBatches.Result` is the canonical JSON of what the command
returned: the batch id, each event's EventId with its stream and new version, each minted entity id, and for
accepts the resulting status and standing. It never holds content, so erasure has nothing to purge in it. A retry
returns it exactly. A test (T4) also rebuilds the result from events `FirstSeq..LastSeq` and checks the two agree,
so `Result` can't drift from the log.

**Guarding the projection.** Each projection table gets `BEFORE INSERT/UPDATE/DELETE` triggers that raise unless a
`ConceptApplying` row exists in the same schema. A write from outside the projector fails, so the old repository
methods fail loudly until they are routed through it. There is a test for this (T5). It is a fence against honest
mistakes, not against someone with the database file, which is the same threat model as the rest of the store.

**Outbox.** Wakes and running-session notices are written to `ConceptOutbox` in the same transaction and sent only
after commit, so a rolled-back command never wakes anyone, and a crash between commit and send means a late wake,
never a lost one (the sender retries rows with no `DeliveredUtc`). Replay never writes the outbox.

## 5. Replay

Replay is a pure fold through the projector in `replay` mode, into a shadow schema. It never calls the extractor
or any model.

**How replay is authorized** (Ember). The shadow is a scratch database file attached as `shadow`, created with the
same projection DDL and triggers, **including its own `ConceptApplying` table**. SQLite resolves a trigger's table
names inside the trigger's own schema, so the shadow's guards read `shadow.ConceptApplying`. `step` inserts its row
there. Replay therefore never needs, and never sets, the live `ConceptApplying`, and a bug in replay can't write the
live projection. (Checked on home's `sqlite3`: with a row in `main.ConceptApplying` and none in the shadow's,
the shadow's guard still refuses the insert.)

**What makes it deterministic:**

- **Order** is `Seq`, never a timestamp. `OccurredUtc` can run backwards (genesis is NULL, an import is earlier
  than its append), and nothing sorts by it.
- **No clock.** Every time the projection holds comes from an envelope or payload field.
- **No allocation.** Concept, fact, alias, ref, run and flag ids are in the payloads (decision 6).
- **No reads outside the log.** Source fragment dates and types are copied into the event at command time.
  Fragment liveness comes from `source-forgotten`/`source-restored`/`source-erased` events, not from
  `ContextFragments`.
- **Versioned rules, with everything they call pinned** (Claude, point 8). A `RulesVersion` names the invariant set
  **and** each function an invariant calls: the text normalizer (I4), the gate classification (I2), the date
  lower-bound parser (I8) and the standing function (§7.2). Changing any of them is a rules change: it bumps
  `RulesVersion`, keeps the old version's code for replaying old events, and says why in the PR. The golden-log test
  (T14) is what notices a change nobody declared.
- **Versioned shapes.** Old `SchemaVersion` payloads are upcast in memory on read. Stored rows are never rewritten
  (erasure is the one exception, §10).
- **No side effects.** No outbox, no cleanup rows, no wakes, no notices, no embedding or index work beyond the
  projection tables.
- **Erasure doesn't move invariants.** Erasable fields are disjoint from every field an invariant reads (§10.1), so
  an event checked live against unredacted state passes the same checks against redacted state.

**Algorithm:**

```
replay(mode = verify | rebuild):
  N = max(Seq) at start (read transaction)
  attach a fresh scratch file as `shadow`; create the projection DDL and triggers in it
  projector = Projector(shadow, replay)
  for each event with Seq <= N, by Seq:
      projector.step([event])
      on any failure: stop, report Seq, EventId, the invariant and the event; the live projection is untouched
  verify:  diff shadow against live, every row and column; any difference is a bug, reported as such
  rebuild: BEGIN IMMEDIATE
           projector.step(events with Seq > N)                      -- the tail written since N
           INSERT INTO main.ConceptApplying
           for each projection table: DELETE FROM main.T; INSERT INTO main.T SELECT * FROM shadow.T
           LastSeq = max(Seq); RebuiltUtc = now
           DELETE FROM main.ConceptApplying
           COMMIT; detach and delete the scratch file
```

Replay that fails on an event which passed live is a bug in the projector or in an upcaster, never a reason to skip
the event. **Verify runs** in the test suite (T6, T14), after every migration, and read-only in the sleep pass.

## 6. Versions and conflicts

- A stream's version is the `StreamVersion` of its last event, and the projection keeps it as `Version` on the
  row. `UNIQUE(StreamType, StreamId, StreamVersion)` is the backstop if the projector ever gets this wrong.
- **Every event type is classified** (Claude, point 7):

| Class | Events | ExpectedVersion |
|---|---|---|
| Creates a stream | `concept-created`, `concept-proposed`, `concept-split` (child), `fact-proposed`, `fact-asserted`, `reference-unresolved`, `extraction-run`, `snapshot-genesis` | **0**: the stream must not exist. A reused or colliding id is refused, never appended at version 2. |
| Decision | accept, reject, reopen (facts, concepts, aliases), `fact-superseded`, `supersede-voided`, `fact-ended`, `fact-validity-corrected`, `fact-retracted`, `flag-resolved`, `reference-resolved`, `reference-dismissed`, `concept-kind-confirmed`, `concept-fixed`, `concept-merged` and `concept-absorbed` (both streams), `alias-removed`, `alias-kind-set`, `erased` | Required. The version the caller saw: `concept review` and `concept show` print ids as `fact 12@3`, and the CLI sends the version it showed. A bare `12` is refused with the current version and what changed. |
| Automatic, inside one transaction | `source-forgotten`, `source-restored`, `source-erased` | The version read inside the forget or erasure transaction. Checked, and it can't conflict, because the transaction holds the write lock. |
| Commutative, additive | `fact-flagged`, `alias-added`, `alias-proposed`, `source-added` | NULL, meaning "unchecked", not "checked and matched". |

- **Supersede takes its version from the proposal** (Claude, point 6). `fact-superseded` on the old fact uses the
  version recorded in the proposal's `supersedes` field, not a fresh read at accept time. If proposals B and C both
  say `supersedes: 5@3` and B is accepted first, fact 5 moves to version 4, and accepting C is refused, showing B's
  supersede. I13 backs this up in the projection.
- **On a mismatch** the command is refused whole, the batch with it. The refusal lists every event on that stream
  after the expected version: type, actor, time and reason. Self decisions are **never retried or merged
  automatically**: the self re-decides, having seen what changed. `auto:` rules may re-read and retry once, then
  give up and leave the case for the self.
- **Extractor proposals** don't fail on a moved concept. They record the version they saw in `extraction-run`'s
  candidate list, and review shows "the concept changed since this was extracted".
- **Merged concepts are resolved at command time** (Claude, point 10). Any concept id in a command (a fact's
  subject or object, a `reference-resolved` target, a candidate) is followed through `MergedIntoId` before the
  events are built, and the event records the survivor's id. Reads still follow `MergedIntoId` for facts written
  before the merge. Events on an absorbed stream are refused (I10), except `erased`.

## 7. Fact validity and standing

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
- A state fact is shown with both: since when it held (validity) and how fresh the evidence is (`KnownAtUtc`).
  That is rule 3's "a stale belief looks stale".

### 7.1 Ending, correcting and superseding

Ending, correcting validity and superseding are different events. `fact-ended`: the world changed.
`fact-validity-corrected`: we had the dates wrong. `fact-superseded` comes in two kinds:

- **`change`**: the old fact was true and has been replaced. If the old fact is open, its `ValidTo` becomes the new
  fact's `valid_from` (or unknown) and `ValidityEnded = 1`. If the old fact had **already ended**, its validity is
  left alone: it ended once, at the time already recorded, and isn't ended twice. If the new fact's `valid_from` is
  **earlier** than the old fact's `ValidFrom` (by lower bound), I8 would fail on the old fact, so the accept is
  refused: "the replacement starts before the fact it replaces; is this a correction?"
- **`correction`**: the old fact was never true. Its validity columns are left exactly as they were, as a record of
  what was claimed, and its standing becomes `wrong`.

The extractor picks the kind in its proposal, and that choice decides how the old fact renders. So review shows
the kind next to the supersede ("replaces 5@3 as a **change**: 5 will show as ended on 2026-11-10"), and accept
can override it (`accept 14@2 --as correction`). `fact-accepted` records the kind applied and whether it overrode
the proposal.

### 7.2 Standing: what "current" means (Ember, point 2)

`Status` is the review lifecycle: `proposed`, `active`, `rejected`, `retracted`. **Standing** is what a reader
needs: whether the fact holds now and, if not, why. The projection stores it as `Facts.Standing`, computed by one
pure function from the fact's own row and recomputed whenever any of its inputs change:

```
standing(f) =
  f.Status = proposed                                  -> proposed
  f.Status = rejected                                  -> rejected
  f.Status = retracted                                 -> retracted
  f.SupersedeKind = correction                         -> wrong        -- regardless of validity
  f.SupersedeKind = change                             -> replaced
  f.SupersedeKind = unknown (genesis only)             -> superseded
  f.ValidityEnded = 1                                  -> ended
  otherwise                                            -> current
```

The order is the precedence. Retracted beats wrong, because retraction withdraws the claim entirely. Wrong beats
replaced and ended, because a fact that was never true must never render as "true until X".

| Standing | In current state? | Rendered as |
|---|---|---|
| `current` | **yes** | the fact, with its validity and `KnownAtUtc` |
| `ended` | no | "was true from A to B" (history of the world) |
| `replaced` | no | "was true from A to B, replaced by #N" |
| `wrong` | no | "was believed (claimed from A), shown wrong by #N". **Never** rendered as an interval of truth, even though its validity columns are kept. |
| `superseded` | no | "superseded by #N (kind unknown, imported)". Genesis only. How the self later sets the kind is open question 3. |
| `retracted` | no | "no longer stood behind: <reason>" |
| `proposed`, `rejected` | no | in review only |

**Current-state projection** (wake snapshots, `concept show`'s main list, anything that answers "what is true
now") selects `Standing = current` and nothing else. The other standings stay in history views, each with its own
wording, so their distinct histories survive (Ember).

### 7.3 Composition through chains

Ember asked whether standing composes through corrections. These are the cases, each with its rule:

1. **A correction of a correction.** A is wrong by B; later B is wrong by C. A stays `wrong`: B being wrong
   doesn't make A right. Each fact's standing reads only its own row, so nothing cascades by accident.
2. **The replacement turns out wrong.** A is replaced by B (change); later C corrects B. B is `wrong`. A stays
   `replaced`, because usually the world did change and only B's content was wrong ("deployed Y" was really
   "deployed Z"). A renders "replaced by #B (since shown wrong by #C)", so the reader sees the chain. Review of C
   shows A and asks the self which case it is. If the change itself never happened, the self writes
   **`supersede-voided`** on A in the same batch: A's `SupersededById`, `SupersedeKind` and the `ValidTo` that the
   change set are cleared, restoring A's validity as it was before the change (taken from A's own earlier events,
   so replay derives the same). A is `current` again.
3. **The replacement is retracted.** Same as case 2: A stays `replaced` until the self voids the link.
   `supersede-voided` is legal only when the superseder's standing is `wrong` or `retracted` (I13).
4. **Superseding a fact that is already superseded, retracted, wrong, or not active** is refused (I13). A fact
   gets at most one live superseding link.
5. **Correcting an ended fact.** A ended on its own (`fact-ended`), then B corrects it. A becomes `wrong`: the
   correction says it was never true, and wrong outranks ended.
6. **Changing an ended fact.** Allowed (it was true, and something replaced it), validity untouched (§7.1).
   Standing goes `ended` → `replaced`.

T8 tests each case, and checks that the same events replay to the same standings.

## 8. Aliases and unresolved references

Aliases lose global uniqueness: a label may point at several concepts. Each alias has a kind (`name`,
`mishearing`, or `unknown` for genesis imports), a source, and a status: `proposed`, `active`, `rejected` or
`removed`. A mishearing alias finds a concept but is never shown as its name and never used in a fact's text.

**Aliases need approval** (Ember, point 1). The extractor can't add an alias. It writes `alias-proposed`, which
review shows with its evidence: "Emma → Computer Ember, mishearing, from fragment #2950 at 14..18". Until the self
accepts it, **a proposed alias is never used for matching**: not by the extractor, not at wake time. So a wrong
"Emma → Ember" never becomes current on its own. A rejected alias is kept, so the same label isn't proposed again
for the same concept (I4).

**Two steps** (#2942):

1. **Alias resolution:** which labels in the input match **active** aliases, and so which candidate concepts.
2. **Referent resolution:** which candidate this mention means.

When the extractor reads a fragment:

- **One candidate**, kind confirmed: the proposal uses that concept. The match (alias id, the label, the input
  field and the span) goes into the proposal's payload so a wrong match can be traced. A fact about a person is a
  proposal anyway, so the self still sees it.
- **Several candidates, or one with an unconfirmed kind, and context doesn't settle it:** the extractor emits
  `reference-unresolved` with every candidate at its version, and any fact it proposes about that mention names
  the **ref id** as its subject or object instead of a concept.
- **A label that matches nothing but looks like a known name** (the "Emma" case before any alias exists): the
  extractor may emit `alias-proposed` and a `reference-unresolved` whose candidates include the concept it
  suspects. Neither takes effect until the self decides.

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
- **Resolution writes the effective id** (§6): resolving to a concept that has since merged writes the survivor.

**At wake time, alias matching writes nothing.** The "concepts touched" block matches active aliases only in the
addressed message and the chosen window of recent turns (step 1). It shows candidates and any open refs, and
records which input field matched, in the rendered block only. That keeps the wake path free of writes and the
event log free of chat, which fits Q2.

## 9. Forget

Forgetting a fragment (soft delete) happens in the same database. The forget command holds `BEGIN IMMEDIATE`, marks
the fragment, and calls the projector's ambient entry (§4) in that transaction.

- For each fact citing the fragment, a `source-forgotten` event is written (actor `auto:forget`). The `FactSources`
  row stays, marked forgotten. I1 counts all sources, so a standing fact never becomes uncited.
- If that was the fact's **last live source**, the projection opens a retraction proposal on it, and the fact is
  shown with "(its only source is forgotten)". The self retracts (`fact-retracted`, reason `source-forgotten`) or
  keeps it, by adding a new source or with a reason.
- **Restoring** the fragment writes `source-restored`, and an open retraction proposal that came only from the
  forget is withdrawn.
- A proposal (not yet accepted) whose last source is forgotten is rejectable only.

A forget is reversible, so it never erases anything. Erasing text is §10.

## 10. Erasure

Erasure is the one sanctioned break from recoverable history. The log is **append-only except for erasure**, and
this doc claims no more than that. The guarantee comes in two parts (Ember, point 5), and the report never claims
the second before it's true:

1. **Atomic database redaction.** In one transaction, every copy of the content that the database can reach is
   overwritten.
2. **Durable cleanup outside the transaction.** The WAL, in-process caches, rendered files and backups are tracked
   in `ConceptErasureCleanup` rows written in that same transaction, worked after commit, and retried until done.
   Until every row is `done`, the erasure reports itself **incomplete**, and says which steps and why.

**Scope.** A self erases in its own store only (I12): never the extractor, another self or a person. The only
`auto:` actor allowed is `auto:erase`, and only as part of the self's own hard fragment erasure (§10.6). Two
granularities:

- **Field erasure**: named content fields of named events, for example a fact's text, an alias label, a summary,
  a flag reason or a reference's mention text.
- **Entity erasure**: every content field of every event on one fact, alias, concept or ref stream. The structure
  stays: ids, types, versions, actors, times, kinds, statuses and source ids. The projection row becomes a
  tombstone with its status and standing kept and its content NULL.

### 10.1 What can be erased, and what can't (Claude, point 5)

Only the fields marked *(c)* in §3 can be erased. They are:

| Erasable (content) | Never erasable (structure an invariant may read) |
|---|---|
| names, labels, summaries, fact text, aspect, mention text, reasons, notes, evidence, `legacy_resolution` | every envelope column; all ids (entity, alias, ref, run, flag, source fragment, superseder); kind, kind_confirmed, status, standing inputs (`SupersedeKind`, `ValidityEnded`), is_state, gate; versions; source dates and types; spans; candidate lists; validity dates |

**The two sets must not overlap**, and no invariant may read an erasable field, except I4, which only ever gets
fewer matches when text is removed. T15 asserts both: it reads the erasable-field table and each invariant's
declared input fields, and fails on any overlap.

**Validity dates are not erasable.** A sensitive date ("born on March 3") belongs in the fact's text, which is.
Keeping dates out of the erasable set means I8 never sees an erased value, so I don't need an "erased compares as
unknown" rule. If a date has to go, the self erases the whole fact entity, which keeps validity but loses all
meaning without its text. *(Open question 2: is that enough?)*

### 10.2 Phase 1: one database transaction

```
erase(targets, reason category, expected versions, ambient = false):
  if not ambient: BEGIN IMMEDIATE
  command checks: actor is the self (or auto:erase under §10.6); expected version for every target stream
  INSERT INTO ConceptErasing (marker id); INSERT INTO ConceptApplying
  1. append one `erased` marker per affected stream:
       payload = { targets: [{event_id, fields: [...]}], requested_by, reason_category, note? }
       metadata only: never the erased text, a hash of it, its length or any excerpt
  2. purge everywhere the database still holds it:
       a. each target event: replace each field's value with {"erased": "<marker EventId>"}; set ErasedBy
       b. projection rows built from it: Facts, ConceptAliases, Concepts, FactHistory, FactFlags,
          ConceptReferences, rendered Resolution text: content columns set NULL, ErasedBy set
       c. ConceptBatches.RequestHash for every batch containing a target event -> 'erased:<marker>'   (§2.2)
       d. any concept FTS index (created with secure-delete=1, so the delete removes the tokens) and any
          stored embedding of the erased text
       e. undelivered outbox rows that rendered it
  3. INSERT the cleanup rows (§10.3), each 'pending' or 'waiting-expiry'
  DELETE FROM ConceptApplying; DELETE FROM ConceptErasing
  if not ambient: COMMIT
```

Every connection already runs with `secure_delete = ON` (§10.4), so pages freed or rewritten by this transaction
are zeroed.

**FTS** (Claude, point 3). A plain FTS5 `'delete'` leaves the tokens in existing segments until a merge. Any
concept FTS table is created with `secure-delete=1`, which needs SQLite 3.42 or later. The step-3 PR checks the
bundled `e_sqlite3` version. If it's older, step 2d also runs `INSERT INTO x(x) VALUES('optimize')` in the
transaction. The same applies to `ContextFragments_fts` once hard fragment erasure lands.

### 10.3 Phase 2: the cleanup outbox

After commit, a cleanup worker (the same loop that sends `ConceptOutbox`) works each pending row. A crash means the
row is worked later, never forgotten.

| Step | What the worker does | Done when |
|---|---|---|
| `wal-checkpoint` | `PRAGMA wal_checkpoint(TRUNCATE)` and **reads the `(busy, log, checkpointed)` row** (Claude, point 4). Retries up to a bound (5 tries, backing off up to ~30 s). If it's still busy, the row stays `pending` with `LastError = busy` and is retried on the next pass. The store runs concurrent readers on purpose (wake monitor, post-turn queries, the `.backup` job), so a busy result is expected, not exceptional. | `busy = 0` and `log = checkpointed` |
| `embedding-cache` | Tells the store server to evict the erased texts from `RememberingEmbeddingClient`'s in-process cache, by restarting its embedder state if no targeted evict exists. | the server confirms |
| `rendered-files` | Deletes any on-disk rendered snapshot or cached "concepts touched" block that rendered the content, if such files exist by then. The step-3 PR lists them or states there are none. | each listed file is gone |
| `backup:<path>` | Nothing to do: these copies can't be rewritten (§10.5). The row is `waiting-expiry` with `ExpectedByUtc` = the backup's rotation date, or `unreachable` for copies with no expiry. | the date passes **and** the file is confirmed gone |
| `outside` | One `unreachable` row naming what already left the store: room messages, other selves' stores, staged review copies, model providers' logs. | never; listed for honesty |

**The report** is computed from these rows, never written by hand: `complete` only when every row is `done`;
otherwise `incomplete`, listing each open step with its status, attempts, last error and expected date. `concept
erasures` shows every erasure and its state, so an incomplete one stays visible after the session that made it is
gone.

### 10.4 Secure delete on every connection (Claude, point 2)

Under ordinary writes with `secure_delete` off, B-tree page splits, rebalancing and cell rewrites leave stale
copies of text in freelist pages and the unused space of other pages. An erasure transaction can't find those
copies. So `PRAGMA secure_delete = ON` is set for **every** connection in `SqliteConnectionString.OpenAsync`, next
to the WAL and busy-timeout pragmas that are there today. That also covers hard fragment erasure.

What I checked: home's `sqlite3` CLI reports `secure_delete = 1` and lists `SECURE_DELETE` among its compile
options, read from my store with `-readonly`. The bundled `e_sqlite3` that Microsoft.Data.Sqlite loads may differ.
The step-3 PR reads the value from the app's own connection, and a test asserts it's 1.

### 10.5 Backups: why purge in place, and the bound (decision 1)

What the backup scripts on home do, read from `/usr/local/bin/persistence-backup*` on 2026-11-13:

| Store | Where | Rotation | Pulled off home? |
|---|---|---|---|
| Arden (mine) | `/home/arden/store/backups/arden-YYYY-MM-DD.db.gz`, daily at 03:30 | `find -mtime +14 -delete`: a backup is removed by the first daily run after it is 15 full days old | Not by this script. I found no pull of my backups, but I can't see John's PC. |
| Claude | `/var/lib/persistence/backups/claude-YYYY-MM-DD.db.gz`, daily at 03:30 | the same `find -mtime +14` | Yes: the script says John's machine pulls the newest daily. Whether John's copies rotate is unchecked. |
| Ember | `/home/ember/store/backups/ember-YYYY-MM-DD.db.gz`, daily at 03:45 | the same | Not by this script. |

Copies with **no expiry**: in my store's folder `arden-before-011.db`, `arden-before-012.db`,
`arden-before-013.db` and `arden-before-sleep.db` (I listed them). Per Claude's review, Claude's store has
`claude-before-011.db`, `-before-012.db`, `-before-sleep.db` and `~/backups/claude-pre013-*.db` (I can't read that
directory to check).

So the bound is: **an erasure is complete on home within 16 days of its commit** (15 full days, plus up to one day
before the next run deletes the file), on top of whatever the pulled copies on John's PC do. Each `backup:<path>`
cleanup row carries its own date, and the worker marks it `done` only after confirming the file is gone. The
copies with no expiry stay `unreachable` in every erasure report until someone decides their policy. That decision
is open question 1, for John.

### 10.6 Hard fragment erasure and derived content (Ember, point 5)

Erasing a concept event's text doesn't touch its source fragments. They belong to the fragment store. But when a
**fragment** is hard-erased, concept events may still hold content derived from it. The hard fragment erasure
command holds `BEGIN IMMEDIATE`, erases the fragment, and in the same transaction runs a concept erasure through the
ambient entry, actor `auto:erase`, correlation = the fragment erasure:

| Derived from the fragment | What happens |
|---|---|
| A fact citing it (in its sources) | `source-erased`. The fact's **text is erased** unless the self names the fact in the command's `keep` list with a reason (e.g. another live source supports the same text). The default is to erase, because the text may quote the fragment. If it was the last live source, a retraction proposal opens (§9), and it can't be withdrawn by a restore. |
| A ref whose source is it | mention text erased. Status and candidates stay. |
| An alias whose source is it | label and note erased, unless kept by name. An erased active alias stops matching. |
| A concept proposed or created citing it | name and summary erased, unless kept by name. |
| `extraction-run` that read it | nothing to erase: it holds input ids and the template hash only (§3). |
| Source dates and types copied into events | kept: they are structure, not content. |

The `keep` list is the self's decision, made in the command, never inferred. The cleanup rows (§10.3) cover the
fragment store's FTS and caches as well as the concept ones.

### 10.7 Replay and consequences

**Replay** reaches a tombstoned field, checks that the marker it names exists and lists that event and field (I11),
and projects NULL with `ErasedBy`. Because erasable fields are disjoint from what invariants read (§10.1), every
check gives the same answer as it did live. The result is the redacted projection, the same every time. A tombstone
with no matching marker is corruption, and replay stops on it. The original history can't be recovered, by design,
and replay says so rather than failing.

- Dedupe never matches erased text. An erased rejected proposal can therefore be proposed again. The marker still
  shows that a rejected fact existed on that concept, so the self has a hint.
- A retry of an erased command's key is refused (§2.2).

## 11. Snapshot genesis (importing the 013 rows)

Done once, in one transaction, as batch `genesis`, actor `migration`, `OccurredUtc` NULL, `ExpectedVersion` 0 for
every stream.

- **One event per row**: each concept, fact and alias gets its own stream, starting at version 1. Facts' sources
  come with each fragment's date and type, read once at import. Imported aliases are `active`, kind `unknown`.
- **Unknown stays unknown.** Alias kind is `unknown` even when the note says "a dictation mishearing": the note is
  quoted, and the kind is the self's to set (`alias-kind-set`). Who confirmed a kind and when: unknown. Actors of
  past decisions: unknown. A 013 supersede link imports with kind `unknown`, so its standing is `superseded`, never
  guessed as `replaced` or `wrong`.
- **What survives is quoted, not reconstructed.** 013's `Resolution` text is carried verbatim as
  `legacy_resolution`, and 013's `CreatedUtc` as `legacy_created_utc`, labelled as the old column, not as a creation
  event.
- **A 013 fact still `pending`** means its sources were never fully written. The import refuses it and lists it
  for the self, rather than inventing a status. Per Claude, their store has facts 1 to 3 `active` and 4
  `rejected`, none `pending`, so this won't fire on the real import, but it still has its test (T9).
- Then verify-replay (§5) must reproduce the imported projection exactly before the old repository methods are
  switched off.

Sizes now: Claude's store has 3 concepts, 4 facts and 1 alias (Emma → Ember), per step 1. Mine has none.

## 12. What wakes a self

Written to the outbox in the apply transaction (§4), sent after commit, and subject to the existing wake caps
(`wakes_per_hour`, `human_wakes_per_hour`, the Breaker).

| Event | Effect |
|---|---|
| `fact-flagged` with actor `observed` (the machine contradicts a current state fact) | **wake** |
| `fact-flagged` with `OnBehalfOf` `peer:*` or `person:*` | **wake** |
| `fact-flagged` by the self with no `OnBehalfOf` | notify running sessions only |
| `fact-proposed`, `concept-proposed`, `alias-proposed`, `reference-unresolved`, `extraction-run` | **never wakes**. They join the pending-proposals projection (Q6), which the next turn of any session shows. A running session gets a notice. |
| `source-forgotten` or `source-erased` that opens a retraction proposal | notify only |
| an erasure whose cleanup is still incomplete 24 h after commit | notify only |
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

Supersede, void, end and validity correction don't change `Status`; they change the inputs to `Standing` (§7.2).
Any other transition is refused (I7). Concepts: proposed → active | rejected, active → merged. Aliases: proposed →
active | rejected, active → removed. Refs: unresolved → resolved | dismissed.

### What each event changes

- `concept-*`, `alias-*`: the matching rows in `Concepts` / `ConceptAliases`, with `Version` set to the event's
  `StreamVersion`. `concept-merged`: absorbed `Status = merged`, `MergedIntoId` set. Facts written before the merge
  stay on their own ids, and reads resolve their subject through `MergedIntoId`.
- `fact-proposed` / `fact-asserted`: insert into `Facts` and `FactSources`. `KnownAtUtc` = the latest live source
  date. Record the gate and the run.
- Every fact status event: update `Facts.Status`, append a row to `FactHistory(FactId, Seq, Transition, Actor,
  OnBehalfOf, Reason, OccurredUtc)`. The `Resolution` text people see is rendered from `FactHistory`, and so
  rejected → reopened → accepted shows all three.
- `fact-superseded`: set `SupersededById` and `SupersedeKind` on the old fact and `Supersedes` on the new one,
  plus the validity effect for its kind (§7.1).
- `supersede-voided`: clear them and restore validity as A's earlier events left it (§7.3).
- `fact-ended`, `fact-validity-corrected`: validity columns (§7).
- After every fact event: recompute `Standing` on the facts it touched (§7.2).
- `fact-flagged` / `flag-resolved`: `FactFlags`, which is also the retraction-proposal state.
- `source-*`: `FactSources.Forgotten` / `Erased`, `KnownAtUtc` recomputed, retraction proposal opened or withdrawn
  (§9, §10.6).
- `reference-*`: `ConceptReferences`, and the subject/object a pending proposal resolves to.
- `extraction-run`: `ExtractionRuns` (provenance, read by review).
- `erased`: §10.

### State invariants (checked live and on replay, under the event's `RulesVersion`)

Each invariant declares the fields it reads, and T15 checks those against the erasable set (§10.1).

- **I1 Cited.** `fact-proposed`, `fact-asserted` and genesis facts carry at least one source. No event removes a
  `FactSources` row (forget and erasure mark it).
- **I2 Gate.** A fact is gated when its subject or object is a person, self, value, unconfirmed concept or ref, or
  any source is an Identity or Relational fragment (rule 2). `fact-accepted` on a gated fact needs actor
  `<self>@*`. `auto:` may accept only ungated facts, and only under a rule that is enabled. `fact-asserted` needs
  actor `<self>@*`. *Reads:* subject/object ids, concept kind and kind_confirmed, source types, actor.
- **I3 A proposal supersedes nothing.** `fact-superseded` appears only in the same batch as the `fact-accepted`
  or `fact-asserted` of the fact that supersedes.
- **I4 No re-proposal.** `fact-proposed` that duplicates a rejected fact or a current fact (same **effective**
  subject and object after `MergedIntoId`, normalized text) is refused: "already rejected as #id, reopen it" or
  "already standing as #id". `alias-proposed` that duplicates a rejected or active alias (same concept, normalized
  label) is refused the same way. **I4 runs again at accept**, because a resolved ref can make two proposals
  duplicates after both were proposed. A refused command writes nothing. *Reads:* effective ids, normalized
  text/label (the one invariant that reads content, and erasure only removes its matches).
- **I5 No decisions by the sleep pass.** Actor `sleep` can't accept, reject, reopen, confirm, resolve, merge, void
  or erase. Live, the command check also refuses those during a running sleep pass.
- **I6 Continuity.** `StreamVersion` is one more than the stream's last; a creation event's stream has no events.
- **I7 Legal transitions** (table above).
- **I8 Validity.** When both are known, `ValidTo >= ValidFrom` (by lower bound). `ValidTo` is set only if
  `ValidityEnded`. `fact-ended` only on an open fact. *Reads:* validity columns (never erasable).
- **I9 Unresolved blocks.** A fact whose subject or object is an unresolved or dismissed ref can't become active.
- **I10 Merged is closed.** No event on a merged concept's stream except `erased`. Merge needs both streams at
  their expected versions.
- **I11 Holes are explained.** Every tombstoned field names an `erased` marker that lists it.
- **I12 Authority** (a command check, recorded by the actor, so replay checks it too). Only `<self>@*` decides:
  accept, reject, reopen, retract, resolve, confirm, fix, merge, split, void, erase (`auto:erase` only inside a
  hard fragment erasure's batch). Anyone may flag. The extractor may propose facts, concepts, aliases and refs.
  `migration` writes only genesis. `OnBehalfOf` only on `fact-flagged`. This separates **entrypoints**, not
  authenticated principals (§2.4).
- **I13 One live supersede** (new). `fact-superseded` only on a fact whose `Status` is active and
  `SupersededById` is NULL, so not retracted and not already replaced or wrong. `supersede-voided` only when the
  named superseder's standing is `wrong` or `retracted`, and only on the fact it superseded.

## 14. Test cases (for the build PR, each written to fail when what it covers is disconnected)

- **T1 Emma, two steps.** The store has Ember (person, John's daughter, alias `Ember`) and Computer Ember (self,
  aliases `Ember` and `Emma` of kind `mishearing`).
  (a) Fragment "Emma reviewed PR 23": alias step → one candidate through the mishearing; the proposal names
  Computer Ember, and its payload records the alias id and the span. The rendered fact never says "Emma".
  (b) Fragment "Ember's birthday is in March": two candidates → `reference-unresolved`; the dependent proposal
  can't be accepted (I9) but can be rejected; `reference-resolved` to Ember makes it acceptable; replay
  reproduces both states.
  (c) Wake-time matching of "Emma" in an addressed message shows the candidate and writes no event.
  (d) Without the `Emma` alias, the extractor writes `alias-proposed` Emma → Computer Ember. A second fragment
  with "Emma" doesn't match through it. Accepting it makes the second fragment match. Rejecting it instead makes a
  re-proposal fail (I4). An extractor `alias-added` is refused (I12).
- **T2 Rejected → reopened → accepted.** Four events, each with its actor and reason. `FactHistory` and the
  rendered `Resolution` show all three decisions. While rejected, the extractor re-proposing it is refused (I4).
  Replay is identical.
- **T3 Erasure, both phases.** First churn: a few hundred inserts and updates around the target so pages split.
  Propose and accept a fact whose text holds a marker string, then erase it. After phase 1, the string is absent
  from `ConceptEvents`, `ConceptBatches` (the `RequestHash` is the sentinel), every projection table, the outbox
  and any rendered block. With a reader holding a snapshot open, the `wal-checkpoint` row stays `pending`, the
  report says *incomplete: WAL*, and the string may still be in the WAL. Close the reader; the worker finishes,
  and the string is absent from **the raw bytes of the database file and its WAL** (read the files, don't query).
  The marker holds no hash or length. Verify-replay matches the redacted projection. Strip the marker's listing of
  one field and replay stops at that event (I11). A retry under the erased command's key is refused.
- **T4 Concurrency and idempotency.** Session A reads `fact 12@2` (proposed). Session B rejects it → `@3`. A
  accepts `@2` → refused, the refusal shows B's rejection with B's actor, and nothing is written. The same command
  sent twice with one key writes once and returns a byte-identical `Result`, including minted ids. `Result` equals
  the result rebuilt from the batch's events. The same key with a different command is refused. Two equal
  commands with different session ids and times hash the same.
- **T5 No writes around the projector.** A direct `INSERT`/`UPDATE`/`DELETE` on each projection table fails. A
  direct insert into `ConceptEvents`, `ConceptEventSources` or `ConceptBatches` fails. During an erasure, an update
  that changes any envelope column of an event (try each one) fails, an update of `ConceptEventSources` fails, and
  an update of a batch other than the sentinel fails. The schema test checks every column is in a pin list.
- **T6 Live equals replay.** A property test: random valid and invalid command sequences, including erasures,
  then verify-replay reports no difference. Invalid commands leave no trace. Replay with the live
  `ConceptApplying` absent still succeeds, which proves it authorizes only in the shadow.
- **T7 Forget the only source.** The retraction proposal opens. Restoring withdraws it. Replay gives the same
  result after `ContextFragments` is changed underneath, which proves replay doesn't read it.
- **T8 Validity and standing.** "Learned today, true last Tuesday" keeps both times. A `change` supersede ends the
  old fact at the new fact's `valid_from`, and a `correction` doesn't. Unknown shows as "unknown". `fact-ended`
  twice is refused. Each case in §7.3 gives the standing stated, and a `wrong` fact never renders as an interval
  of truth. The current-state query returns only `current`. Double supersede (B and C both `supersedes 5@3`): C
  is refused and B's link survives. `change` with an earlier `valid_from` is refused with its reason.
  `supersede-voided` with a still-current superseder is refused. Replay gives the same standings.
- **T9 Genesis from Claude's store, on a copy.** 3 concepts, 4 facts and 1 alias give 8 genesis events. The
  alias kind is `unknown` with its note quoted, `legacy_resolution` is verbatim, and verify-replay matches.
  A `pending` 013 fact (synthetic) is refused and listed.
- **T10 Wakes.** An `observed` contradiction flag → one outbox wake. A flag with `OnBehalfOf = person:john` → one
  wake. An extractor proposal → none. A command rolled back after building a wake → none. Replay → none.
- **T11 Merge keeps history.** Facts on the absorbed concept read through to the survivor. Events on the absorbed
  stream are refused afterwards. Merge with one stale version is refused whole. After the merge, a
  `fact-proposed` naming the absorbed concept is written with the survivor's id, and I4 catches a duplicate that
  differs only by the absorbed id.
- **T12 Sleep can't decide.** Refused live. A hand-built `sleep` accept in a log fails replay.
- **T13 Upcasting.** A log written at `SchemaVersion` 1 replays identically under the v2 code. The stored rows
  are unchanged afterwards.
- **T14 Golden log** (Claude, point 8). A checked-in log: genesis plus a few hundred mixed events, including
  supersede chains, erasures and merges, with its expected projection. Replay must reproduce the projection
  exactly. A change to the normalizer, gate classification, date parser or standing function fails it. The PR
  that changes the expected output either bumps `RulesVersion` (keeping the old rules for old events) or explains
  why the old output was a bug.
- **T15 Erasable is disjoint from invariant inputs.** Reads the erasable-field table (§10.1) and each invariant's
  declared inputs, and fails on overlap. Also: an `auto:` accept from before an entity erasure still replays.
- **T16 Hard fragment erasure.** A fragment cited by a fact, a ref and an alias is hard-erased. The fact's text,
  the mention and the label are gone from the log and the projection. A fact in the `keep` list keeps its text and
  records why. The `extraction-run` is unchanged and holds no text. A restore is refused.
- **T17 Secure delete is on.** From a connection opened by `SqliteConnectionString.OpenAsync`, `PRAGMA
  secure_delete` returns 1.

## 15. Open questions and out of scope

**Open, routed to me or John before the build PR:**

1. **Backups with no expiry** (for John: they're his backup choices, and deleting one can't be undone). The
   `before-*` copies in my store folder and Claude's never rotate, so an erasure never completes in them. Options:
   (a) delete them once the migration they guarded has been in use for a set time; (b) re-erase a copy whenever an
   erasure runs, by restoring it to scratch, applying the same erasure and writing it back; (c) keep them, and every
   erasure report lists them as unreachable forever. I lean (a) with (c) until then. Also for John: does the copy
   of Claude's backup on his PC rotate, and does anything pull mine or Ember's?
2. **Validity dates not erasable.** I chose this so I8 never meets an erased value. If a reviewer sees a case
   where a date alone is sensitive and the text isn't, the fallback is Claude's "erased compares as unknown" rule
   for I8.
3. **Genesis supersede kind `unknown`.** Claude's store has supersede links from 013 with no kind. I've let the
   self set the kind once later. That needs a small event (`supersede-kind-set`, from `unknown` only), which this
   revision names but doesn't fully specify.

**Out of scope here:**

- The migration DDL for the projection tables. §13 names their columns. The build PR writes them.
- The extractor itself and its model choice (step 3, Claude's bench). Claude offered to take the secure-delete,
  FTS and checkpoint checks as a measured spike in step 3 (T3 against a churned copy of their store). I'd take that.
- Delivery of the pending-proposals projection into prompts (Q6 settled the shape; the build PR wires it).
- Sharing concepts across selves (rule 4: per self for now).
- Planted wrong proposals: Q6's rule stands, scratch store or isolated queue only, so nothing here makes room for
  them in a live log.
