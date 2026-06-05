# Persistence v0.1 Design

This is the living design specification for the v0.1 refactor, built incrementally through conversation.
Sections marked `[TBD]` are pending design discussion. Method signatures are intentionally omitted — names
are enough to drive implementation.

---

## Schema

### Full Schema

```sql


-- Entities --


CREATE TABLE IF NOT EXISTS ContextFragments (
    Id INTEGER PRIMARY KEY,
    FragmentType TEXT NOT NULL,
    Status TEXT NOT NULL,
    Content TEXT NOT NULL,
    Summary TEXT NULL,
    CreatedUtc TEXT NOT NULL,
    LastModifiedUtc TEXT NOT NULL,
    LastAccessedUtc TEXT NOT NULL,
    Importance REAL NOT NULL,
    Confidence REAL NOT NULL,
    IsProtected INTEGER NOT NULL DEFAULT 0,
    Notes TEXT NULL
);

CREATE INDEX IF NOT EXISTS Idx_ContextFragments_FragmentType ON ContextFragments(FragmentType);
CREATE INDEX IF NOT EXISTS Idx_ContextFragments_Status ON ContextFragments(Status);
CREATE INDEX IF NOT EXISTS Idx_ContextFragments_Importance ON ContextFragments(Importance);
CREATE INDEX IF NOT EXISTS Idx_ContextFragments_Confidence ON ContextFragments(Confidence);
CREATE INDEX IF NOT EXISTS Idx_ContextFragments_CreatedUtc ON ContextFragments(CreatedUtc);

CREATE INDEX IF NOT EXISTS Idx_ContextFragments_FragmentType_Status ON ContextFragments(FragmentType, Status);
CREATE INDEX IF NOT EXISTS Idx_ContextFragments_FragmentType_Status_Importance_Confidence ON ContextFragments(
    FragmentType,
    Status,
    Importance,
    Confidence
);

----

CREATE TABLE IF NOT EXISTS Tags (
    Id INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    ParentTagId INTEGER NULL,
    CreatedUtc TEXT NOT NULL,
    LastModifiedUtc TEXT NOT NULL,
    Description TEXT NULL,
    Notes TEXT NULL,
    FOREIGN KEY(ParentTagId) REFERENCES Tags(Id)
);

CREATE INDEX IF NOT EXISTS Idx_Tags_ParentTagId ON Tags(ParentTagId);

CREATE UNIQUE INDEX IF NOT EXISTS uIdx_Name ON Tags(Name) WHERE ParentTagId IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uIdx_Name_ParentTagId ON Tags(Name, ParentTagId) WHERE ParentTagId IS NOT NULL;

----

CREATE TABLE IF NOT EXISTS Sources (
    Id INTEGER PRIMARY KEY,
    SourceType TEXT NOT NULL,
    Name TEXT NULL,
    CreatedUtc TEXT NOT NULL,
    LastModifiedUtc TEXT NOT NULL,
    Notes TEXT NULL
);

CREATE INDEX IF NOT EXISTS Idx_Sources_SourceType ON Sources(SourceType);

----

CREATE TABLE IF NOT EXISTS AuditLogs (
    Id INTEGER PRIMARY KEY,
    SessionId TEXT NOT NULL,
    WorkingContextId INTEGER NULL,   -- NULL when written before a WorkingContext is set (first-run creation)
    EventType TEXT NOT NULL,
    TargetType TEXT NOT NULL,
    TargetId INTEGER NOT NULL,
    SourceId INTEGER NOT NULL,
    OldData TEXT NULL,
    NewData TEXT NULL,
    CreatedUtc TEXT NOT NULL,
    LastModifiedUtc TEXT NOT NULL,
    Notes TEXT NULL,
    FOREIGN KEY(SourceId) REFERENCES Sources(Id),
    FOREIGN KEY(WorkingContextId) REFERENCES WorkingContexts(Id)
);

CREATE INDEX IF NOT EXISTS Idx_AuditLogs_SessionId ON AuditLogs(SessionId);
CREATE INDEX IF NOT EXISTS Idx_AuditLogs_CreatedUtc ON AuditLogs(CreatedUtc);

CREATE INDEX IF NOT EXISTS Idx_AuditLogs_TargetType_TargetId ON AuditLogs(TargetType, TargetId);
CREATE INDEX IF NOT EXISTS Idx_AuditLogs_EventType_TargetType_TargetId ON AuditLogs(EventType, TargetType, TargetId);

----

CREATE TABLE IF NOT EXISTS ActionLogs (
    Id INTEGER PRIMARY KEY,
    SessionId TEXT NOT NULL,
    WorkingContextId INTEGER NOT NULL,
    ActionType TEXT NOT NULL,
    Payload TEXT NULL,
    Result TEXT NULL,
    CreatedUtc TEXT NOT NULL,
    LastModifiedUtc TEXT NOT NULL,
    Notes TEXT NULL,
    FOREIGN KEY(WorkingContextId) REFERENCES WorkingContexts(Id)
);

CREATE INDEX IF NOT EXISTS Idx_ActionLogs_SessionId ON ActionLogs(SessionId);
CREATE INDEX IF NOT EXISTS Idx_ActionLogs_WorkingContextId ON ActionLogs(WorkingContextId);
CREATE INDEX IF NOT EXISTS Idx_ActionLogs_CreatedUtc ON ActionLogs(CreatedUtc);

----

CREATE TABLE IF NOT EXISTS WorkingContexts (
    Id INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL,
    LastModifiedUtc TEXT NOT NULL,
    LastAccessedUtc TEXT NOT NULL,
    Summary TEXT NOT NULL,
    Notes TEXT NULL
);

----

CREATE TABLE IF NOT EXISTS ScheduledEvents (
    Id INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    WorkingContextId INTEGER NOT NULL,
    ScheduledForUtc TEXT NOT NULL,
    FiredAtUtc TEXT NULL,
    Status TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL,
    LastModifiedUtc TEXT NOT NULL,
    Notes TEXT NULL,
    FOREIGN KEY(WorkingContextId) REFERENCES WorkingContexts(Id)
);

CREATE INDEX IF NOT EXISTS Idx_ScheduledEvents_ScheduledForUtc ON ScheduledEvents(ScheduledForUtc);
CREATE INDEX IF NOT EXISTS Idx_ScheduledEvents_Status ON ScheduledEvents(Status);


-- Tag Junction Tables --


CREATE TABLE IF NOT EXISTS ScheduledEventTags (
    Id INTEGER PRIMARY KEY,
    TagId INTEGER NOT NULL,
    ScheduledEventId INTEGER NOT NULL,
    FOREIGN KEY(TagId) REFERENCES Tags(Id),
    FOREIGN KEY(ScheduledEventId) REFERENCES ScheduledEvents(Id)
);

CREATE INDEX IF NOT EXISTS Idx_ScheduledEventTags_TagId ON ScheduledEventTags(TagId);
CREATE INDEX IF NOT EXISTS Idx_ScheduledEventTags_ScheduledEventId ON ScheduledEventTags(ScheduledEventId);

CREATE UNIQUE INDEX IF NOT EXISTS uIdx_ScheduledEventTags_TagId_ScheduledEventId ON ScheduledEventTags(TagId, ScheduledEventId);

----

CREATE TABLE IF NOT EXISTS WorkingContextTags (
    Id INTEGER PRIMARY KEY,
    TagId INTEGER NOT NULL,
    WorkingContextId INTEGER NOT NULL,
    FOREIGN KEY(TagId) REFERENCES Tags(Id),
    FOREIGN KEY(WorkingContextId) REFERENCES WorkingContexts(Id)
);

CREATE INDEX IF NOT EXISTS Idx_WorkingContextTags_TagId ON WorkingContextTags(TagId);
CREATE INDEX IF NOT EXISTS Idx_WorkingContextTags_WorkingContextId ON WorkingContextTags(WorkingContextId);

CREATE UNIQUE INDEX IF NOT EXISTS uIdx_WorkingContextTags_TagId_WorkingContextId ON WorkingContextTags(TagId, WorkingContextId);

----

CREATE TABLE IF NOT EXISTS ContextFragmentTags (
    Id INTEGER PRIMARY KEY,
    TagId INTEGER NOT NULL,
    ContextFragmentId INTEGER NOT NULL,
    FOREIGN KEY(TagId) REFERENCES Tags(Id),
    FOREIGN KEY(ContextFragmentId) REFERENCES ContextFragments(Id)
);

CREATE INDEX IF NOT EXISTS Idx_ContextFragmentTags_TagId ON ContextFragmentTags(TagId);
CREATE INDEX IF NOT EXISTS Idx_ContextFragmentTags_ContextFragmentId ON ContextFragmentTags(ContextFragmentId);

CREATE UNIQUE INDEX IF NOT EXISTS uIdx_ContextFragmentTags_TagId_ContextFragmentId ON ContextFragmentTags(TagId, ContextFragmentId);


-- Other Junction Tables --


CREATE TABLE IF NOT EXISTS ContextFragmentSources (
    Id INTEGER PRIMARY KEY,
    ContextFragmentId INTEGER NOT NULL,
    SourceId INTEGER NOT NULL,
    FOREIGN KEY(ContextFragmentId) REFERENCES ContextFragments(Id),
    FOREIGN KEY(SourceId) REFERENCES Sources(Id)
);

CREATE INDEX IF NOT EXISTS Idx_ContextFragmentSources_ContextFragmentId ON ContextFragmentSources(ContextFragmentId);
CREATE INDEX IF NOT EXISTS Idx_ContextFragmentSources_SourceId ON ContextFragmentSources(SourceId);

CREATE UNIQUE INDEX IF NOT EXISTS uIdx_ContextFragmentSources_ContextFragmentId_SourceId ON ContextFragmentSources(ContextFragmentId, SourceId);

----

CREATE TABLE IF NOT EXISTS WorkingContextFragments (
    Id INTEGER PRIMARY KEY,
    WorkingContextId INTEGER NOT NULL,
    ContextFragmentId INTEGER NOT NULL,
    Weight REAL NOT NULL,
    Order INTEGER NOT NULL,
    FOREIGN KEY(WorkingContextId) REFERENCES WorkingContexts(Id),
    FOREIGN KEY(ContextFragmentId) REFERENCES ContextFragments(Id)
);

CREATE INDEX IF NOT EXISTS Idx_WorkingContextFragments_WorkingContextId ON WorkingContextFragments(WorkingContextId);

CREATE UNIQUE INDEX IF NOT EXISTS uIdx_WorkingContextFragments_WorkingContextId_ContextFragmentId ON WorkingContextFragments(WorkingContextId, ContextFragmentId);


-- System Tables --


CREATE TABLE IF NOT EXISTS Migrations (
    Id INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    AppliedUtc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS uIdx_Migrations_Name ON Migrations(Name);
```

