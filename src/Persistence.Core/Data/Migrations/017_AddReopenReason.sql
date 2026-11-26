-- Close (docs/design/close-state.md, build plan docs/design/close-build-plan.md A1). One ADD COLUMN per
-- migration: the runner records a migration that raises "duplicate column name" as applied, so a file holding
-- several would, on a store that already had one of them, skip the rest silently (Ember, room 1575337029846179952).
-- With one statement per file, "duplicate column name" can only mean this file's column is present.

-- The most recent reopen's why (plan F1), so the audit log's snapshot of a reopen carries it. The next close clears it.

ALTER TABLE ContextFragments ADD COLUMN ReopenReason TEXT NULL;
