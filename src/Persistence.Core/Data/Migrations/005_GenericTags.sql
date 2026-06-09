-- Consolidate the three per-entity tag junctions (ContextFragmentTags, WorkingContextTags,
-- ScheduledEventTags) into one polymorphic EntityTags table keyed by (EntityType, EntityId), so a
-- single tag-apply/resolve/search path serves fragments, working contexts, events, and anything
-- added later — no new junction table per taggable type.

CREATE TABLE IF NOT EXISTS EntityTags (
    Id INTEGER PRIMARY KEY,
    TagId INTEGER NOT NULL,
    EntityType TEXT NOT NULL,
    EntityId INTEGER NOT NULL,
    FOREIGN KEY(TagId) REFERENCES Tags(Id)
);

CREATE INDEX IF NOT EXISTS Idx_EntityTags_TagId ON EntityTags(TagId);
CREATE INDEX IF NOT EXISTS Idx_EntityTags_Entity ON EntityTags(EntityType, EntityId);
CREATE UNIQUE INDEX IF NOT EXISTS uIdx_EntityTags_Tag_Entity ON EntityTags(TagId, EntityType, EntityId);

-- Carry existing links over. EntityType matches the entity class name (same convention as AuditLogs.TargetType).
INSERT INTO EntityTags (TagId, EntityType, EntityId)
    SELECT TagId, 'ContextFragmentEntity', ContextFragmentId FROM ContextFragmentTags;
INSERT INTO EntityTags (TagId, EntityType, EntityId)
    SELECT TagId, 'WorkingContextEntity', WorkingContextId FROM WorkingContextTags;
INSERT INTO EntityTags (TagId, EntityType, EntityId)
    SELECT TagId, 'ScheduledEventEntity', ScheduledEventId FROM ScheduledEventTags;

DROP TABLE ContextFragmentTags;
DROP TABLE WorkingContextTags;
DROP TABLE ScheduledEventTags;
