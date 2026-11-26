-- Close (docs/design/close-state.md, build plan docs/design/close-build-plan.md A1). One ADD COLUMN per
-- migration: the runner records a migration that raises "duplicate column name" as applied, so a file holding
-- several would, on a store that already had one of them, skip the rest silently (Ember, room 1575337029846179952).
-- With one statement per file, "duplicate column name" can only mean this file's column is present.

-- The fragment this one was split from: a split part inherits its original's assertion date (close-state.md §2).
-- Existing splits are linked by `pstore backfill-splits`, not here: their notes are untrusted text, and each owner
-- sees the report before anything is written.

ALTER TABLE ContextFragments ADD COLUMN SplitFromId INTEGER NULL REFERENCES ContextFragments(Id);
