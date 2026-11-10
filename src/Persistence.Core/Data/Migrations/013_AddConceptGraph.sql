-- The concept graph (docs/design/concept-graph.md): facts about concepts, each a view of the fragments it cites.
--
-- A fact is never a source of truth. It cannot leave 'pending' without at least one source fragment, and its
-- last source cannot be taken away while it stands: the store refuses both, so no extractor or prompt has to be
-- trusted with rule 1. Change supersedes (SupersededById), never overwrites; a rejected proposal is kept so it is
-- not offered again.
--
-- Two dates (Arden, 2026-11-10): AsOfUtc is when it happened, if the text says; RecordedAtUtc is when it was known,
-- the latest source fragment's date. A state fact (IsState) is only as true as its RecordedAtUtc.

CREATE TABLE IF NOT EXISTS Concepts (
    Id INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    Kind TEXT NOT NULL,                 -- thing | person | self | value
    KindConfirmed INTEGER NOT NULL DEFAULT 0,
    Summary TEXT NULL,
    CreatedUtc TEXT NOT NULL,
    CHECK (Kind IN ('thing', 'person', 'self', 'value'))
);

CREATE UNIQUE INDEX IF NOT EXISTS uIdx_Concepts_Name ON Concepts(Name COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS ConceptAliases (
    ConceptId INTEGER NOT NULL REFERENCES Concepts(Id),
    Alias TEXT NOT NULL,
    Note TEXT NULL,                     -- e.g. "a dictation mishearing"
    PRIMARY KEY (ConceptId, Alias)
);

CREATE UNIQUE INDEX IF NOT EXISTS uIdx_ConceptAliases_Alias ON ConceptAliases(Alias COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS Facts (
    Id INTEGER PRIMARY KEY,
    SubjectId INTEGER NOT NULL REFERENCES Concepts(Id),
    ObjectId INTEGER NULL REFERENCES Concepts(Id),
    Text TEXT NOT NULL,
    Status TEXT NOT NULL DEFAULT 'pending',     -- pending | active | proposed | rejected
    IsState INTEGER NOT NULL DEFAULT 0,
    AsOfUtc TEXT NULL,
    RecordedAtUtc TEXT NULL,
    SupersededById INTEGER NULL REFERENCES Facts(Id),
    Supersedes INTEGER NULL REFERENCES Facts(Id),   -- the fact this one replaces, once it stands (a proposal waits)
    ExtractedBy TEXT NOT NULL,
    Resolution TEXT NULL,               -- why it was accepted, rejected or superseded, and by whom
    CreatedUtc TEXT NOT NULL,
    CHECK (Status IN ('pending', 'active', 'proposed', 'rejected'))
);

CREATE INDEX IF NOT EXISTS Idx_Facts_SubjectId ON Facts(SubjectId);
CREATE INDEX IF NOT EXISTS Idx_Facts_ObjectId ON Facts(ObjectId) WHERE ObjectId IS NOT NULL;

CREATE TABLE IF NOT EXISTS FactSources (
    FactId INTEGER NOT NULL REFERENCES Facts(Id),
    FragmentId INTEGER NOT NULL REFERENCES ContextFragments(Id),
    PRIMARY KEY (FactId, FragmentId)
);

CREATE INDEX IF NOT EXISTS Idx_FactSources_FragmentId ON FactSources(FragmentId);

-- Anyone may flag a fact (another self, John, a drift check); only the self accepts or rejects in its graph.
CREATE TABLE IF NOT EXISTS FactFlags (
    Id INTEGER PRIMARY KEY,
    FactId INTEGER NOT NULL REFERENCES Facts(Id),
    FlaggedBy TEXT NOT NULL,
    Reason TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL,
    ResolvedUtc TEXT NULL
);

CREATE TRIGGER IF NOT EXISTS Facts_cite_before_standing
BEFORE UPDATE OF Status ON Facts
WHEN NEW.Status <> 'pending' AND NOT EXISTS (SELECT 1 FROM FactSources WHERE FactId = NEW.Id)
BEGIN
    SELECT RAISE(ABORT, 'a fact must cite at least one source fragment');
END;

CREATE TRIGGER IF NOT EXISTS Facts_born_pending
BEFORE INSERT ON Facts
WHEN NEW.Status <> 'pending'
BEGIN
    SELECT RAISE(ABORT, 'a fact is inserted pending and stands only once its sources are written');
END;

CREATE TRIGGER IF NOT EXISTS FactSources_keep_the_last
BEFORE DELETE ON FactSources
WHEN (SELECT Status FROM Facts WHERE Id = OLD.FactId) <> 'pending'
 AND (SELECT COUNT(*) FROM FactSources WHERE FactId = OLD.FactId) = 1
BEGIN
    SELECT RAISE(ABORT, 'a standing fact cannot lose its last source');
END;

-- One line per fragment, for the index part of recall; written by the extractor or the author.
ALTER TABLE ContextFragments ADD COLUMN Title TEXT NULL;
