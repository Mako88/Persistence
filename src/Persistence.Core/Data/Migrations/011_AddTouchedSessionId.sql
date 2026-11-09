-- Which harness session last wrote or edited a fragment, beside the one that first wrote it (010).
--
-- The core leads with what this session kept, so that what was curated for a compaction comes back after
-- it. Selecting on SessionId counted only what the session added: the curation prompt asks a peer to edit
-- a fragment that is now wrong rather than add beside it, and such an edit, made by this session to a
-- fragment another session wrote, was left out (Arden, 2026-11-09). SessionId stays as written: it is
-- provenance ("session X wrote this", which a refused supersede quotes). TouchedSessionId moves with every
-- add and edit, and LastModifiedUtc says when.
-- Backfilled from SessionId: until now, the only session to touch a fragment was the one that wrote it,
-- as far as the store can tell.

ALTER TABLE ContextFragments ADD COLUMN TouchedSessionId TEXT NULL;

UPDATE ContextFragments SET TouchedSessionId = SessionId WHERE SessionId IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_ContextFragments_TouchedSessionId
    ON ContextFragments (TouchedSessionId) WHERE TouchedSessionId IS NOT NULL;
