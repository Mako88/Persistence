-- Semantic (vector) associative recall.
--
-- Recall already existed as an idea -- IMemorySurfacer surfaces relevant memories on their own, "the
-- way a mind surfaces a related thought" -- but it was matched lexically (FTS5/BM25), so a memory was
-- only reachable through words it literally contained. A peer that wrote "I keep mistaking intent for
-- outcome" could not surface it while discussing "believing something happened because I meant it".
-- Storing an embedding per fragment makes recall associative on meaning rather than on vocabulary.
--
-- Shape notes:
--
--   Composite PK (ContextFragmentId, Model) -- one vector per fragment *per embedding model*. Cosine
--   between vectors from different models is meaningless rather than merely noisy, so the model is
--   part of the identity of a vector, not metadata about it. Switching models leaves the old rows
--   inert (nothing queries them) instead of corrupting the ranking, and switching back reuses them.
--
--   ContentHash -- SHA-256 of the embedded text, so an edited fragment re-embeds and an unchanged one
--   does not. Content-addressed rather than timestamp-based: restoring a fragment's earlier text
--   correctly reuses the vector it already had.
--
--   Vector BLOB -- little-endian float32, packed. SQLite has no native vector type and peer stores are
--   small enough (hundreds to low thousands of authored fragments) that scoring in memory is cheap;
--   an ANN index would be premature here and would add a native dependency to a store that is
--   deliberately a single portable file.
--
--   ON DELETE CASCADE -- an embedding is derived data with no independent meaning. Note this fires on
--   *hard* delete only; the peer's own "forget" is a soft delete (IsDeleted), which correctly leaves
--   the vector in place so an undo restores a fragment that is immediately recallable again.

CREATE TABLE IF NOT EXISTS FragmentEmbeddings (
    ContextFragmentId INTEGER NOT NULL REFERENCES ContextFragments (Id) ON DELETE CASCADE,
    Model             TEXT    NOT NULL,
    Dimensions        INTEGER NOT NULL,
    Vector            BLOB    NOT NULL,
    ContentHash       TEXT    NOT NULL,
    UpdatedUtc        TEXT    NOT NULL,
    PRIMARY KEY (ContextFragmentId, Model)
);

-- Recall loads every vector for the active model in one sweep, then scores in memory.
CREATE INDEX IF NOT EXISTS IX_FragmentEmbeddings_Model
    ON FragmentEmbeddings (Model);
