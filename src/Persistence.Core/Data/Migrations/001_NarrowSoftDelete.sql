-- Narrow soft-delete (IsDeleted) to peer memory only: ContextFragments and WorkingContexts keep it.
-- The other tables never actually soft-deleted — tags hard-delete (DeleteTreeAsync), sources are
-- reference data, scheduled events use Status, and action logs are append-only — so the column and
-- its read filters were dead weight. None of these columns are indexed or referenced by a
-- constraint, so DROP COLUMN is safe.

ALTER TABLE Tags DROP COLUMN IsDeleted;
ALTER TABLE Sources DROP COLUMN IsDeleted;
ALTER TABLE ScheduledEvents DROP COLUMN IsDeleted;
ALTER TABLE ActionLogs DROP COLUMN IsDeleted;
