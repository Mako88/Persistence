-- Close (build plan A1): the indexes, after every column they name was added by 014 to 019, so their
-- "no such column" path can't fire on a store that ran those.

CREATE INDEX IF NOT EXISTS IX_ContextFragments_SplitFromId ON ContextFragments (SplitFromId) WHERE SplitFromId IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_ContextFragments_ClosedUtc ON ContextFragments (ClosedUtc) WHERE ClosedUtc IS NOT NULL;
