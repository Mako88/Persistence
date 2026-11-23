"""Prune a COPY of a store's concept layer: drop the facts whose evidence a predicate rejects, then the concepts and
aliases that stood only on them. The one place outside the store that knows the concept tables (migration 013), so
the eval's per-case cut and the snapshot build's gold/`given` drop share it (Arden and Claude, #62, room
1571765945196748801). The store has no verb that removes a fact, concept or alias, and `reject` can't stand in: a
rejected text is never proposed again, which would bias a run.

    from prune import prune, at_or_above, cites_any
    report = prune(db, at_or_above(first_fragment), scratch=work_dir)     # the eval, per case
    report = prune(db, cites_any(gold | given), scratch=snapshot_dir)     # the snapshot build

`drop(ids)` gets a fact's evidence (its FactSources fragment ids, a non-empty frozenset) and returns True to drop it.
A fact with no source is undated: never passed to the predicate, kept, and listed in the report.

Refusals, each a PruneRefused before anything is written:
- the schema is not 013: the latest recorded migration must be 013 and the five concept tables must have exactly the
  columns pinned below. A later migration breaks the eval loudly instead of quietly pruning the wrong rows;
- the database is not inside `scratch`, or is the live store (PERSISTENCE_STORE_DB, or a file under a store
  directory: /home/*/store, /opt/persistence).

What it does, in one transaction:
1. drops each fact the predicate rejects, with its FactSources and FactFlags;
2. un-supersedes: a kept fact whose SupersededById (or Supersedes) named a dropped fact has it cleared, so the copy
   holds the state as it stood without the dropped evidence (the store marks supersession only by that link);
3. drops each concept that had facts (as subject or object) and has none left, with its aliases. A concept that never
   had a fact is kept and listed as undated, and so is every alias of a kept concept: neither table has a source
   column, so their only evidence is their facts' sources.
"""
import glob
import os
import re
import sqlite3
from dataclasses import dataclass, field

MIGRATION = "013_AddConceptGraph.sql"   # recorded as Persistence.Data.Migrations.013_AddConceptGraph.sql

# The pin: the concept tables as migration 013 builds them. test_prune checks this against the real migration file.
SCHEMA_013 = {
    "Concepts": ("Id", "Name", "Kind", "KindConfirmed", "Summary", "CreatedUtc"),
    "ConceptAliases": ("ConceptId", "Alias", "Note"),
    "Facts": ("Id", "SubjectId", "ObjectId", "Text", "Status", "IsState", "AsOfUtc", "RecordedAtUtc", "SupersededById",
              "Supersedes", "ExtractedBy", "Resolution", "CreatedUtc"),
    "FactSources": ("FactId", "FragmentId"),
    "FactFlags": ("Id", "FactId", "FlaggedBy", "Reason", "CreatedUtc", "ResolvedUtc"),
}

LIVE_ROOTS = ("/home/*/store", "/opt/persistence")


class PruneRefused(Exception):
    """Nothing was written."""


@dataclass
class PruneReport:
    facts: list = field(default_factory=list)            # dropped fact ids
    concepts: list = field(default_factory=list)         # dropped concept ids
    aliases: list = field(default_factory=list)          # dropped (concept id, alias)
    unsuperseded: list = field(default_factory=list)     # kept fact ids whose supersession link named a dropped fact
    undated_facts: list = field(default_factory=list)    # kept: no source
    undated_concepts: list = field(default_factory=list) # kept: never had a fact
    kept_aliases: list = field(default_factory=list)     # kept with their concept; aliases carry no evidence of their own

    def summary(self):
        """Ids and counts only, never text (the eval's reports print no store content)."""
        return (f"pruned {len(self.facts)} facts, {len(self.concepts)} concepts, {len(self.aliases)} aliases; "
                f"un-superseded {len(self.unsuperseded)}; kept undated: {len(self.undated_facts)} facts "
                f"{sorted(self.undated_facts)}, {len(self.undated_concepts)} concepts {sorted(self.undated_concepts)}, "
                f"{len(self.kept_aliases)} aliases (no evidence column)")


def at_or_above(first):
    """The eval's cut for a case whose first fragment is `first`: live catch-up reaching it has seen only lower ids,
    so a fact citing `first` or anything later is a leak, even if it also cites an earlier one."""
    first = int(first)
    return lambda ids: max(ids) >= first


def cites_any(ids):
    """The snapshot build's cut: a fact citing any of `ids` (the gold fragments and their `given` ones)."""
    ids = frozenset(int(i) for i in ids)
    return lambda evidence: not ids.isdisjoint(evidence)


def check_schema(con):
    """Raise PruneRefused unless this is the 013 concept schema."""
    try:
        names = [r[0] for r in con.execute("SELECT Name FROM Migrations")]
    except sqlite3.DatabaseError as e:
        raise PruneRefused(f"not a store (no Migrations table: {e})") from e
    numbered = sorted((int(m.group(1)), n) for n in names if (m := re.search(r"(?:^|\.)(\d{3})_[^.]*\.sql$", n)))
    if not numbered or not numbered[-1][1].endswith(MIGRATION):
        latest = numbered[-1][1] if numbered else "none"
        raise PruneRefused(f"schema drift: prune is pinned to migration {MIGRATION}, this store's latest is {latest}. "
                           "Update scripts/concepts/eval/prune.py (and its pin) for the new schema before pruning.")
    for table, cols in SCHEMA_013.items():
        have = tuple(r[1] for r in con.execute(f"PRAGMA table_info({table})"))
        if have != cols:
            raise PruneRefused(f"schema drift: {table} has columns {have}, prune is pinned to {cols} ({MIGRATION})")