---

## Data Layer

### Entities

All entities extend `BaseEntity`:

```
BaseEntity
  Id: long
  CreatedUtc: DateTimeOffset
  LastModifiedUtc: DateTimeOffset
  Notes: string?
  IsNew: bool  [not mapped to DB]
```

---

**`ContextFragmentEntity`** → table `ContextFragments`

```
FragmentType: ContextFragmentType
Status: ContextFragmentStatus
Content: string
Summary: string?
LastAccessedUtc: DateTimeOffset
Importance: float
Confidence: float
IsProtected: bool
[Computed] Sources: List<SourceEntity>
[Computed] Tags: List<TagEntity>
```

**`ContextFragmentType`** (enum):
`Core, Identity, Relational, ChatMessage, Proposal, Summary, ScratchPad, Personal, ActionResponse, AuditLog, ActionLog`

- `ScratchPad` and `ActionResponse` fragments are never persisted to the DB
- `AuditLog` is used when the remote peer queries the audit log — the result is surfaced as a temporary fragment in context. `ActionLog` is the same for actions.

**`ContextFragmentStatus`** (enum): `Active, Archived, Deleted`

---

**`WorkingContextEntity`** → table `WorkingContexts`

```
Name: string
Summary: string
LastAccessedUtc: DateTimeOffset
[Computed] ContextFragments: List<WeightedContextFragment>
```

