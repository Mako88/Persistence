-- Supersession: a fragment can be replaced by a newer one without the older one being erased.
--
-- Editing a fragment overwrites it, and the audit log is the only trace of what it said before. That is
-- the wrong shape for a peer changing its mind: the earlier view is part of how it got here, and "I
-- used to think X, and now think Y because Z" is worth more than Y alone. Forgetting is wrong too, since
-- the earlier view was not a mistake to hide.
--
-- So a superseded fragment keeps its content, is archived (out of the core, still searchable), and
-- points at what replaced it. The pointer lives on the older row because a fragment is superseded at
-- most once, while one fragment may replace several (a merge). Following the pointers forward from any
-- old view always reaches the current one.

ALTER TABLE ContextFragments ADD COLUMN SupersededById INTEGER NULL REFERENCES ContextFragments(Id);

CREATE INDEX IF NOT EXISTS IX_ContextFragments_SupersededById
    ON ContextFragments (SupersededById) WHERE SupersededById IS NOT NULL;
