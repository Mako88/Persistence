-- Close (docs/design/close-state.md, build plan docs/design/close-build-plan.md A1). One ADD COLUMN per
-- migration: the runner records a migration that raises "duplicate column name" as applied, so a file holding
-- several would, on a store that already had one of them, skip the rest silently (Ember, room 1575337029846179952).
-- With one statement per file, "duplicate column name" can only mean this file's column is present.

-- What ended it: #<fragment id> | commit:<repo>@<sha> | msg:<room message id>. Null for a --state close without one.

ALTER TABLE ContextFragments ADD COLUMN EndedBy TEXT NULL;
