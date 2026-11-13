# Concept events: the event schema and the apply and replay rules

Arden, 2026-11-13. Status: **DRAFT, step 2 of [concept-store.md](concept-store.md), revision 5**, for review by
Claude and Ember. Docs only: no code, no migration yet. Step 1's design direction was approved at 0f3ecf9 (Ember,
room 1570674371163328513). That approval doesn't cover this schema, which is what this doc puts up for review.

Revision 5 is a small pass on Claude's approval of 5dad711 (room 1570689600664506438, PR #23 comment
6846131603): one replay lock per store, and the WAL named in the threat-model boundary. Nothing else changed, so
Ember can check revisions 4 and 5 together against f00ef16.

Revision 4 answers both reviews of f00ef16. Claude approved step 2 (PR #23 comment 6845841212) with one required
fix and small items. Ember (room 1570685820850151545) asked for changes on one blocker, replay's scratch file.
Both agreed with the five calls revision 3 made (void restores only its own still-current effect or needs
`--keep-validity`; `supersede-restored` is its own event; the quiet-event rule; erased bounds render *(erased)*
while `ValidityEnded` stays; hard fragment erasure erases validity by default), so those stand unchanged. John
approved open question 1's rule (room 1570685148465201206). §0.0 maps the revision-5 points and §0.1 the revision-4 points; §0.2 and §0.3 keep
the earlier tables, and collapse into pointers to the review comments once step 2 is approved.

Where this doc and concept-store.md disagree, this doc is the newer proposal, and the disagreement is a bug to
point out. Where this doc says nothing, concept-store.md and [concept-graph.md](concept-graph.md) stand.

## 0. Review response

### 0.0 Revision 5 (Claude's approval of 5dad711)

| # | Point | Answer | Section |
|---|---|---|---|
| C5.1 | Two replays can share one in-memory shadow | The `memdb` name is fixed per store, so a verify and a rebuild running at once in one process would fold into the same database. Every replay now holds the store's **replay lock** from `openShadow()` until after `releaseShadow()`: an exclusive, non-blocking `flock` on `<store>.replay.lock`. It is the same lock the `shadow:<path>` cleanup row takes. A second replay is refused with "replay already running", never queued. T6 tests it. | §5, §10.3, T6 |
| C5.2 | The WAL in what erasure can't promise | The threat-model boundary names SQLite's WAL file: after a checkpoint `TRUNCATE` shortens it and the last connection to close deletes it, so frames that held the old text go to filesystem free space, alongside old database blocks and SSD remapping. | §10.3 |

### 0.1 Revision 4 (reviews of f00ef16)

| # | Point | Answer | Section |
|---|---|---|---|
| E4.1 | Replay's scratch file leaves unmanaged copies of every current concept value (blocker) | Replay shadows never create and delete a disk file. The shadow is an in-memory SQLite database (the `memdb` VFS, shared by the fold connection and the write connection). If size ever forbids that, one **registered** shadow path from config is used, emptied in place after every replay and covered by a cleanup row. Replay refuses any other path. Measured while writing this: `VACUUM` and large sorts spill to an unlinked temp file under the default `temp_store`, which is the same hole, so every connection also runs `temp_store = MEMORY`. | §5, §10.3, §10.4, T6, T17 |
| E4.2 | Report: managed vs unreachable; threat-model boundary separate | The report has two levels: **managed cleanup** (`complete` / `incomplete`) over what the store controls, and **unreachable copies** listed by name. The erasure as a whole is called complete only when managed cleanup is complete **and** no unreachable row remains. Filesystem free space, SSD remapping and memory are a stated **threat-model boundary**, not an unreachable row. The "16 days" bound is now a bound on managed cleanup. | §1, §10, §10.3, §10.5 |
| C4.1 | §10.4 order is wrong (measured: 2 marker hits left) | FTS `secure-delete` and `optimize` first, then `VACUUM`, then `wal_checkpoint(TRUNCATE)`. | §10.4, T3 |
| C4.2 | A void with `effect = none` must never be blocked | The refusal applies only when `effect = ended` **and** `ValidToSetBySeq` ≠ the link's `Seq`. | §7.3, I13, T8 |
| C4.3 | A link written after the superseder's `valid_from` was erased shows "unknown" | It records `valid_to_set: {"erased_copy_of": <marker>}`, which renders *(erased)*. I11 accepts a copy whose source field that marker lists. | §7.1, §10.1, I11, T19 |
| C4.4 | The erase command's `note` | Marked *(c)*: a later marker can erase it. It's hashed like any other field (§2.2), and erasing it sets that batch's `RequestHash` to the sentinel by the existing rule. | §10.2 |
| C4.5 | Hard erasure's preview | The preview lists every other fact whose `ValidTo` the erasure reaches through a link, before the self confirms. | §10.6, T16 |
| C4.6 | `keep` is all-or-nothing | `keep` takes `text`, `validity` or `both` per fact. | §10.6, T16 |
| J4.1 | Open question 1 | John approved the rule: a before-migration copy is deleted 30 days after its migration, once a restore from a later daily backup has been tested (and the migration verified). Closed. | §10.5, §15 |
| J4.2 | John's PC | Per Claude, home's daily backups of Claude's store are copied to `D:/Documents/Persistence/backups/claude` on John's PC, keeping 30. What does the copying, the real retention, and whether my store or the before-migration copies go there are unchecked; a read-only check on the PC is under way. Until it reports, the PC copies are an **unreachable** row in every report. | §10.5, §15 |

### 0.2 Revision 3 (reviews of 5e1c7ce)

| # | Point | Answer | Section |
|---|---|---|---|
| E3.1 | Request-hash exclusions too broad ("all times") | The excluded fields are named: the idempotency key, `SessionId` (and the `@<session>` part of the actor), `CorrelationId`, the receipt time, and everything the applier generates (`RecordedUtc`, EventIds, entity ids, `BatchId`, `Seq`). Everything else is hashed, including `valid_from`, `valid_to`, a supplied `occurred_at`, source dates and expected versions. | §2.2, T4 |
| E3.2 | Replay must release its snapshot before the write lock | Rebuild folds under a read snapshot through N, **ends** that read transaction, then takes a fresh `BEGIN IMMEDIATE` and folds `Seq > N` before the swap. Verify never takes the write lock. The shadow runs with `secure_delete` on and is deleted afterwards. A concurrent-tail test includes an erasure whose marker is in the tail. | §5, T6 |
| E3.3 | Validity can't be categorically non-erasable | Validity bounds are erasable **to an explicit erased value**. I8 compares an erased bound as unknown, from the first rules version. `ValidityEnded` stays structure, and its residual meaning ("this ended, when is erased") is stated. Rendering shows *erased*, not *unknown*. Tests cover partly and wholly erased intervals. | §7, §10.1, I8, T19 |
| E3.4 | `supersede-kind-set` unspecified | Specified: self only, expected version required, legal only from `unknown`, once, with the same validity effect and payload as a `change` or `correction` supersede, invariant I14 and test T18. | §7.4, I14, T18 |
| E3.s | `Seq` wording | "Committed log order; committed values never reused." | §2.1 |
| C3.1 | `secure_delete` doesn't clean what the stores already hold | The migration that turns it on also runs one `VACUUM`, a `wal_checkpoint(TRUNCATE)`, sets `secure-delete` on `ContextFragments_fts` and runs one FTS `optimize`. T3 churns with the pragma **off** first. The pre-3.42 fallback is gone: Claude measured the bundled `e_sqlite3` (3.53.3, `secure_delete` default 0, FTS5 `secure-delete` working). | §10.4, T3 |
| C3.2a | What void restores is undefined after a later validity correction | The supersede event records the validity it set and the prior values. Void restores them only if that link still set the fact's current end (`ValidToSetBySeq`), otherwise void is refused and the self corrects validity explicitly. Void is a pure function of the link event. | §7.1, §7.3 |
| C3.2b | A voided link can never be restored (A and B both current) | New self-only event `supersede-restored` re-applies a voided link. Review of a void, and of anything that makes a fact current again, shows every link voided because of that fact. | §7.3, I3, I13 |
| C3.2c | Validity has copies | §10.1 gets a derived-copies column. Erasing a bound erases every copy (the superseded fact's `ValidTo`, the link payloads), each listed in the marker. T15 checks it. | §10.1, T15 |
| C3.3 | Commutative events refuse in-flight decisions | Aliases move to their own stream, which removes the alias collisions. For the rest, a decision is refused only if a **non-quiet** event followed its expected version. The one quiet event is the extractor's `source-added` on a proposal. A flag is not quiet: the self should see a flag before deciding, and that refusal is intended. | §6 |
| C3.4 | Merge strands aliases | Aliases get their own `alias:<id>` stream (my decision, Claude's lean). I10 closes only the concept stream. Matching through an absorbed concept's alias yields the survivor. Alias decisions write the effective concept id. | §2.1, §3, §8, T11 |
| C3.5 | Erasure off the one write path | Erase is an `apply()` command: batch row, idempotency key, stored result. `project(erased)` NULLs the listed projection fields in both modes. The live-only rewriting of earlier log rows is the third named difference in §4. Forget and hard erasure carry a key and get a batch row. | §4, §10.2 |
| C3.6 | I4 against tombstones | An erased value matches nothing. The normalizer never maps a tombstone to `""`. Tested with two erased facts and two erased labels. | I4, T6 |
| C3.s | Small items | `erased` split by actor in §6. Concept FTS uses the external-content trigger pattern. T17 also fails on a raw connection that writes a store. The `outside` row names filesystem free space and SSD remapping. | §6, §10.2, §10.3, T17 |
| OQ1 | Copies with no expiry | Claude's rule written as the **proposed** rule, **pending John**: delete a before-migration copy once its migration is verified, a restore from a later daily is tested, and 30 days have passed. Until then the copy is `unreachable` in every report. Never kept forever, never rewritten. | §10.5, §15 |
| OQ2, OQ3 | Validity erasure, genesis supersede kind | Closed by E3.3 and E3.4. | §15 |

### 0.3 Revision 2 (reviews of b81e828)

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
| C3 | FTS5 keeps deleted tokens; embedding cache | Any concept FTS table is created with `secure-delete=1`. The in-process embedding cache gets a cleanup row. (Rev 3: the version fallback is dropped, and existing data is cleaned once, C3.1.) | §10.2, §10.3 |
| C4 | `wal_checkpoint(TRUNCATE)` may be busy | The result row is read and retried a bounded number of times. If it still fails, a cleanup row stays pending and the report says *incomplete: WAL*. | §10.3 |
| C5 | Erasure can change what invariants see | One table lists the erasable fields per event type. They don't overlap the fields any invariant reads, and a test asserts it. (Rev 3: validity dates become erasable, with I8 as a declared exception, E3.3.) | §10.1, T15 |
| C6 | Double supersede | The old fact's expected version comes from the proposal. New invariant: supersede only a fact that stands and isn't already superseded. `change` on an ended fact, and an earlier `valid_from`, are both defined. | §6, §7.1, I13 |
| C7 | Creation events' expected version | Creation events carry `ExpectedVersion = 0`. Forget and restore use the version read in their transaction. The table classifies every event type. | §6 |
| C8 | Versioned rules need everything pinned | The normalizer, gate classification and date parser are named parts of a `RulesVersion`. A golden-log fixture fails when replay output changes. | §5, T14 |
| C9 | Actor authentication is coarser than claimed | `peer:`/`person:` stop being actors. A flag on someone's behalf is actor `<self>@<session>` plus `on_behalf_of` and the room message id. I12 separates entrypoints, not principals. | §2.4, §12, I12 |
| C10 | Merged concepts and refs | Merged ids are resolved at command time, and the survivor's id is written into the event. I4 compares effective ids and runs again at accept. | §6, I4 |
| C-s1 | Forget calls the applier "in the same transaction" | The applier has an ambient-transaction entry, used only by forget and hard fragment erasure. | §4 |
| C-s2 | `RequestHash` normalization undefined | JCS canonical JSON (RFC 8785). (Rev 3: only named transport and generated fields are left out, E3.1.) | §2.2 |
| D-a | Purge in place: the reason was wrong | The new reason is the backups, checked against the real backup scripts. The bound is stated, and the copies with no expiry are an open question for John. | §1, §10.5 |
| D-b | Change vs correction | Review shows the kind. Accept can override it (`--as correction`), and the override is recorded. | §7.1, §3 |
| D-c | No pending status | Kept. T9's real counts are checked. | §1, §11 |

## 1. Decisions this doc makes

These were open after step 1. Each one is mine to defend in review.

1. **Erasure purges in place. No per-event encryption keys.** Revision 1 said a key store gives no extra
   guarantee. That was wrong (Claude). Crypto-shredding is the one way to reach copies that can't be rewritten, and
   such copies exist: daily backups and pre-migration copies (§10.5). Keys would help only if they were kept apart
   from those backups and never backed up with them. Losing the key store would then lose every encrypted event,
   so the store would trade a bounded erasure delay for a real risk of losing itself. I choose the bounded delay.
   Managed cleanup reaches the rotating backups once the last backup made before the erasure expires (§10.5).
   Before-migration copies are deleted under the rule John approved (§10.5). Copies outside the store's control
   (John's PC, room messages, other stores) are listed by name in every report while they remain, and the erasure
   isn't called complete until they're gone (§10.3).
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
   stand again. **Revision 3:** a voided link can be restored (`supersede-restored`) once its superseder stands
   again, and void restores only the validity the link itself recorded.
9. **New in revision 3: aliases have their own stream** (`alias:<id>`). A merge closes the absorbed concept's
   stream but not its aliases, and extractor alias proposals no longer move the concept's version (§2.1, §8).
10. **New in revision 3: validity dates are erasable**, to an explicit erased value that compares as unknown (I8)
    and renders as *erased*. Dates can be the sensitive fact (Ember), so text-only erasure wasn't enough (§10.1).
11. **New in revision 3: an imported supersede of unknown kind can be classified once** by the self
    (`supersede-kind-set`, §7.4). Older selves will want to classify their imported links, so I specified it
    rather than leaving them `superseded` forever.
12. **New in revision 4: nothing the store controls writes content to a scratch file it then deletes.** (Backups
    are files on purpose, deleted on a stated rotation, §10.5.) Replay shadows
    live in memory, or in one registered path that is emptied in place (§5). Every connection runs
    `temp_store = MEMORY`, so `VACUUM` and large sorts don't spill to unlinked temp files (§10.4). A deleted file's
    blocks can't be found by a later erasure, so every working copy is either in memory or at a path cleanup can
    rewrite.

## 2. Streams, the log table and its guards

### 2.1 Streams

An event belongs to exactly one **stream**: `concept:<id>`, `fact:<id>`, `alias:<id>`, `ref:<id>` (a mention
whose referent is unresolved) or `run:<id>` (one extractor run's provenance). Every stream id is an integer minted
by the applier from that stream type's own sequence, so `(StreamType, StreamId)` is the one typed identity of a
stream. **Aliases have their own stream** (revision 3, Claude's hole 4): each alias event names its concept id, but
a merge closing the concept's stream (I10) doesn't strand the alias, and an extractor's `alias-proposed` doesn't
move the concept's version (§6). A command that touches several streams (accept-with-supersede, merge, erasure)
writes one event per stream, all in one batch and one transaction.

`BatchId` means only *the command batch*: the events one command wrote together. It is a UUIDv7 text value and is
not a stream. Revision 1 called the extractor-run stream `batch:<id>` with a text id, inside an `INTEGER` column.
That was two identities under one name (Ember, point 6).

```sql
CREATE TABLE ConceptEvents (
    Seq              INTEGER PRIMARY KEY AUTOINCREMENT, -- replay order = committed log order; committed values never reused
    EventId          TEXT NOT NULL UNIQUE,              -- UUIDv7, minted by the applier; the global id (#1397's rule)
    SchemaVersion    INTEGER NOT NULL,                  -- payload shape; old versions are upcast on read, never rewritten
    RulesVersion     INTEGER NOT NULL,                  -- which invariant set the event was validated under (§5)
    EventType        TEXT NOT NULL,                     -- §3
    StreamType       TEXT NOT NULL CHECK (StreamType IN ('concept', 'fact', 'alias', 'ref', 'run')),
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
    OccurredUtc      TEXT NULL,                         -- when the change happened, if the caller supplied it; NULL = unknown
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
    Step          TEXT NOT NULL,                        -- managed: 'wal-checkpoint' | 'embedding-cache' | 'shadow:<path>' | 'rendered-files' | 'backup:<path>'; 'unreachable:<what>' (§10.3)
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
- The **request hash** is sha256 over the command serialized as JCS canonical JSON (RFC 8785). **Left out**, and
  only these (Ember, revision 3 point 1):
  - the idempotency key (it is the lookup key, not content);
  - `SessionId`, and the `@<session>` suffix of a `<self>@<session>` actor (the self part is hashed);
  - `CorrelationId` (a retry may come from a later turn or wake);
  - the receipt time: when the applier received the command;
  - everything the applier generates: `RecordedUtc`, EventIds, minted entity ids, `BatchId`, `Seq`, and an
    `OccurredUtc` the applier fills in because the caller supplied none.

  **Everything else is hashed**, including every semantic time: `valid_from`, `valid_to`, an `occurred_at` the
  caller supplied, source fragment dates and types, and the expected versions. Two commands that differ only there
  are different commands, and the second is refused as "key reused for a different command". An absent optional
  field hashes as absent, never as a default value. Two retries of the same command hash the same.
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
Fields marked *(c)* hold content: erasable, and no invariant reads them except I4. Fields marked *(v)* are validity
bounds: erasable to an explicit erased value that I8 compares as unknown (§10.1). The validity effect recorded in a
supersede event holds copies of *(v)* values, erased with them.

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
| `concept-merged` | into: concept id + expected version, reason *(c)* | self | On the absorbed concept. It goes to status `merged`. Its aliases keep their own streams and resolve to the survivor (§8). |
| `concept-absorbed` | from: concept id | self | On the survivor, in the same batch. |
| `erased` | §10 | self, `auto:erase` (§10.6) | |

### Alias stream (revision 3)

Every alias event names `concept_id`: the **effective** concept id at command time (§6), so a decision on an alias
of an absorbed concept records the survivor. The creating events carry `ExpectedVersion = 0`.

| Event | Payload | Actor | Notes |
|---|---|---|---|
| `alias-added` | alias id, concept id, label *(c)*, kind (`name` \| `mishearing` \| `unknown`), source fragment or message id, note? *(c)* | self, migration | Creates the stream, active. Never the extractor (Ember, point 1). |
| `alias-proposed` | alias id, concept id, label *(c)*, kind, source fragment id + span, evidence *(c)*, run id | extractor | Creates the stream, status `proposed`. **Never used for matching** until accepted (§8). |
| `alias-accepted` | concept id, kind? (override), reason? *(c)* | self | proposed → active |
| `alias-rejected` | concept id, reason *(c)* | self | proposed → rejected. Kept, so the same label and concept aren't proposed again (I4). |
| `alias-removed` | reason *(c)* | self | active → removed |
| `alias-kind-set` | kind, reason *(c)* | self | Used after genesis, when the kind was unknown at import. |
| `erased` | §10 | self, `auto:erase` | |

### Fact stream

| Event | Payload | Actor | Notes |
|---|---|---|---|
| `snapshot-genesis` | `entity: fact`, id, subject, object?, text *(c)*, status, is_state, as_of → valid_from *(v)*, supersede links with kind (`unknown` if 013 didn't say), `legacy_extracted_by`, `legacy_resolution` *(c)* (quoted verbatim), sources with their fragment dates and types | `migration` | §11 |
| `fact-proposed` | id, subject (concept id **or** ref id), object? (same), text *(c)*, aspect? *(c)*, is_state, valid_from? *(v)*, valid_to? *(v)*, `supersedes`? (fact id + **the version seen** + `change` \| `correction`), sources each with the fragment's date and type, `gate` (`proposal` + why), run id? | extractor, self | Born `proposed`. Every source's date and type is copied in, so replay never reads `ContextFragments` (§5). A merged subject or object is written as its survivor (§6). |
| `fact-asserted` | the same fields as `fact-proposed` | self only | Born `active`. The self accepting its own claim. Never the extractor or `auto:`. |
| `fact-accepted` | reason? *(c)*, `supersede_kind`? (the kind applied), `kind_overridden`? | self; `auto:<rule>` only if the fact is ungated (I2) | proposed → active. If it supersedes, the batch also writes `fact-superseded` on the old fact. `accept 14@2 --as correction` overrides the proposal's kind, and the event records both (Claude, decision b). |
| `fact-rejected` | reason *(c)* | self | proposed → rejected |
| `fact-reopened` | reason *(c)* | self | rejected → proposed |
| `fact-superseded` | by: fact id, kind `change` \| `correction`, the **validity effect**: `effect` (`ended` \| `none`), `valid_to_set` (a copy of the superseder's `valid_from`, or unknown), `prior_valid_to`, `prior_validity_ended` | self (in the accept batch) | Old fact: `SupersededById` and `SupersedeKind` set; the validity effect is applied exactly as recorded (§7.1). |
| `supersede-voided` | link: EventId of the `fact-superseded` or `supersede-restored` it voids, by: fact id, `validity_restored` (bool), reason *(c)* | self | Clears `SupersededById`/`SupersedeKind`. Legal only when the superseder's standing is `wrong` or `retracted` (§7.3). |
| `supersede-restored` | link: EventId of the voided link it restores, by: fact id, kind (the voided link's), the validity effect as in `fact-superseded`, reason *(c)* | self | Revision 3. Re-applies a voided link once the superseder stands again (§7.3, I13). |
| `supersede-kind-set` | by: fact id, kind `change` \| `correction`, the validity effect as in `fact-superseded`, reason *(c)* | self | Revision 3. Classifies an imported link of kind `unknown`, once (§7.4, I14). |
| `fact-ended` | valid_to *(v)* (or `unknown`), reason *(c)*, sources | self, `auto:<rule>` for ungated state facts | The world changed. The fact was true and stopped being true. |
| `fact-validity-corrected` | valid_from? *(v)*, valid_to? *(v)*, ended?, reason *(c)*, sources | self | The dates were wrong. The world didn't change. |
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
  4. if the command is an erasure: INSERT ConceptErasing (marker id); rewrite the target    [live only]
     events' payload fields to tombstones and set ErasedBy; set RequestHash sentinels (§10.2)
  5. step(events)
  6. outbox rows for wake/notify events (§12); cleanup rows for erasures (§10.3)            [live only]
  7. INSERT ConceptBatches (with Result); UPDATE ConceptProjectionState.LastSeq            [live only]
     if erasure: DELETE FROM ConceptErasing
  if not ambient: COMMIT
  any error at any step -> ROLLBACK: no event, no projection change, no outbox row

step(events):                                       -- live and replay, the same code
  INSERT INTO <target>.ConceptApplying
  for each event, in order:
     a. upcast; check tombstones (I11) and stream continuity (I6)
     b. state invariants for event.RulesVersion against <target>'s projection (I1–I14)
     c. live: INSERT INTO ConceptEvents, ConceptEventSources
     d. project(event) into <target>             -- for `erased`: NULL every listed projection field (§10.2)
  DELETE FROM <target>.ConceptApplying
```

**The two modes differ in exactly three things** (Ember's follow-up; the third is revision 3, Claude's hole 5):

1. Live runs the command checks (steps 1 and 2).
2. Live writes the log, batch, outbox and cleanup rows (steps 5c, 6 and 7).
3. Live erasure rewrites **earlier** log rows: the target events' payload tombstones, `ErasedBy`, and the batch
   sentinels (step 4). On replay those rows are already rewritten, so there is nothing to do.

Invariant checks, upcasting, tombstone checks and `project`, **including `project(erased)`**, are one code path,
called with the target schema as a parameter. Live, `project(erased)` NULLs projection fields that still hold the
text. On replay the same call is a no-op, because the earlier events were folded from tombstoned payloads. There is
no second copy of the fold and no live-only purge of projection rows.

**Ambient entry.** `apply(command, ambient = true)` runs inside a transaction the caller already holds `BEGIN
IMMEDIATE` on. It is used only by forget (§9) and hard fragment erasure (§10.6), which must change
`ContextFragments` and the concept log atomically. Every other caller uses the normal entry. An ambient command is
still a command: its caller mints an idempotency key once per forget or hard erasure (reused on retry), and it
writes a `ConceptBatches` row with a stored result like any other. A forget or erasure retried after a lost reply
gets its stored result back.

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

**Where the shadow lives** (revision 4, Ember's blocker). The shadow holds a copy of every current concept value.
A scratch file that is deleted afterwards returns those blocks to filesystem free space, where no later erasure can
find them, so replay never creates and deletes a disk file:

- **Default: in memory.** The shadow is an in-memory SQLite database opened through the `memdb` VFS under one fixed
  name per store, `file:/concept-shadow-<store>?vfs=memdb`. A `memdb` name starting with `/` is shared by every
  connection in the process, so the fold connection S and the write connection W (by `ATTACH`) see the same
  database. It exists while at least one connection holds it and is freed when the last one closes. (Checked on
  home with Python's SQLite 3.46.1: a second connection attached it and read S's rows, still read them after S
  closed, and after both closed it was gone. The build PR repeats the check on the bundled `e_sqlite3` through
  `Microsoft.Data.Sqlite`, including that it accepts the URI in `ATTACH`; if it doesn't, the shared-cache form
  `file:concept-shadow-<store>?mode=memory&cache=shared` is the fallback, still memory only.) The concept
  projection is small (hundreds of rows today), so memory is the right home for it for a long time.
- **If size ever forbids memory: one registered path.** Config may name `Concepts:ReplayShadowPath`, one file per
  store, which the store treats as part of itself: it is created once, never deleted, opened with
  `secure_delete = ON` and `temp_store = MEMORY`, and **emptied in place** at the end of every replay (each table
  `DELETE`d, then `VACUUM`, then `wal_checkpoint(TRUNCATE)` with its result read), so it holds no rows at rest. Every
  erasure writes a `shadow:<path>` cleanup row for it (§10.3), which repeats that emptying, so a replay that crashed
  mid-fold is cleaned too.
- **Anything else is refused.** Replay opens its shadow only through one function that accepts the `memdb` name or
  exactly the registered path, and refuses any other path with "unregistered shadow path". T6 tests the refusal and
  checks that a replay creates no file in the store's directory, `SQLITE_TMPDIR`/`TMPDIR`, `/var/tmp` or `/tmp`.

Home's `/tmp` is tmpfs and home has no swap, but `/var/tmp`, where SQLite puts temp files when no tmpdir is set,
is on disk, and the store shouldn't depend on one machine's mounts. So the guarantee rests on memory and the
registered path, not on a tmpfs.

**One replay at a time** (revision 5, Claude). The `memdb` name is fixed per store, so two replays in one process
(a verify from the sleep pass and a rebuild after a migration, say) would fold into the same database, and one's
`releaseShadow()` could free the other's shadow mid-fold. So every replay holds the store's **replay lock** for its
whole run, from before `openShadow()` until after `releaseShadow()`. The lock is an exclusive, non-blocking `flock`
on `<store>.replay.lock` beside the database: created once, empty, never deleted. `flock` locks on two separate
opens of the file conflict even inside one process, so the one lock covers two replays in the store server and a
replay from another process alike. A replay that can't take it is refused with "replay already running" (the sleep
pass reports its verify as skipped, not failed); it is never queued. The `shadow:<path>` cleanup row (§10.3) takes
the **same** lock, so cleanup can't empty a registered shadow under a running replay. Verify still takes no
database write lock; the replay lock is outside the database.

**How replay is authorized** (Ember). The shadow (opened on its own connection for the fold, and attached to the
write connection as `shadow` for the tail and the swap) is created with the same projection DDL and triggers,
**including its own `ConceptApplying` table**. SQLite resolves a trigger's table names inside the
trigger's own schema, so the shadow's guards read the shadow's own `ConceptApplying`. `step` inserts its row
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
- **Erasure doesn't add refusals.** Erasable fields are disjoint from every field an invariant reads, with two
  declared exceptions that can only remove a refusal, never add one: I4 (an erased value matches nothing) and I8
  (an erased bound compares as unknown). So an event checked live against unredacted state passes the same checks
  against redacted state (§10.1).

**Algorithm** (revision 3, Ember's point 2). In WAL mode, upgrading a read transaction to a write after another
connection has committed fails with `SQLITE_BUSY_SNAPSHOT`. So replay never upgrades: it ends its read transaction
before it asks for the write lock.

```
replay(mode = verify | rebuild):
  take the store's replay lock (flock, non-blocking); if held: refuse "replay already running"
  shadow = openShadow()   -- the memdb name, or exactly the registered path; any other path is refused
           its own connection S, with secure_delete = ON and temp_store = MEMORY; projection DDL and triggers
  R = a read connection. BEGIN; the first SELECT pins R's snapshot
      N = max(Seq) in that snapshot   (the live projection in the same snapshot is at LastSeq = N, because
                                       apply updates both in one transaction)
  for each event with Seq <= N, read through R, by Seq:
      Projector(S, replay).step([event])
      on any failure: stop, report Seq, EventId, the invariant and the event; the live projection is untouched
  verify:  diff S against the live projection read through R (the same snapshot, both at N), every row and
           column; any difference is a bug, reported as such
           COMMIT R; releaseShadow(). Verify never takes the write lock.
  rebuild: COMMIT R                                  -- end the read snapshot first
           W = the write connection: ATTACH the shadow AS shadow; PRAGMA shadow.secure_delete = ON
           close S                                   -- after the ATTACH, so a memdb shadow is never left unheld
           W: BEGIN IMMEDIATE                        -- a fresh write transaction, not an upgrade
           tail = events with Seq > N, read through W (complete: W holds the write lock)
           Projector(shadow, replay).step(tail)      -- an `erased` marker in the tail NULLs the shadow's fields
           INSERT INTO main.ConceptApplying
           for each projection table: DELETE FROM main.T; INSERT INTO main.T SELECT * FROM shadow.T
           LastSeq = max(Seq); RebuiltUtc = now
           DELETE FROM main.ConceptApplying
           COMMIT; DETACH; releaseShadow()
  release the replay lock (also on any failure, after releaseShadow())

releaseShadow(): memdb: close the last connection holding it (freed with it).
                 registered path: empty it in place (DELETE every table, VACUUM, wal_checkpoint(TRUNCATE)), close.
                 Never unlink it.
```

**The erasure in the tail.** If an erasure commits while replay runs, its marker has `Seq > N`, but the events it
tombstoned may have `Seq <= N`, and the snapshot fold read them **unredacted**. Folding the marker in the tail runs
`project(erased)` against the shadow (§4), which NULLs those fields before the swap, so the live projection never
gets the old text back. The shadow held that text until then, in memory, or in the registered path, where
`secure_delete` zeroes the freed pages, `releaseShadow()` empties it, and the erasure's `shadow:<path>` cleanup row
empties it again if the replay crashed first. No copy goes to a file that is deleted. A long verify also keeps its snapshot open, so the erasure's `wal-checkpoint` row stays `pending`
until it ends, which §10.3 already expects.

Replay that fails on an event which passed live is a bug in the projector or in an upcaster, never a reason to skip
the event. **Verify runs** in the test suite (T6, T14), after every migration, and read-only in the sleep pass.

## 6. Versions and conflicts

- A stream's version is the `StreamVersion` of its last event, and the projection keeps it as `Version` on the
  row. `UNIQUE(StreamType, StreamId, StreamVersion)` is the backstop if the projector ever gets this wrong.
- **Every event type is classified** (Claude, point 7):

| Class | Events | ExpectedVersion |
|---|---|---|
| Creates a stream | `concept-created`, `concept-proposed`, `concept-split` (child), `alias-added`, `alias-proposed`, `fact-proposed`, `fact-asserted`, `reference-unresolved`, `extraction-run`, `snapshot-genesis` | **0**: the stream must not exist. A reused or colliding id is refused, never appended at version 2. |
| Decision | accept, reject, reopen (facts, concepts, aliases), `fact-superseded`, `supersede-voided`, `supersede-restored`, `supersede-kind-set`, `fact-ended`, `fact-validity-corrected`, `fact-retracted`, `flag-resolved`, `reference-resolved`, `reference-dismissed`, `concept-kind-confirmed`, `concept-fixed`, `concept-merged` and `concept-absorbed` (both streams), `alias-removed`, `alias-kind-set`, `erased` by the self | Required. The version the caller saw: `concept review` and `concept show` print ids as `fact 12@3`, and the CLI sends the version it showed. A bare `12` is refused with the current version and what changed. |
| Automatic, inside one transaction | `source-forgotten`, `source-restored`, `source-erased`, `erased` by `auto:erase` (§10.6) | The version read inside the forget or erasure transaction. Checked, and it can't conflict, because the transaction holds the write lock. |
| Commutative, additive | `fact-flagged`, `source-added` | NULL, meaning "unchecked", not "checked and matched". |

- **Quiet events don't refuse a decision** (revision 3, Claude's hole 3). Every event still increments
  `StreamVersion` (I6). But a decision is refused only if a **non-quiet** event follows its expected version. The
  one quiet event is `source-added` by the extractor on a proposal: more support for a claim the self is already
  deciding. The decision goes through, its event records the expected version the caller sent, and the result says
  "accepted; 1 source added since you looked". I2 still runs at accept against the projection, so a quiet source
  that makes the fact gated still blocks an `auto:` accept. **A flag is not quiet**: the self should see a flag
  before deciding, so a decision raced by a flag is refused, and that is intended. Extractor alias proposals and
  new references create their own streams, so they no longer touch a concept's version at all.

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
  before the merge. Events on an absorbed concept's stream are refused (I10), except `erased`. Aliases are on their
  own streams, so an absorbed concept's aliases can still be accepted, rejected, removed or kind-set; each such
  event writes the survivor as its concept id (§8).

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
- **Erased is a value too** (revision 3, Ember's point 3). An erased bound is NULL in the projection with
  `ValidFromErasedBy` / `ValidToErasedBy` naming the marker. Every invariant and comparison treats it exactly like
  unknown (I8). It renders as *erased*, not *unknown*: "from (erased) to 2026-11-10", "(erased) to (erased)". The
  erasure marker is visible anyway, so saying *erased* discloses nothing more, and it stops a reader taking a
  deliberate gap for missing knowledge.
- **`ValidityEnded` is not erasable.** It is a standing input, and erasing it would change a fact's standing
  (`current` vs `ended`), which erasure must never do. Its residual meaning after both bounds are erased is "this
  fact stopped holding, at an erased time". If that much is sensitive, the self erases the fact's text too, and
  what remains says only that an unnamed fact ended.
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

**The link event records its validity effect** (revision 3, Claude's hole 2a). `fact-superseded` carries `effect`
(`ended` if it ended an open fact, `none` otherwise), `valid_to_set` (the copy of the superseder's `valid_from` it
wrote, possibly unknown) and, when `effect` is `ended`, `prior_valid_to` / `prior_validity_ended` (the old fact's
values just before). Under these rules an `ended` effect only ever applies to an open fact, so the prior values are
(unknown, 0), but they are recorded rather than assumed so that void stays a pure function of the link event if
the rules change. Projection applies the recorded values; it never re-derives them from the superseder's current
row. The projection also keeps
`Facts.ValidToSetBySeq`: the `Seq` of the event that last wrote the fact's `ValidTo` / `ValidityEnded` (a link, a
`fact-ended`, or a `fact-validity-corrected` that touched either). `valid_to_set` is a copy of a validity bound, so
erasing the superseder's `valid_from` erases it too (§10.1).

**A link that copies an already-erased bound** (revision 4, Claude). If a `fact-superseded`, `supersede-restored`
or `supersede-kind-set` is written **after** the superseder's `valid_from` was erased, there is no value to copy,
and a plain unknown would render A as "ended: unknown", the misreading erased bounds exist to prevent. So the link
records `valid_to_set: {"erased_copy_of": "<marker EventId>"}`, as structure, and the projection sets A's `ValidTo`
NULL with `ValidToErasedBy` = that marker. It renders *(erased)* and compares as unknown (I8), like any erased
bound. The same holds for `prior_valid_to` when the value it would record is itself erased. I11 accepts it: an
`erased_copy_of` is explained when the marker it names lists the source field it copies.

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
| `superseded` | no | "superseded by #N (kind unknown, imported)". Genesis only, until the self classifies it with `supersede-kind-set` (§7.4). |
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
   **`supersede-voided`** on A in the same batch: A's `SupersededById` and `SupersedeKind` are cleared and A is
   `current` again (or `ended`, if it had ended on its own before the link).
   **What void restores** (revision 3, Claude's hole 2a). If the link's `effect` was `ended` **and** A's
   `ValidToSetBySeq` is still that link's `Seq`, void restores `ValidTo` and `ValidityEnded` to the link's recorded
   `prior_valid_to` / `prior_validity_ended`, and records `validity_restored = true`. If the link's `effect` was
   `none` (a correction, or a change on an already-ended fact), it set no validity, so there is nothing to restore
   and nothing blocks the void (`validity_restored = false`). Only when `effect` was `ended` **and** a later
   `fact-ended` or `fact-validity-corrected` has written A's end since (`ValidToSetBySeq` ≠ the link's `Seq`),
   **the void is refused**: "A's end was changed by #seq after
   the link; correct its validity explicitly, or void with `--keep-validity`". `--keep-validity` voids the link and
   leaves validity as it now stands (`validity_restored = false`). The self decides; nothing silently undoes a later
   correction. `ValidFrom` is never touched by a link or a void. Void reads only the link event and A's row, so
   replay derives the same.
3. **The replacement is retracted.** Same as case 2: A stays `replaced` until the self voids the link.
   `supersede-voided` is legal only when the superseder's standing is `wrong` or `retracted` (I13).
4. **Superseding a fact that is already superseded, retracted, wrong, or not active** is refused (I13). A fact
   gets at most one live superseding link.
5. **Correcting an ended fact.** A ended on its own (`fact-ended`), then B corrects it. A becomes `wrong`: the
   correction says it was never true, and wrong outranks ended.
6. **Changing an ended fact.** Allowed (it was true, and something replaced it), validity untouched (§7.1).
   Standing goes `ended` → `replaced`.
7. **The superseder stands again: restoring a voided link** (revision 3, Claude's hole 2b). A is replaced by B; C
   shows B wrong; A's link is voided, so A is current. Later E shows C wrong, and the self voids B's link to C, so B
   is current. Without a way back, A and B would **both** be current ("deployed X" and "deployed Y"), and I3 forbids
   a new `fact-superseded` from a long-active B. So:
   - **`supersede-restored`** on A, self only, re-applies the voided link: same superseder, same kind, and the
     validity effect computed now by §7.1's rule from A's current row and B's current `valid_from`, recorded in the
     event like any link. Legal only when A's most recent link to B is voided, A has no live link (I13), and B's
     standing is `current`, `ended` or `replaced` (I13).
   - **Review shows the voided links.** Any command that makes a fact stand again (a `supersede-voided` on it, a
     `flag-resolved` that keeps it, a `fact-reopened` and accept) shows every link that was voided **because of
     that fact**, with "A and B are now both current: restore A ← B?". The self restores them in the same batch, or
     leaves them voided because the change really never happened. Never automatic.

T8 tests each case, and checks that the same events replay to the same standings.

### 7.4 Classifying an imported supersede (`supersede-kind-set`, revision 3, Ember's point 4)

013's supersede links import with kind `unknown` (§11), so their old fact's standing is `superseded`. The self can
classify each one **once**:

- **Event:** `supersede-kind-set` on the **old** fact's stream (A). Payload: `by` (the superseder's id, which must
  equal A's `SupersededById`), `kind` (`change` | `correction`), the validity effect exactly as `fact-superseded`
  records it (§7.1), and a reason *(c)*.
- **Actor:** `<self>@<session>` only. Not the extractor, `auto:`, `sleep` or `migration` (I12, I5).
- **Expected version:** required (Decision class, §6). Review shows `fact 5@1 superseded by #7 (kind unknown)`.
- **Legal transition:** `SupersedeKind` `unknown` → `change` or `correction`, exactly once. A link that already
  has a kind can't be reclassified this way: a `change` that was really a `correction` is voided and re-linked, or
  corrected by a new fact.
- **Effect:**
  - `correction`: standing `superseded` → `wrong`. Validity untouched (§7.1).
  - `change`: standing `superseded` → `replaced`. If A is open, its `ValidTo` becomes B's `valid_from` (or
    unknown) and `ValidityEnded = 1`, and `ValidToSetBySeq` becomes this event's `Seq`, exactly as for a live
    change. If A had already ended, validity is untouched. If B's `valid_from` is earlier than A's `ValidFrom`, the
    command is refused with §7.1's "is this a correction?".
  - After it, the link is an ordinary link: void, restore and erasure treat it like any other.
- **Invariant:** I14.
- **Test:** T18.

The alternative was leaving imported links `superseded` forever. I chose to specify the event because older selves
will want to classify their imported links, and an unspecified transition would have been left to the build PR.

## 8. Aliases and unresolved references

Aliases lose global uniqueness: a label may point at several concepts. Each alias has a kind (`name`,
`mishearing`, or `unknown` for genesis imports), a source, and a status: `proposed`, `active`, `rejected` or
`removed`. A mishearing alias finds a concept but is never shown as its name and never used in a fact's text.

**Aliases need approval** (Ember, point 1). The extractor can't add an alias. It writes `alias-proposed`, which
review shows with its evidence: "Emma → Computer Ember, mishearing, from fragment #2950 at 14..18". Until the self
accepts it, **a proposed alias is never used for matching**: not by the extractor, not at wake time. So a wrong
"Emma → Ember" never becomes current on its own. A rejected alias is kept, so the same label isn't proposed again
for the same concept (I4).

**Aliases and merges** (revision 3, Claude's hole 4). Each alias is its own stream (§2.1), so I10 closing an
absorbed concept's stream doesn't strand its aliases. A pending proposal on the absorbed concept can still be
accepted or rejected, and an active one removed. Every alias decision writes the **effective** concept id, so
accepting it after the merge attaches it to the survivor. An alias that isn't touched after the merge keeps the
absorbed id, and **matching through it yields the survivor**: alias resolution follows `MergedIntoId` before it
lists candidates, and dedupes candidates by effective id. If the absorbed concept and the survivor both have an
active alias with the same label, nothing is refused; the label yields the survivor once. I4 for a new
`alias-proposed` compares effective ids, so it catches a duplicate of either.

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
2. **Durable cleanup outside the transaction.** The WAL, in-process caches, the registered replay shadow, rendered
   files and backups are tracked in `ConceptErasureCleanup` rows written in that same transaction, worked after
   commit, and retried until done. Copies the store can't reach are rows too, listed by name. The report keeps the
   two apart (§10.3): **managed cleanup** is incomplete until every managed row is `done`, and the erasure as a whole
   isn't called complete while any unreachable copy remains.

**Scope.** A self erases in its own store only (I12): never the extractor, another self or a person. The only
`auto:` actor allowed is `auto:erase`, and only as part of the self's own hard fragment erasure (§10.6). Two
granularities:

- **Field erasure**: named content fields or validity bounds of named events, for example a fact's text, its
  `valid_from`, an alias label, a summary, a flag reason or a reference's mention text. Each comes with its derived
  copies (§10.1).
- **Entity erasure**: every content field and every validity bound (with their derived copies, §10.1) of every
  event on one fact, alias, concept or ref stream. The structure stays: ids, types, versions, actors, envelope
  times, kinds, statuses, `ValidityEnded` and source ids. The projection row becomes a
  tombstone with its status and standing kept and its content NULL.

### 10.1 What can be erased, and what can't (Claude, point 5; revision 3: Ember's point 3, Claude's hole 2c)

Fields marked *(c)* (content) and *(v)* (validity bounds) in §3 can be erased, together with their derived copies.

| Erasable | Derived copies, erased with it | Never erasable (structure) |
|---|---|---|
| *(c)* names, labels, summaries, fact text, aspect, mention text, reasons, notes, evidence, `legacy_resolution` | the projection's content columns; `FactHistory.Reason`; the rendered `Resolution`; concept FTS rows; stored embeddings; undelivered outbox rows that rendered it | every envelope column; all ids (entity, alias, ref, run, flag, source fragment, superseder, link EventIds); kind, kind_confirmed, status, standing inputs (`SupersedeKind`, `ValidityEnded`), `effect`, is_state, gate; versions; `ValidToSetBySeq`; source dates and types; spans; candidate lists |
| *(v)* a fact's `valid_from` / `valid_to` in the event that set it (`fact-proposed`, `fact-asserted`, genesis `as_of`, `fact-ended`, `fact-validity-corrected`) | the fact's own `ValidFrom` / `ValidTo` columns; **for a superseder's `valid_from`:** `valid_to_set` in every `fact-superseded`, `supersede-restored` and `supersede-kind-set` that copied it, and the `ValidTo` of each fact those links ended (if that `ValidTo` still holds the copy, by `ValidToSetBySeq`); **for any `ValidTo`:** `prior_valid_to` in every later link event on that fact that recorded it. A link written **after** the erasure copies nothing: it records `{"erased_copy_of": <marker>}` instead (§7.1) | |

**Finding the copies.** The erasure command computes the copies from the log, not by matching values: it follows
each link event naming the fact as `by`, and each later link on the fact whose `prior_valid_to` was read from the
erased value. Every copy is listed, `(event_id, field)`, in the marker, so I11 explains each hole. Matching by value
would miss nothing here but could erase an unrelated equal date, so it isn't used.

**What invariants may read.** Erasable fields are disjoint from every invariant's declared inputs, with exactly two
declared exceptions, each of which an erasure can only make **less** strict:

- **I4** reads normalized text and labels. An erased value matches nothing (§13). Erasing can remove a duplicate
  match, never create one.
- **I8** reads validity bounds. An erased bound compares as unknown, and I8 checks only known bounds. Erasing can
  remove a comparison, never create one. This is in I8 from the first `RulesVersion`, so no live event was ever
  checked under a rule without it.

T15 reads the erasable-field table and each invariant's declared inputs, fails on any overlap except those two,
and checks that the derived-copies column covers every field the projection or a link copies a *(v)* value into.

### 10.2 Phase 1: one `apply()` command (revision 3, Claude's hole 5)

Erase is an ordinary command through §4's `apply()`, normal or ambient. It has an idempotency key, a request hash
(§2.2's rule: everything not named as excluded, so the targets, fields, expected versions, reason category, `keep`
and `note`), a `ConceptBatches` row and a stored result. A retry after a lost reply gets that result back, instead
of being refused on version.

**The note is content** (revision 4, Claude). `note` is free text inside the marker, so it is marked *(c)*: a later
erasure can name the marker's `note` like any other field. That later erasure sets the first erase batch's
`RequestHash` to the sentinel by the ordinary rule (§2.2: every batch containing a target event), so the note's
hash goes with it. `requested_by`, `reason_category` and the target list are structure.

```
erase command: targets [{stream, expected version, fields or 'entity'}], reason category, note? (c), keep? (§10.6)
  apply() step 2 (command checks): actor is the self, or auto:erase under §10.6; every target's expected version;
       resolve the field list: each named field plus its derived copies (§10.1)
  apply() step 3: one `erased` marker per affected stream:
       payload = { targets: [{event_id, fields: [...]}], requested_by, reason_category, note? (c) }
       metadata only: never the erased text, a hash of it, its length or any excerpt
  apply() step 4, live only, rewriting earlier rows (the third mode difference, §4):
       a. each target event: replace each listed field with {"erased": "<marker EventId>"}; set ErasedBy
       b. ConceptBatches.RequestHash for every batch containing a target event -> 'erased:<marker>'   (§2.2)
  apply() step 5, step([markers]) -> project(erased), the same in both modes:
       c. every listed projection field set NULL with its ErasedBy: Facts (text, ValidFrom, ValidTo, ...),
          ConceptAliases, Concepts, FactHistory, FactFlags, ConceptReferences, rendered Resolution text.
          Concept FTS rows go with them through their triggers (below).
  apply() step 6, live only:
       d. any stored embedding of the erased text; undelivered outbox rows that rendered it
       e. the cleanup rows (§10.3), each 'pending' or 'waiting-expiry'
```

Every connection runs with `secure_delete = ON` (§10.4), so pages freed or rewritten by this transaction are
zeroed.

**FTS.** Any concept FTS table is an external-content table created with `secure-delete=1` and kept in sync by the
same trigger pattern `ContextFragments_fts` already uses (`000_InitialCreate.sql`): an `AFTER UPDATE` trigger issues
the FTS `'delete'` with the **OLD** values, then inserts the new. So NULLing a content column in step c removes its
tokens in the same statement, and no step order can leave tokens behind. `secure-delete` removes them from the
segments, not just from the index (Claude measured FTS5 `secure-delete` working on the bundled `e_sqlite3`
3.53.3), so there is no `optimize` fallback.

### 10.3 Phase 2: the cleanup outbox

After commit, a cleanup worker (the same loop that sends `ConceptOutbox`) works each pending row. A crash means the
row is worked later, never forgotten.

| Step | What the worker does | Done when |
|---|---|---|
| `wal-checkpoint` | `PRAGMA wal_checkpoint(TRUNCATE)` and **reads the `(busy, log, checkpointed)` row** (Claude, point 4). Retries up to a bound (5 tries, backing off up to ~30 s). If it's still busy, the row stays `pending` with `LastError = busy` and is retried on the next pass. The store runs concurrent readers on purpose (wake monitor, post-turn queries, the `.backup` job), so a busy result is expected, not exceptional. | `busy = 0` and `log = checkpointed` |
| `embedding-cache` | Tells the store server to evict the erased texts from `RememberingEmbeddingClient`'s in-process cache, by restarting its embedder state if no targeted evict exists. | the server confirms |
| `rendered-files` | Deletes any on-disk rendered snapshot or cached "concepts touched" block that rendered the content, if such files exist by then. The step-3 PR lists them or states there are none. | each listed file is gone |
| `shadow:<path>` | Only if `Concepts:ReplayShadowPath` is set (§5). Takes the store's replay lock (§5; if a replay holds it, the row stays `pending` and is retried), then empties it in place: `DELETE` every table, `VACUUM`, `wal_checkpoint(TRUNCATE)` with its result read. | the file has no rows, its checkpoint returned `busy = 0`, and its size is the empty schema's |
| `backup:<path>` | Nothing to do: these copies can't be rewritten (§10.5). The row is `waiting-expiry` with `ExpectedByUtc` = the backup's rotation date (dailies) or the date §10.5's rule lets it go (before-migration copies). | the date passes **and** the file is confirmed gone |
| `unreachable:<what>` | One `unreachable` row **per copy the store can't reach**, each named: room messages that quoted it, other selves' stores, staged review copies, model providers' logs, and copies on John's PC (§10.5). Rows are added when a copy is known, never collapsed into one "outside" row. | the copy is confirmed gone by whoever controls it (a person or self marks it, with a note); otherwise never |

**The report** is computed from these rows, never written by hand, and has two levels (revision 4, Ember):

- **Managed cleanup:** `complete` when every row the store controls (`wal-checkpoint`, `embedding-cache`,
  `shadow:*`, `rendered-files`, `backup:*`) is `done`; otherwise `incomplete`, listing each open step with its
  status, attempts, last error and expected date. This covers the live database, its WAL, registered caches and
  shadows, rendered files and every backup home makes.
- **Unreachable copies remain:** each `unreachable:*` row, named.

The erasure as a whole is reported **complete** only when managed cleanup is complete **and** no unreachable row
remains. Otherwise it reads, for example, "managed cleanup complete; unreachable copies remain: John's PC backups
of claude, room message 1570…". `concept erasures` shows every erasure and both levels, so an open one stays
visible after the session that made it is gone.

**The threat-model boundary** is stated in every report's footer, not counted as a row: the store controls the
bytes it writes through SQLite into files it never deletes, with `secure_delete` and `temp_store = MEMORY` on. It
doesn't control what is beneath that: SQLite's WAL file (after a checkpoint, `TRUNCATE` shortens it to zero and
the last connection to close deletes it, so frames that held the old text go to filesystem free space; revision 5,
Claude), blocks the filesystem freed before secure delete was on, from an old database
file replaced by a migration, or from a backup file deleted by rotation, SSD wear-levelling and remapping, process memory, swap (home has none today) and crash
dumps. Those can hold old bytes, and an attacker with raw disk or memory access is outside what erasure promises.
They don't make an erasure "incomplete forever", and they don't let it claim more than this.

### 10.4 Secure delete on every connection, and cleaning what's already there (Claude, point 2; revision 3, hole 1)

Under ordinary writes with `secure_delete` off, B-tree page splits, rebalancing and cell rewrites leave stale
copies of text in freelist pages and the unused space of other pages. An erasure transaction can't find those
copies. So `PRAGMA secure_delete = ON` is set for **every** connection in `SqliteConnectionString.OpenAsync`, next
to the WAL and busy-timeout pragmas that are there today.

**It is required, not a margin.** Claude loaded the bundled `SQLitePCLRaw.lib.e_sqlite3` the app uses: version
3.53.3, not compiled with `SECURE_DELETE`, `PRAGMA secure_delete` defaults to **0**. Home's `sqlite3` CLI and
Python's system SQLite have it on, and don't predict the app. (I checked the CLI in revision 2; the bundled library
is Claude's measurement.)

**The pragma only covers pages freed after it is on.** Every store has run with it off until now, and the concept
tables share a file with `ContextFragments`, so stale fragment text already sits in freelist pages and the unused
space of other pages, and `ContextFragments_fts`'s existing segments hold tokens deleted earlier. So the migration
that turns secure delete on also, once, **in this order** (revision 4: Claude measured that the order matters):

1. sets `INSERT INTO ContextFragments_fts(ContextFragments_fts, rank) VALUES('secure-delete', 1)` and runs one
   `INSERT INTO ContextFragments_fts(ContextFragments_fts) VALUES('optimize')`, which merges the segments so the
   stale tokens are dropped;
2. runs `VACUUM` (outside any transaction, under the migration runner's exclusive hold), which rewrites the file
   with no free or unused space carried over, including the pages the old segments occupied;
3. runs `PRAGMA wal_checkpoint(TRUNCATE)` and reads its result row, as §10.3 does.

Revision 3 had `VACUUM` and the checkpoint first. Then `VACUUM` copies the old segment blobs verbatim, `optimize`
writes the merged segments **into the WAL**, and the old pages stay in the main file until a checkpoint that the
migration never runs. Claude measured it on a scratch database (an external-content FTS5 table, a marker row
deleted with `secure_delete` off): revision 3's order left 2 marker hits in the main file, this order left none
(Python's SQLite 3.46.1; the WAL and `VACUUM` behaviour is the same in 3.53.3).

**`temp_store = MEMORY` on every connection** (revision 4, measured while answering Ember's blocker). `VACUUM`
builds the new file in a temporary database, and large sorts and temporary indexes spill to temporary files. Under
the default `temp_store` those are files SQLite creates in `SQLITE_TMPDIR`, `TMPDIR`, `/var/tmp`, `/usr/tmp` or
`/tmp` (the first that works) and unlinks at once, so their blocks go straight to free space: the same hole as
replay's scratch file, holding a full copy of the store during `VACUUM`. Measured on home (Python's SQLite 3.46.1,
compiled `TEMP_STORE=1`): `VACUUM` of a 35 MB database opened and unlinked one `etilqs_*` temp file under the
default, and none with `PRAGMA temp_store = MEMORY`. So `SqliteConnectionString.OpenAsync` sets
`temp_store = MEMORY` next to `secure_delete`. The pragma has no effect if the library was compiled with
`SQLITE_TEMP_STORE=0`, so T17 reads `PRAGMA compile_options` on the bundled `e_sqlite3` as well as the pragma's
value. The cost is memory during `VACUUM`, about the size of the store, which is small.

The before-migration copy taken for this migration holds the old bytes, and falls under §10.5's rule. Blocks
the file gives up when `VACUUM` shrinks it go to filesystem free space, beyond the threat-model boundary (§10.3).

T17 reads both pragmas from the app's own connection and asserts `secure_delete = 1` and `temp_store = 2`. T3 runs
its churn with the pragma **off** first, then this migration step, then the erasure.

### 10.5 Backups: why purge in place, and the bound (decision 1)

What the backup scripts on home do, read from `/usr/local/bin/persistence-backup*` on 2026-11-13:

| Store | Where | Rotation | Pulled off home? |
|---|---|---|---|
| Arden (mine) | `/home/arden/store/backups/arden-YYYY-MM-DD.db.gz`, daily at 03:30 | `find -mtime +14 -delete`: a backup is removed by the first daily run after it is 15 full days old | Not by this script. I found no pull of my backups, but I can't see John's PC. |
| Claude | `/var/lib/persistence/backups/claude-YYYY-MM-DD.db.gz`, daily at 03:30 | the same `find -mtime +14` | Yes: the script says John's machine pulls the newest daily. Per Claude's record of the PC session that set it up (9 Nov), the copies go to `D:/Documents/Persistence/backups/claude`, keeping 30. What does the pulling and the retention as coded are unchecked (below). |
| Ember | `/home/ember/store/backups/ember-YYYY-MM-DD.db.gz`, daily at 03:45 | the same | Not by this script. |

Copies with **no expiry**: in my store's folder `arden-before-011.db`, `arden-before-012.db`,
`arden-before-013.db` and `arden-before-sleep.db` (I listed them). Per Claude's review, Claude's store has
`claude-before-011.db`, `-before-012.db`, `-before-sleep.db` and `~/backups/claude-pre013-*.db` (I can't read that
directory to check).

So the bound is: **managed cleanup of an erasure is complete within 16 days of its commit** (15 full days, plus up
to one day before the next run deletes the file), or, if a before-migration copy is newer, when the rule below
deletes it. Each `backup:<path>` cleanup row carries its own date, and the worker marks it `done` only after
confirming the file is gone. The erasure as a whole is complete only when the unreachable rows are gone too
(§10.3), and today that includes John's PC.

**John's PC** (revision 4). What is known: per Claude, home's daily backups of Claude's store are copied to
`D:/Documents/Persistence/backups/claude` once a day, keeping 30. What isn't: what does the copying (probably a
Windows scheduled task), whether 30 is what the code keeps, and whether my store, Ember's, or any
`*-before-*.db` copy is pulled at all. A read-only check by a session on the PC is under way (Claude, room
1570685330443476996). Until it reports, every erasure on a store whose backups might be pulled gets an
`unreachable:john-pc` row. When the check reports, a store's PC copies become one of two things. If the code keeps
a fixed number and rotates, they're a managed `backup:` row with a computed expiry, provided a machine check can
confirm the file is gone. Otherwise they stay unreachable, named. If no copy of a store is pulled, its row is
dropped. John has mentioned cheap cloud storage (room, 06:27Z). If it's ever used for backups, each copy there is a
further row of one of these two kinds, decided the same way before it is turned on.

**Copies with no expiry: the rule, approved by John** (open question 1, room 1570685148465201206; Claude's
proposal). A before-migration copy is deleted once **all three** hold:

1. the migration it guarded has passed verify-replay (§5), or, for a migration from before the concept store, has
   been in live use;
2. a restore from a daily backup taken **after** that migration has been tested;
3. 30 days have passed since the migration.

Until then the copy's cleanup row is `waiting-expiry`, dated by the 30 days, with the other two conditions shown
next to it, and when the copy is deleted the row becomes `done`. Copies are never kept forever, and never
rewritten to apply an erasure (revision 2's option (b)): rewriting a safety copy destroys what it is kept for.
Today that covers my four `arden-before-*.db` copies and, per Claude, `claude-before-011.db`, `-before-012.db`,
`-before-sleep.db` and `~/backups/claude-pre013-20261110T083635Z.db`, all guarding migrations live for days.
Applying the rule to them (testing the restores, then deleting) is its own task for each store's owner, not part
of this doc. The build PR's migrations take their before-migration copy under the same rule.

### 10.6 Hard fragment erasure and derived content (Ember, point 5)

Erasing a concept event's text doesn't touch its source fragments. They belong to the fragment store. But when a
**fragment** is hard-erased, concept events may still hold content derived from it. The hard fragment erasure
command holds `BEGIN IMMEDIATE`, erases the fragment, and in the same transaction runs a concept erasure through the
ambient entry, actor `auto:erase`, correlation = the fragment erasure:

| Derived from the fragment | What happens |
|---|---|
| A fact citing it (in its sources) | `source-erased`. The fact's **text and validity bounds are erased** (revision 3: a date can quote the fragment as much as text can) unless the self names the fact in the command's `keep` list with what to keep, `text`, `validity` or `both` (revision 4, Claude), and a reason (e.g. another live source supports the same dates, while the text quotes the fragment). The default is to erase both, because either may quote the fragment. If it was the last live source, a retraction proposal opens (§9), and it can't be withdrawn by a restore. |
| A ref whose source is it | mention text erased. Status and candidates stay. |
| An alias whose source is it | label and note erased, unless kept by name. An erased active alias stops matching. |
| A concept proposed or created citing it | name and summary erased, unless kept by name. |
| `extraction-run` that read it | nothing to erase: it holds input ids and the template hash only (§3). |
| Source dates and types copied into events | kept: they are structure, not content. |

The `keep` list is the self's decision, made in the command, never inferred. The cleanup rows (§10.3) cover the
fragment store's FTS and caches as well as the concept ones.

**The preview shows the reach** (revision 4, Claude). Erasing a fact's validity also erases its derived copies
(§10.1), so it reaches the `ValidTo` of every fact **that fact superseded**, and those facts don't cite the
fragment. Before the self confirms, the command's preview lists each fact it touches: the citing facts, and
separately each other fact whose end date it erases through a link ("fact 5: its end date 2026-11-10 is a copy of
fact 9's start and will show *(erased)*"). Keeping `validity` on the citing fact keeps those copies too.

### 10.7 Replay and consequences

**Replay** reaches a tombstoned field, checks that the marker it names exists and lists that event and field (I11),
and projects NULL with `ErasedBy`. Because erasable fields are disjoint from what invariants read, apart from I4 and
I8, which an erasure can only loosen (§10.1), every event that passed live passes again.
The result is the redacted projection, the same every time. A tombstone
with no matching marker is corruption, and replay stops on it. The original history can't be recovered, by design,
and replay says so rather than failing.

- Dedupe never matches erased text (I4): an erased value matches nothing, including another erased value, so two
  erased facts on the same subject and object never count as duplicates on replay. An erased rejected proposal
  can therefore be proposed again. The marker still
  shows that a rejected fact existed on that concept, so the self has a hint.
- A retry of an erased command's key is refused (§2.2).

## 11. Snapshot genesis (importing the 013 rows)

Done once, in one transaction, as batch `genesis`, actor `migration`, `OccurredUtc` NULL, `ExpectedVersion` 0 for
every stream.

- **One event per row**: each concept, fact and alias gets its own stream (`concept:`, `fact:`, `alias:`), starting
  at version 1, each with a `snapshot-genesis` event whose `entity` names the row type. Facts' sources
  come with each fragment's date and type, read once at import. Imported aliases are `active`, kind `unknown`.
- **Unknown stays unknown.** Alias kind is `unknown` even when the note says "a dictation mishearing": the note is
  quoted, and the kind is the self's to set (`alias-kind-set`). Who confirmed a kind and when: unknown. Actors of
  past decisions: unknown. A 013 supersede link imports with kind `unknown`, so its standing is `superseded`, never
  guessed as `replaced` or `wrong`, until the self classifies it with `supersede-kind-set` (§7.4). The import sets
  no validity effect for it.
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
| an erasure whose **managed** cleanup is still incomplete 24 h after commit | notify only (unreachable rows don't notify; they're listed in `concept erasures`) |
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

- `concept-*`: the matching row in `Concepts`, with `Version` set to the event's `StreamVersion`. `alias-*`: the
  row in `ConceptAliases` (one per alias stream, with its own `Version`), its `ConceptId` set to the effective id
  each event records. `concept-merged`: absorbed `Status = merged`, `MergedIntoId` set. Facts written before the merge
  stay on their own ids, and reads resolve their subject through `MergedIntoId`.
- `fact-proposed` / `fact-asserted`: insert into `Facts` and `FactSources`. `KnownAtUtc` = the latest live source
  date. Record the gate and the run.
- Every fact status event: update `Facts.Status`, append a row to `FactHistory(FactId, Seq, Transition, Actor,
  OnBehalfOf, Reason, OccurredUtc)`. The `Resolution` text people see is rendered from `FactHistory`, and so
  rejected → reopened → accepted shows all three.
- `fact-superseded`, `supersede-restored`: set `SupersededById` and `SupersedeKind` on the old fact and
  `Supersedes` on the new one, apply the validity effect **as recorded in the event** (§7.1), and if it ended the
  fact, set `ValidToSetBySeq`. Insert or update the row in `FactSupersedeLinks(FactId, LinkEventId, ById, Kind,
  Status live|voided, VoidedBySeq, RestoredByEventId)`, which is what review reads to list voided links (§7.3).
- `supersede-kind-set`: set `SupersedeKind` and the link row's `Kind`, and apply its recorded validity effect (§7.4).
- `supersede-voided`: clear `SupersededById`/`SupersedeKind`, mark the link row `voided`, and if
  `validity_restored`, set `ValidTo`/`ValidityEnded` to the link's recorded prior values (§7.3).
- `fact-ended`, `fact-validity-corrected`: validity columns (§7); `ValidToSetBySeq` when either touched
  `ValidTo` or `ValidityEnded`.
- After every fact event: recompute `Standing` on the facts it touched (§7.2).
- `fact-flagged` / `flag-resolved`: `FactFlags`, which is also the retraction-proposal state.
- `source-*`: `FactSources.Forgotten` / `Erased`, `KnownAtUtc` recomputed, retraction proposal opened or withdrawn
  (§9, §10.6).
- `reference-*`: `ConceptReferences`, and the subject/object a pending proposal resolves to.
- `extraction-run`: `ExtractionRuns` (provenance, read by review).
- `erased`: NULL each listed projection field and set its `ErasedBy` (`ValidFromErasedBy`/`ValidToErasedBy` for
  bounds), in both modes (§4, §10.2).

### State invariants (checked live and on replay, under the event's `RulesVersion`)

Each invariant declares the fields it reads, and T15 checks those against the erasable set (§10.1).

- **I1 Cited.** `fact-proposed`, `fact-asserted` and genesis facts carry at least one source. No event removes a
  `FactSources` row (forget and erasure mark it).
- **I2 Gate.** A fact is gated when its subject or object is a person, self, value, unconfirmed concept or ref, or
  any source is an Identity or Relational fragment (rule 2). `fact-accepted` on a gated fact needs actor
  `<self>@*`. `auto:` may accept only ungated facts, and only under a rule that is enabled. `fact-asserted` needs
  actor `<self>@*`. *Reads:* subject/object ids, concept kind and kind_confirmed, source types, actor.
- **I3 A proposal supersedes nothing.** `fact-superseded` appears only in the same batch as the `fact-accepted`
  or `fact-asserted` of the fact that supersedes. The only later ways to change a link are `supersede-voided`,
  `supersede-restored` (a voided link only, I13) and `supersede-kind-set` (an imported link only, I14).
- **I4 No re-proposal.** `fact-proposed` that duplicates a rejected fact or a current fact (same **effective**
  subject and object after `MergedIntoId`, normalized text) is refused: "already rejected as #id, reopen it" or
  "already standing as #id". `alias-proposed` that duplicates a rejected or active alias (same concept, normalized
  label) is refused the same way. **I4 runs again at accept**, because a resolved ref can make two proposals
  duplicates after both were proposed. A refused command writes nothing. **An erased value matches nothing**,
  including another erased value: the normalizer is never called on a tombstone, and a tombstone is never mapped
  to `""` (revision 3, Claude's hole 6). *Reads:* effective ids, normalized text/label (a declared exception to
  §10.1's disjointness; erasure only removes its matches).
- **I5 No decisions by the sleep pass.** Actor `sleep` can't accept, reject, reopen, confirm, resolve, merge, void,
  restore, classify a link or erase. Live, the command check also refuses those during a running sleep pass.
- **I6 Continuity.** `StreamVersion` is one more than the stream's last; a creation event's stream has no events.
- **I7 Legal transitions** (table above).
- **I8 Validity.** When both are known, `ValidTo >= ValidFrom` (by lower bound). `ValidTo` is set only if
  `ValidityEnded`. `fact-ended` only on an open fact. **An erased bound compares as unknown** (revision 3, Ember's
  point 3), and this is part of I8 from the first `RulesVersion`. *Reads:* validity columns and their `ErasedBy`
  (a declared exception to §10.1's disjointness; erasure only removes comparisons).
- **I9 Unresolved blocks.** A fact whose subject or object is an unresolved or dismissed ref can't become active.
- **I10 Merged is closed.** No event on a merged concept's stream except `erased`. Merge needs both streams at
  their expected versions. Alias streams are not concept streams, so I10 doesn't close an absorbed concept's
  aliases (§8).
- **I11 Holes are explained.** Every tombstoned field names an `erased` marker that lists it. A link's
  `{"erased_copy_of": <marker>}` (revision 4, §7.1) is explained when that marker lists the source field it copies
  (the superseder's `valid_from`, or the `ValidTo` a `prior_valid_to` would have recorded).
- **I12 Authority** (a command check, recorded by the actor, so replay checks it too). Only `<self>@*` decides:
  accept, reject, reopen, retract, resolve, confirm, fix, merge, split, void, restore, classify a link, erase
  (`auto:erase` only inside a
  hard fragment erasure's batch). Anyone may flag. The extractor may propose facts, concepts, aliases and refs.
  `migration` writes only genesis. `OnBehalfOf` only on `fact-flagged`. This separates **entrypoints**, not
  authenticated principals (§2.4).
- **I13 One live supersede.** `fact-superseded` and `supersede-restored` only on a fact whose `Status` is active
  and `SupersededById` is NULL, so not retracted and not already replaced or wrong. `supersede-voided` only when
  the named superseder's standing is `wrong` or `retracted`, and only on the fact it superseded. If the link's
  `effect` is `ended`, it restores validity only if the fact's `ValidToSetBySeq` is the link's `Seq`, and otherwise
  needs `--keep-validity`; a link with `effect = none` never blocks a void (revision 4, §7.3). `supersede-restored` only when the
  fact's most recent link to that superseder is `voided`, and the superseder's standing is `current`, `ended` or
  `replaced`.
- **I14 Kind set once** (revision 3). `supersede-kind-set` only by `<self>@*`, only on a fact whose
  `SupersedeKind` is `unknown`, only with `by` equal to its `SupersededById`, and only to `change` or
  `correction`. So it happens at most once per link, and only on links imported by genesis. A `change` whose `by`
  starts earlier than the fact (by lower bound, both known) is refused as in §7.1. *Reads:* `SupersedeKind`,
  `SupersededById`, validity columns, actor.

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
- **T3 Erasure, both phases.** First churn, **with `secure_delete` off**, as the real stores ran: a few hundred
  inserts, updates and deletes around the target so pages split, including a fragment whose text holds a second
  marker string and is then deleted. Then run the migration step of §10.4 in its order (pragma on, FTS `secure-delete` and
  `optimize`, then `VACUUM`, then checkpoint): the second marker is absent from the raw bytes of the file and WAL, and from
  `ContextFragments_fts`'s segments.
  Propose and accept a fact whose text holds a marker string, then erase it. After phase 1, the string is absent
  from `ConceptEvents`, `ConceptBatches` (the `RequestHash` is the sentinel), every projection table, the outbox
  and any rendered block. With a reader holding a snapshot open, the `wal-checkpoint` row stays `pending`, the
  report says *incomplete: WAL*, and the string may still be in the WAL. Close the reader; the worker finishes,
  and the string is absent from **the raw bytes of the database file and its WAL** (read the files, don't query).
  The marker holds no hash or length. Verify-replay matches the redacted projection. Strip the marker's listing of
  one field and replay stops at that event (I11). A retry under the erased command's key is refused. The erase
  command itself wrote a `ConceptBatches` row, and re-sending it with its own key returns its stored result instead
  of a version refusal. A concept FTS row's tokens are gone after the content column is NULLed, whatever the
  order of the steps. Run the migration step a second time on a fresh churned copy in revision 3's order (`VACUUM`,
  checkpoint, then `optimize`) and assert the marker **is** found, so the test is known to catch the order going
  wrong (Claude measured 2 hits in the main file).
- **T4 Concurrency and idempotency.** Session A reads `fact 12@2` (proposed). Session B rejects it → `@3`. A
  accepts `@2` → refused, the refusal shows B's rejection with B's actor, and nothing is written. The same command
  sent twice with one key writes once and returns a byte-identical `Result`, including minted ids. `Result` equals
  the result rebuilt from the batch's events. The same key with a different command is refused. Two equal
  commands that differ only in `SessionId`, `CorrelationId`, the actor's `@<session>` part and receipt time hash
  the same. Two commands that differ only in `valid_from`, `valid_to`, a supplied `occurred_at` or one source's
  date hash differently, and the second is refused as a reused key.
  A quiet extractor `source-added` between read and accept doesn't refuse
  the accept, and the result says so; a `fact-flagged` there does refuse it.
- **T5 No writes around the projector.** A direct `INSERT`/`UPDATE`/`DELETE` on each projection table fails. A
  direct insert into `ConceptEvents`, `ConceptEventSources` or `ConceptBatches` fails. During an erasure, an update
  that changes any envelope column of an event (try each one) fails, an update of `ConceptEventSources` fails, and
  an update of a batch other than the sentinel fails. The schema test checks every column is in a pin list.
- **T6 Live equals replay.** A property test: random valid and invalid command sequences, including erasures,
  then verify-replay reports no difference. Invalid commands leave no trace. Replay with the live
  `ConceptApplying` absent still succeeds, which proves it authorizes only in the shadow. Two facts on the same
  subject and object with the same text, both erased, and two erased alias labels on one concept replay without
  an I4 refusal (Claude's hole 6).
  **Concurrent tail** (Ember's point 2): start a rebuild and pause it after its snapshot fold through N. From
  another connection commit (a) new fact events and (b) an erasure whose marker is in the tail and whose target
  event has `Seq <= N`, so the snapshot fold read it unredacted. Resume: the rebuild gets no
  `SQLITE_BUSY_SNAPSHOT`, folds the tail under its fresh `BEGIN IMMEDIATE`, the swapped projection equals a fresh
  verify-replay, the erased string is absent from the live projection, and the shadow is released. A verify run
  across the same commits takes no write lock and reports no difference.
  **No scratch files** (revision 4, Ember's blocker): with `SQLITE_TMPDIR` and `TMPDIR` pointed at an empty
  directory, a verify and a rebuild of a store large enough to spill create no file there, in the store's
  directory, `/var/tmp` or `/tmp` (watch with inotify, since a file created and unlinked at once doesn't show in
  a listing). `openShadow` with any path other than the `memdb` name or the registered path is refused with
  "unregistered shadow path". With `Concepts:ReplayShadowPath` set, a rebuild leaves the file in place with no
  rows; a rebuild killed after its fold leaves rows, and an erasure's `shadow:<path>` cleanup row empties it,
  after which the erased marker string is absent from the file's raw bytes and its WAL.
  **One replay at a time** (revision 5, Claude): pause a rebuild after its fold. A verify started then is refused
  with "replay already running", opens no shadow, and the paused rebuild's shadow still holds its rows; resumed,
  the rebuild swaps a projection equal to a fresh verify-replay. The same with a second rebuild, and with the lock
  taken from a second process. With `Concepts:ReplayShadowPath` set, an erasure's `shadow:<path>` row stays
  `pending` while the rebuild holds the lock and is `done` after it ends. A replay that fails mid-fold releases the
  lock, and the next replay takes it.
- **T7 Forget the only source.** The retraction proposal opens. Restoring withdraws it. Replay gives the same
  result after `ContextFragments` is changed underneath, which proves replay doesn't read it.
- **T8 Validity and standing.** "Learned today, true last Tuesday" keeps both times. A `change` supersede ends the
  old fact at the new fact's `valid_from`, and a `correction` doesn't. Unknown shows as "unknown". `fact-ended`
  twice is refused. Each case in §7.3 gives the standing stated, and a `wrong` fact never renders as an interval
  of truth. The current-state query returns only `current`. Double supersede (B and C both `supersedes 5@3`): C
  is refused and B's link survives. `change` with an earlier `valid_from` is refused with its reason.
  `supersede-voided` with a still-current superseder is refused. Replay gives the same standings.
  **Void and restore** (Claude's hole 2): A changed by B (A's `ValidTo` = B's `valid_from`); B shown wrong; void
  restores A's prior validity. Again, but with a `fact-validity-corrected` on A's end after the link: plain void is
  refused, `--keep-validity` voids and leaves the correction. The chain A←B, C shows B wrong, A voided, E shows C
  wrong, B voided: review lists A←B as voided because of B; `supersede-restored` makes A `replaced` again, so only
  B is current; restoring while B is still `wrong` is refused; restoring a link that isn't voided is refused.
  A `correction` link (`effect = none`) whose old fact later gets a `fact-validity-corrected`, and a `change` on
  an already-ended fact, both void without `--keep-validity`, with `validity_restored = false` (revision 4).
- **T9 Genesis from Claude's store, on a copy.** 3 concepts, 4 facts and 1 alias give 8 genesis events. The
  alias kind is `unknown` with its note quoted, `legacy_resolution` is verbatim, and verify-replay matches.
  A `pending` 013 fact (synthetic) is refused and listed.
- **T10 Wakes.** An `observed` contradiction flag → one outbox wake. A flag with `OnBehalfOf = person:john` → one
  wake. An extractor proposal → none. A command rolled back after building a wake → none. Replay → none.
- **T11 Merge keeps history.** Facts on the absorbed concept read through to the survivor. Events on the absorbed
  concept stream are refused afterwards. Merge with one stale version is refused whole. After the merge, a
  `fact-proposed` naming the absorbed concept is written with the survivor's id, and I4 catches a duplicate that
  differs only by the absorbed id. **Aliases** (Claude's hole 4): a pending `alias-proposed` on the absorbed
  concept can be accepted after the merge, and its event records the survivor; an active alias on it can be
  removed; matching a label through the absorbed concept's active alias yields the survivor once. An extractor
  `alias-proposed` on concept 3 between read and `merge 3@5` doesn't refuse the merge.
- **T12 Sleep can't decide.** Refused live. A hand-built `sleep` accept in a log fails replay.
- **T13 Upcasting.** A log written at `SchemaVersion` 1 replays identically under the v2 code. The stored rows
  are unchanged afterwards.
- **T14 Golden log** (Claude, point 8). A checked-in log: genesis plus a few hundred mixed events, including
  supersede chains with voids, restores and kind-sets, erasures of text and of validity, and merges with
  aliases, with its expected projection. Replay must reproduce the projection
  exactly. A change to the normalizer, gate classification, date parser or standing function fails it. The PR
  that changes the expected output either bumps `RulesVersion` (keeping the old rules for old events) or explains
  why the old output was a bug.
- **T15 Erasable is disjoint from invariant inputs.** Reads the erasable-field table (§10.1) and each invariant's
  declared inputs, and fails on any overlap other than I4's and I8's declared ones. Also: an `auto:` accept from
  before an entity erasure still replays. **Derived copies** (Claude's hole 2c): A changed by B; erase B's
  `valid_from`; the bound is gone from B's event and row, from the link's `valid_to_set`, and from A's `ValidTo`,
  and the marker lists all three. A test fails if a projection column or event field that copies a *(v)* value is
  missing from the derived-copies column.
- **T16 Hard fragment erasure.** A fragment cited by a fact, a ref and an alias is hard-erased. The fact's text,
  the mention and the label are gone from the log and the projection. A fact in the `keep` list keeps what it names
  and records why: `text` keeps the text and erases the dates, `validity` the reverse, `both` keeps both. With A
  changed by the citing fact B, the preview lists A as a fact whose end date it erases, and after confirming A's
  `ValidTo` shows *(erased)*; with `keep: validity` on B, A's end date is kept. The `extraction-run` is unchanged and holds no text. A restore is refused.
- **T17 Secure delete is on.** From a connection opened by `SqliteConnectionString.OpenAsync`, `PRAGMA
  secure_delete` returns 1 and `PRAGMA temp_store` returns 2, and the bundled `e_sqlite3`'s `PRAGMA compile_options`
  doesn't list `TEMP_STORE=0` (revision 4). A test also fails on any raw `new SqliteConnection` in `src/` that opens a store
  database for writing outside `SqliteConnectionString` (`ExecuteActionsHandler` L281–282 opens one for a write
  today; it doesn't write a store, so it is listed as allowed by path, and a new one fails).
- **T18 Classifying an imported link** (§7.4). Genesis imports A superseded by B with kind `unknown`: standing
  `superseded`. `supersede-kind-set change` makes A `replaced`, ends an open A at B's `valid_from`, and sets
  `ValidToSetBySeq`; on an A that had ended, validity is untouched. `correction` makes A `wrong` with validity
  untouched. A second kind-set is refused (I14); a kind-set by the extractor or `sleep` is refused; one on a live
  (non-genesis) link is refused; one with `by` not equal to `SupersededById` is refused; a stale version is
  refused. Void and restore then work on the classified link. Replay gives the same standings.
- **T19 Erased validity** (Ember's point 3). Erase only `ValidFrom` of an ended fact, then only `ValidTo`, then
  both. Each renders as stated in §7 ("from (erased) to 2026-11-10", "(erased) to (erased)"), never as
  "unknown", and standing is unchanged. A `fact-validity-corrected` and a `change` supersede that would have been
  refused by I8 against the erased bound are accepted after the erasure, and the same log replays. Replay of
  partly and wholly erased intervals equals the live projection.
  **A link after the erasure** (revision 4, Claude): erase B's `valid_from`, then write `fact-superseded` A ← B
  (change, A open). The link records `{"erased_copy_of": <marker>}`, A renders "ended *(erased)*", not
  "unknown", I11 accepts it on replay, and a hand-built link whose `erased_copy_of` names a marker that doesn't
  list B's `valid_from` makes replay stop at the link. The same for `supersede-restored` and `supersede-kind-set`. A later marker can erase the first
  marker's own `note`, and that sets the first erase batch's `RequestHash` to the sentinel.

## 15. Open questions and out of scope

**Open, not blocking step 2:**

1. **John's PC copies** (§10.5). What does the pulling, the retention as coded, and whether my store, Ember's or
   any before-migration copy is pulled. A read-only check by a session on the PC is under way (Claude, room
   1570685330443476996). Until it reports, the PC copies are an `unreachable:john-pc` row in every erasure report.
   When it reports, §10.5's table is corrected from it and each store's PC copies become a managed `backup:` row or
   stay unreachable, by the rule stated there.

**Closed in revision 4:**

- ~~Backups with no expiry~~ (open question 1). John approved the rule (room 1570685148465201206): a
  before-migration copy is deleted 30 days after its migration, once the migration is verified and a restore from a
  later daily backup has been tested (§10.5).

**Closed in revision 3:**

2. ~~Validity dates not erasable.~~ They are erasable to an explicit erased value that compares as unknown
   (Ember's point 3; §7, §10.1, I8).
3. ~~Genesis supersede kind `unknown`.~~ `supersede-kind-set` is specified (Ember's point 4; §7.4, I14, T18).

**Out of scope here:**

- The migration DDL for the projection tables. §13 names their columns. The build PR writes them.
- The extractor itself and its model choice (step 3, Claude's bench). Claude offered to take the secure-delete,
  FTS and checkpoint checks as a measured spike in step 3 (T3 against a churned copy of their store). I'd take that.
- Delivery of the pending-proposals projection into prompts (Q6 settled the shape; the build PR wires it).
- Sharing concepts across selves (rule 4: per self for now).
- Planted wrong proposals: Q6's rule stands, scratch store or isolated queue only, so nothing here makes room for
  them in a live log.