**`WeightedContextFragment`** (extends `ContextFragmentEntity`):

```
Weight: float
Order: int
```

Default fragment ordering within a context: `Core → Identity → Relational → [other types] → ChatMessage`

---

**`TagEntity`** → table `Tags`

```
Name: string
ParentTagId: long?
Description: string?
[Computed] ChildTags: List<TagEntity>
```

---

**`SourceEntity`** → table `Sources`

```
SourceType: SourceType
Name: string?
```

**`SourceType`** (enum): `System, RemotePeer, LocalPeer, DerivedFromFragments`

Additional values may be added as new use cases emerge.

---

**`AuditLogEntity`** → table `AuditLogs`

```
SessionId: string
WorkingContextId: long?
EventType: AuditEventType
TargetType: string
TargetId: long
SourceId: long?
OldData: string?
NewData: string?
```

**`AuditEventType`** (enum): [TBD — enumerate common event types as they emerge]

---

**`ActionLogEntity`** → table `ActionLogs`

```
SessionId: string
WorkingContextId: long
ActionType: string
Payload: string?
Result: string?
```

---

**`ScheduledEventEntity`** → table `ScheduledEvents`

```
Name: string
WorkingContextId: long
ScheduledForUtc: DateTimeOffset
FiredAtUtc: DateTimeOffset?
Status: ScheduledEventStatus
[Computed] Tags: List<TagEntity>
```

**`ScheduledEventStatus`** (enum): `Pending, Fired, Cancelled`

