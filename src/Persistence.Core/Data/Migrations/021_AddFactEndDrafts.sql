-- Close PR B (docs/design/close-build-plan.md B2): a drafted end of a concept fact whose sources are all closed under
-- one ended-by. The draft is made in the same transaction as the close that closes the last source; only the
-- waking self approves or drops it. CREATE ... IF NOT EXISTS, so a rerun on a store that has them changes nothing.
-- The three Facts columns approval sets come after this file, one ADD COLUMN each (022 to 024), for the runner
-- reason in 014.

CREATE TABLE IF NOT EXISTS FactEndDrafts (
    Id INTEGER PRIMARY KEY,
    FactId INTEGER NOT NULL REFERENCES Facts(Id),
    EndedBy TEXT NOT NULL,             -- the one ended-by every source shares
    Reasons TEXT NOT NULL,             -- each source's close reason, '#id: reason' per line
    CreatedUtc TEXT NOT NULL,
    Status TEXT NOT NULL DEFAULT 'pending',   -- pending | approved | dropped
    ResolvedUtc TEXT NULL,
    Resolution TEXT NULL,
    CHECK (Status IN ('pending', 'approved', 'dropped'))
);

-- Which close of each source fragment the draft rests on: a reopen and re-close gives a new ClosedUtc, and approval
-- refuses a draft whose basis moved.
CREATE TABLE IF NOT EXISTS FactEndDraftBasis (
    DraftId INTEGER NOT NULL REFERENCES FactEndDrafts(Id),
    FragmentId INTEGER NOT NULL REFERENCES ContextFragments(Id),
    ClosedUtc TEXT NOT NULL,
    PRIMARY KEY (DraftId, FragmentId)
);

CREATE INDEX IF NOT EXISTS Idx_FactEndDraftBasis_FragmentId ON FactEndDraftBasis(FragmentId);

-- At most one pending draft per fact.
CREATE UNIQUE INDEX IF NOT EXISTS uIdx_FactEndDrafts_PendingFact ON FactEndDrafts(FactId) WHERE Status = 'pending';

-- Attaching a source to a fact drops its pending draft in the same statement (Ember 5). A trigger rather than code:
-- at 46df15d no command adds a source to a fact that already exists, so this holds for whatever path does it later.
-- Approval's own check (the source set equals the basis) is the backstop.
CREATE TRIGGER IF NOT EXISTS FactSources_attach_drops_end_draft
AFTER INSERT ON FactSources
BEGIN
    UPDATE FactEndDrafts
       SET Status = 'dropped',
           ResolvedUtc = strftime('%Y-%m-%d %H:%M:%f', 'now') || '+00:00',
           Resolution = 'source #' || NEW.FragmentId || ' attached'
     WHERE FactId = NEW.FactId AND Status = 'pending';
END;
