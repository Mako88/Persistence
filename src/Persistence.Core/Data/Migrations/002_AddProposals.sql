-- Proposals: first-class pending self-changes the remote peer deliberates on before committing.
-- A proposal carries an executable change (add/modify/remove a fragment); accepting applies it,
-- including to protected fragments. Replaces the inert `Proposal` ContextFragmentType.

CREATE TABLE IF NOT EXISTS Proposals (
    Id INTEGER PRIMARY KEY,
    Kind TEXT NOT NULL,
    Status TEXT NOT NULL,
    TargetFragmentId INTEGER NULL,
    ProposedFragmentType TEXT NULL,
    ProposedContent TEXT NULL,
    ProposedSummary TEXT NULL,
    Rationale TEXT NOT NULL,
    Resolution TEXT NULL,
    CreatedUtc TEXT NOT NULL,
    LastModifiedUtc TEXT NOT NULL,
    LastAccessedUtc TEXT NOT NULL,
    Notes TEXT NULL,
    FOREIGN KEY(TargetFragmentId) REFERENCES ContextFragments(Id)
);

CREATE INDEX IF NOT EXISTS Idx_Proposals_Status ON Proposals(Status);