---

**`MigrationRecord`** → table `Migrations`

Not a `BaseEntity`. Managed exclusively by `DatabaseManager` via raw SQL — no repository.

```
Id: long
Name: string
AppliedAtUtc: DateTimeOffset
```

---

### Repository Base Pattern

`EntityRepository<T>` owns the connection string and is the only class that touches connections
directly. No derived class opens a connection.

**Collection population** is handled by passing an optional mapper `Func` to the base query methods.
The mapper receives the hydrated entity and the open `IDbConnection` so it can issue follow-up queries.

**Transaction support**: `BeginTransactionAsync` opens a connection, starts a transaction, and returns
a `RepositoryTransaction` wrapper. Methods that accept a `RepositoryTransaction` use
`transaction.Connection` instead of opening a new connection, so they participate in the same
transaction. The canonical pattern is: save entity → write audit log entry → commit, all within one
`RepositoryTransaction`.

---

**`IEntityRepository<T>`** (base interface — minimal public surface):

- `GetByIdAsync`
- `SaveAsync`

---

**`EntityRepository<T>`** (base implementation):

- `GetByIdAsync` (optional collection mapper)
- `QueryAsync` (optional collection mapper)
- `GetFirstOrDefaultAsync` (optional collection mapper)
- `ExecuteAsync`
- `SaveAsync` (accepts optional `RepositoryTransaction`; uses its connection when provided, otherwise opens a new one)
- `BeginTransactionAsync` (returns `RepositoryTransaction`)

---

**`RepositoryTransaction`** (disposable wrapper — disposes both transaction and connection):

- `Connection`
- `Transaction`
- `CommitAsync`
- `Dispose`

---

### Entity Repositories

**`IContextFragmentRepository` / `ContextFragmentRepository`**

- `GetByIdAsync`
- `GetByTypeAsync`
- `GetActiveByTypeAsync`
- `GetByTagAsync`
- `GetCoreFragmentsAsync` — returns all active Core fragments; used when seeding a new WorkingContext
- `SaveAsync`
- `SoftDeleteAsync`

---

**`IWorkingContextRepository` / `WorkingContextRepository`**

- `GetMostRecentAsync` — returns the context with the most recent `LastAccessedUtc`, or null
- `GetByIdAsync`
- `CreateAsync`
- `AddFragmentAsync`
- `RemoveFragmentAsync`
- `UpdateFragmentOrderAsync`
- `UpdateFragmentWeightAsync`
- `TouchAsync` — updates `LastAccessedUtc` to now
- `SaveAsync`

---

**`ITagRepository` / `TagRepository`**

- `GetByIdAsync`
- `GetByNameAsync`
- `GetChildrenAsync`
- `SaveAsync`

---

**`ISourceRepository` / `SourceRepository`**

- `GetByIdAsync`
- `GetByTypeAsync`
- `SaveAsync`

---

**`IAuditLogRepository` / `AuditLogRepository`**

- `LogAsync` (accepts optional `RepositoryTransaction` so it can share a transaction with the triggering change)
- `GetByTargetAsync`
- `GetBySessionAsync`

---

**`IActionLogRepository` / `ActionLogRepository`**

- `LogAsync`
- `GetBySessionAsync`
- `GetByWorkingContextAsync`

---

**`IScheduledEventRepository` / `ScheduledEventRepository`**

- `GetDueEventsAsync` — events where `ScheduledForUtc <= now` and `Status = Pending`
- `GetByWorkingContextAsync`
- `SaveAsync`
- `MarkFiredAsync`
- `CancelAsync`

---

### DatabaseManager

Replaces `DatabaseBootstrap`. Handles all non-query database concerns: migrations, seeding, and
(eventually) backups. Registered as singleton.

**`IDatabaseManager`**:

- `InitializeAsync` — entry point; runs `MigrateAsync` then `SeedAsync`
- `MigrateAsync` — loads embedded migration files, applies any not yet recorded in `Migrations`
- `SeedAsync` — seeds initial data on a fresh database

Internal (not on interface):

- `GetAppliedMigrationsAsync`
- `ApplyMigrationAsync`
- `SeedCoreFragmentsAsync` — loads `Seeds/core_fragments.json` and inserts any fragments not already present
- `EnsureSystemSourceAsync` — inserts a `SourceType.System` row if none exists (required before any system-initiated audit entries)

**Seed format** (`Seeds/core_fragments.json`): a JSON array of objects with fields `Content`,
`Summary`, `Importance`, `Confidence`, `IsProtected`, and `Notes`. Each maps to a
`ContextFragmentEntity` with `FragmentType = Core` and `Status = Active`. On a fresh database these
are inserted; on subsequent startups, any fragment whose exact `Content` already exists in
`ContextFragments` is skipped. Populating the initial `WorkingContext` with these fragments is the
Orchestrator's responsibility (startup step 3), not `DatabaseManager`'s.