def check_path(db, scratch):
    real = os.path.realpath(db)
    root = os.path.realpath(scratch)
    if os.path.commonpath([real, root]) != root or real == root:
        raise PruneRefused(f"refused: {db} is not inside the scratch directory {scratch}; prune only works on copies")
    live = os.environ.get("PERSISTENCE_STORE_DB")
    if live and os.path.realpath(live) == real:
        raise PruneRefused(f"refused: {db} is the live store (PERSISTENCE_STORE_DB)")
    for pattern in LIVE_ROOTS:
        for d in glob.glob(pattern):
            d = os.path.realpath(d)
            if os.path.commonpath([real, d]) == d:
                raise PruneRefused(f"refused: {db} is under a store directory ({d}); copy it out first")
    if not os.path.isfile(real):
        raise PruneRefused(f"refused: {db} does not exist")


def prune(db, drop, *, scratch):
    """Prune the concept layer of the store copy at `db` (inside `scratch`) by `drop(evidence ids) -> bool`.
    Returns a PruneReport. Raises PruneRefused, writing nothing, on a live path or a schema that is not 013."""
    check_path(db, scratch)
    con = sqlite3.connect(db, isolation_level=None)
    try:
        con.execute("PRAGMA foreign_keys = ON")
        con.execute("PRAGMA secure_delete = ON")   # dropped text doesn't linger in free pages of a shipped copy
        check_schema(con)
        report = PruneReport()
        con.execute("BEGIN IMMEDIATE")
        try:
            evidence = {}
            for fid, frag in con.execute("SELECT f.Id, s.FragmentId FROM Facts f LEFT JOIN FactSources s ON s.FactId = f.Id"):
                evidence.setdefault(fid, set())
                if frag is not None:
                    evidence[fid].add(frag)
            report.undated_facts = sorted(f for f, ids in evidence.items() if not ids)
            gone = sorted(f for f, ids in evidence.items() if ids and drop(frozenset(ids)))
            report.facts = gone
            had_facts = {r[0] for r in con.execute("SELECT SubjectId FROM Facts UNION SELECT ObjectId FROM Facts WHERE ObjectId IS NOT NULL")}
            if gone:
                con.execute("CREATE TEMP TABLE prune_gone (Id INTEGER PRIMARY KEY)")
                con.executemany("INSERT INTO prune_gone VALUES (?)", [(f,) for f in gone])
                g = "(SELECT Id FROM prune_gone)"
                report.unsuperseded = [r[0] for r in con.execute(
                    f"SELECT Id FROM Facts WHERE Id NOT IN {g} AND (SupersededById IN {g} OR Supersedes IN {g}) ORDER BY Id")]
                con.execute(f"UPDATE Facts SET SupersededById = NULL WHERE Id NOT IN {g} AND SupersededById IN {g}")
                con.execute(f"UPDATE Facts SET Supersedes = NULL WHERE Id NOT IN {g} AND Supersedes IN {g}")
                con.execute(f"UPDATE Facts SET SupersededById = NULL, Supersedes = NULL WHERE Id IN {g}")
                con.execute(f"DELETE FROM FactFlags WHERE FactId IN {g}")
                # Pending first: FactSources_keep_the_last guards a standing fact's last source, rightly, on the live store.
                con.execute(f"UPDATE Facts SET Status = 'pending' WHERE Id IN {g}")
                con.execute(f"DELETE FROM FactSources WHERE FactId IN {g}")
                con.execute(f"DELETE FROM Facts WHERE Id IN {g}")
                con.execute("DROP TABLE prune_gone")
            still = {r[0] for r in con.execute("SELECT SubjectId FROM Facts UNION SELECT ObjectId FROM Facts WHERE ObjectId IS NOT NULL")}
            concepts = {r[0] for r in con.execute("SELECT Id FROM Concepts")}
            report.concepts = sorted((had_facts - still) & concepts)
            report.undated_concepts = sorted(concepts - had_facts)
            for cid in report.concepts:
                report.aliases += [(cid, a) for (a,) in con.execute("SELECT Alias FROM ConceptAliases WHERE ConceptId = ? ORDER BY Alias", (cid,))]
                con.execute("DELETE FROM ConceptAliases WHERE ConceptId = ?", (cid,))
                con.execute("DELETE FROM Concepts WHERE Id = ?", (cid,))
            report.kept_aliases = [(c, a) for c, a in con.execute("SELECT ConceptId, Alias FROM ConceptAliases ORDER BY ConceptId, Alias")]
            bad = [r for r in con.execute("PRAGMA foreign_key_check") if r[0] in SCHEMA_013]
            if bad:
                raise PruneRefused(f"prune would break foreign keys in the concept tables: {bad[:5]}; rolled back")
            con.execute("COMMIT")
        except BaseException:
            con.execute("ROLLBACK")
            raise
        return report
    finally:
        con.close()
