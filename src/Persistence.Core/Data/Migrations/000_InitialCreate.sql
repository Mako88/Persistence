

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
    IsDeleted INTEGER NOT NULL DEFAULT 0,
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
    LastAccessedUtc TEXT NOT NULL,
    Description TEXT NULL,
    IsDeleted INTEGER NOT NULL DEFAULT 0,
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
    LastAccessedUtc TEXT NOT NULL,
    IsDeleted INTEGER NOT NULL DEFAULT 0,
    Notes TEXT NULL
);

CREATE INDEX IF NOT EXISTS Idx_Sources_SourceType ON Sources(SourceType);

----

CREATE TABLE IF NOT EXISTS AuditLogs (
    Id INTEGER PRIMARY KEY,
    SessionId TEXT NOT NULL,
    WorkingContextId INTEGER NULL,
    EventType TEXT NOT NULL,
    TargetType TEXT NOT NULL,
    TargetId INTEGER NOT NULL,
    SourceId INTEGER NOT NULL,
    OldData TEXT NULL,
    NewData TEXT NULL,
    CreatedUtc TEXT NOT NULL,
    FOREIGN KEY(SourceId) REFERENCES Sources(Id),
    FOREIGN KEY(WorkingContextId) REFERENCES WorkingContexts(Id) ON DELETE SET NULL
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
    LastAccessedUtc TEXT NOT NULL,
    IsDeleted INTEGER NOT NULL DEFAULT 0,
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
    IsDeleted INTEGER NOT NULL DEFAULT 0,
    Notes TEXT NULL
);

----

CREATE TABLE IF NOT EXISTS ScheduledEvents (
    Id INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    WorkingContextId INTEGER NOT NULL,
    ScheduledForUtc TEXT NOT NULL,
    TriggeredAtUtc TEXT NULL,
    Status TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL,
    LastModifiedUtc TEXT NOT NULL,
    LastAccessedUtc TEXT NOT NULL,
    IsDeleted INTEGER NOT NULL DEFAULT 0,
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
    [Order] INTEGER NOT NULL,
    FOREIGN KEY(WorkingContextId) REFERENCES WorkingContexts(Id),
    FOREIGN KEY(ContextFragmentId) REFERENCES ContextFragments(Id)
);

CREATE INDEX IF NOT EXISTS Idx_WorkingContextFragments_WorkingContextId ON WorkingContextFragments(WorkingContextId);

CREATE UNIQUE INDEX IF NOT EXISTS uIdx_WorkingContextFragments_WorkingContextId_ContextFragmentId ON WorkingContextFragments(WorkingContextId, ContextFragmentId);