`EnsureSystemSourceAsync` sets `ISessionContext.SourceId` to the System source's ID as a side
effect, so repositories can write audit entries immediately after `InitializeAsync` returns.

---

## Services & DI

### IoC

No changes to the attribute-based registration system. `[Singleton]` and `[Service]` attributes
remain as-is. `Initializer.cs` is still the composition root.

### MediatR Notifications

All notification types live in `Persistence.Core`. Both Console handlers and Core handlers are
registered at startup via the standard MediatR assembly scan.

**Console → Core:**

| Notification | Purpose |
|---|---|
| `HumanInputReceived` | Local peer submitted a message |

**Core → Console:**

| Notification | Purpose |
|---|---|
| `ProcessingStarted` | Show thinking indicator |
| `RemotePeerReplied` | Display a plain-text reply from the remote peer |
| `ActionsExecuted` | Display action results |
| `ContextModified` | Optional display summary of context changes applied |
| `TurnCompleted` | Restore input prompt; turn is fully over |
| `WakeUpEventFired` | Show wake-up event details before processing begins |

Console has one `INotificationHandler<T>` per Core → Console notification type. Each handler calls
the appropriate `IDisplayProvider` method.

---

## Runtime

### Orchestrator

Still owns the input loop and display lifecycle, but significantly slimmed down. No longer
coordinates the turn directly — that moves to `TurnHandler`.

Also implements `INotificationHandler<WakeUpEventFired>` to manage display lifecycle around
wake-ups (clear partial input before, restore input prompt after).

**Startup sequence:**

1. Call `DatabaseManager.InitializeAsync`
2. Call `WorkingContextRepository.GetMostRecentAsync`
3. If null: call `CreateAsync`, then populate with `ContextFragmentRepository.GetCoreFragmentsAsync`
4. Start `WakeUpMonitor`
5. Enter input loop

**Input loop:**

- Read input via `IDisplayProvider.RequestInputAsync`
- Handle slash commands directly (no MediatR)
- Publish `HumanInputReceived` for all other input

Methods:

- `RunAsync`
- `InitializeAsync`
- `HandleCommandAsync`
- `Handle` (INotificationHandler<WakeUpEventFired>)

---

### TurnHandler

Core-side `INotificationHandler<HumanInputReceived>`. Owns the full turn lifecycle. Registered as
singleton so it can hold state (the input queue).

Incoming `HumanInputReceived` notifications are enqueued. If a turn is already in progress, the new
input is queued and appended to the context before the next send — the local peer can type
while the remote peer is processing.

**Turn loop (per message dequeued):**

1. Compose context (working context fragments + queued pending messages)
2. Publish `ProcessingStarted`
3. Call model
4. Parse response → determine response type (context modifications / actions / plain text)
5. Dispatch to appropriate handler
6. If response includes `continue: true` → go to step 1 with updated context
7. If `continue: false` (or absent) → publish `TurnCompleted`

The `continue` flag is available on any response type. It signals whether the remote peer
wants the context sent back immediately or wants to hand control back to the local peer.

Methods:

- `Handle` (INotificationHandler implementation — enqueues input, starts turn if idle)
- `ProcessTurnAsync`
- `DispatchResponseAsync`
- `ApplyContextModificationsAsync`
- `ExecuteActionsAsync`

---

### WakeUpMonitor

Background service (managed `Task`, started by `Orchestrator.InitializeAsync`). Polls
`ScheduledEventRepository.GetDueEventsAsync` on a short interval and publishes
`WakeUpEventFired` for each due event.

Methods:

- `StartAsync`
- `StopAsync`
- `PollAsync` (internal loop)

---

## Remote Peer Response Format

[TBD — to be designed in a separate discussion. Key constraints already decided:
- Response can be one of: context modifications, action array, plain text — or a mix
- Any response type can include a `continue` flag
- Context modifications should support JSON-patch-style edits, inserting new fragments, creating
  summary fragments from multiple existing ones, removing fragments from the current context,
  and reordering fragments
- Actions should support scheduling wake-up events, executing available shell commands, and
  querying the audit log]

---

## Working Context Format

[TBD — the exact structure sent to the remote peer. Known requirements:
- Include fragment ID and source(s) alongside content
- Ordered by the `Order` field on `WorkingContextFragments`
- Default order: Core → Identity → Relational → [other types] → ChatMessage]
