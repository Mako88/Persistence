-- Provenance by session: which harness session wrote a fragment or said an utterance.
--
-- A peer can run as several sessions at once (two Claude Code windows on one store). With nothing to
-- tell them apart, their conversations interleaved in the core as one stream, each session reading the
-- other's "tests pass, publishing" as its own work, and the one working note was replaced by whichever
-- wrote last. With the session kept, a session reads its own conversation and its own note, sees the
-- other live sessions as one labelled line each, and an authored fragment says which session wrote it.
-- NULL for everything kept before this, and for writes made outside any session.

ALTER TABLE ContextFragments ADD COLUMN SessionId TEXT NULL;

CREATE INDEX IF NOT EXISTS IX_ContextFragments_SessionId
    ON ContextFragments (SessionId) WHERE SessionId IS NOT NULL;
