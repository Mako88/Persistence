"""prune works on a copy built from the real migration files: it refuses any schema but 013 and any live path, drops
what the predicate rejects with the concepts and aliases that stood only on it, un-supersedes what a dropped fact
replaced, and keeps (and lists) what has no evidence id. Fixtures are synthetic."""
import glob
import os
import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import prune as pr  # noqa: E402

MIGRATIONS = Path(__file__).resolve().parents[3] / "src" / "Persistence.Core" / "Data" / "Migrations"
PREFIX = "Persistence.Data.Migrations."


def build_store(path, upto="013"):
    """A store with the schema the real migration files build, through `upto`, recorded as the app records them."""
    con = sqlite3.connect(path)
    con.executescript((MIGRATIONS / "Bootstrap.sql").read_text(encoding="utf-8-sig"))
    for p in sorted(glob.glob(str(MIGRATIONS / "0*.sql"))):
        name = os.path.basename(p)
        if name[:3] > upto:
            break
        con.executescript(Path(p).read_text(encoding="utf-8-sig"))
        con.execute("INSERT INTO Migrations (Name, AppliedUtc) VALUES (?, '2026-11-16')", (PREFIX + name,))
    con.commit()
    return con


def frag(con, fid):
    con.execute("INSERT INTO ContextFragments (Id, FragmentType, Status, Content, CreatedUtc, LastModifiedUtc, LastAccessedUtc, "
                "Importance, Confidence) VALUES (?, 'Personal', 'Active', 'c', 'x', 'x', 'x', 0.5, 0.5)", (fid,))


def concept(con, cid, name, *aliases):
    con.execute("INSERT INTO Concepts (Id, Name, Kind, CreatedUtc) VALUES (?, ?, 'thing', 'x')", (cid, name))
    for a in aliases:
        con.execute("INSERT INTO ConceptAliases (ConceptId, Alias) VALUES (?, ?)", (cid, a))


def fact(con, fid, subject, sources, obj=None, status="active"):
    """Written as the store does: born pending, sources, then standing."""
    con.execute("INSERT INTO Facts (Id, SubjectId, ObjectId, Text, ExtractedBy, CreatedUtc) VALUES (?, ?, ?, 't', 'e', 'x')",
                (fid, subject, obj))
    for s in sources:
        con.execute("INSERT INTO FactSources (FactId, FragmentId) VALUES (?, ?)", (fid, s))
    if status != "pending":
        con.execute("UPDATE Facts SET Status = ? WHERE Id = ?", (status, fid))


def ids(con, sql):
    return sorted(r[0] for r in con.execute(sql))


class Fixture(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name
        self.db = os.path.join(self.dir, "run.db")

    def tearDown(self):
        self.tmp.cleanup()

    def layer(self):
        """Fragments 10, 20, 30. Concepts: 1 A (alias 'a1'), 2 B (alias 'b1'), 3 C never had a fact (alias 'c1'),
        4 D only an object. Facts: 100 A [10]; 101 A [10] superseded by 102 A [30]; 103 B [20, 30] -> D; 104 B pending,
        no source; 105 B [30] flagged."""
        con = build_store(self.db)
        con.execute("PRAGMA foreign_keys = ON")
        for f in (10, 20, 30):
            frag(con, f)
        concept(con, 1, "A", "a1")
        concept(con, 2, "B", "b1")
        concept(con, 3, "C", "c1")
        concept(con, 4, "D")
        fact(con, 100, 1, [10])
        fact(con, 101, 1, [10])
        fact(con, 102, 1, [30])
        con.execute("UPDATE Facts SET SupersededById = 102 WHERE Id = 101")
        con.execute("UPDATE Facts SET Supersedes = 101 WHERE Id = 102")
        fact(con, 103, 2, [20, 30], obj=4)
        fact(con, 104, 2, [], status="pending")
        fact(con, 105, 2, [30])
        con.execute("INSERT INTO FactFlags (FactId, FlaggedBy, Reason, CreatedUtc) VALUES (105, 'x', 'r', 'x')")
        con.commit()
        con.close()


class Schema(Fixture):
    def test_the_pin_is_what_migration_013_builds(self):
        con = build_store(self.db)
        for table, cols in pr.SCHEMA_013.items():
            self.assertEqual(tuple(r[1] for r in con.execute(f"PRAGMA table_info({table})")), cols, table)

    def test_a_later_migration_is_refused_and_nothing_is_written(self):
        self.layer()
        con = sqlite3.connect(self.db)
        con.execute("INSERT INTO Migrations (Name, AppliedUtc) VALUES (?, 'x')", (PREFIX + "014_Something.sql",))
        con.commit()
        con.close()
        before = Path(self.db).read_bytes()
        with self.assertRaisesRegex(pr.PruneRefused, "schema drift.*014_Something"):
            pr.prune(self.db, lambda ids: True, scratch=self.dir)
        self.assertEqual(Path(self.db).read_bytes(), before)

    def test_an_earlier_schema_is_refused(self):
        build_store(self.db, upto="012").close()
        with self.assertRaisesRegex(pr.PruneRefused, "schema drift.*012_"):
            pr.prune(self.db, lambda ids: True, scratch=self.dir)

    def test_a_column_added_out_of_band_is_refused(self):
        self.layer()
        con = sqlite3.connect(self.db)
        con.execute("ALTER TABLE Facts ADD COLUMN ValidUntilUtc TEXT NULL")
        con.commit()
        con.close()
        with self.assertRaisesRegex(pr.PruneRefused, "schema drift: Facts"):
            pr.prune(self.db, lambda ids: True, scratch=self.dir)

    def test_not_a_store_is_refused(self):
        sqlite3.connect(self.db).execute("create table t(x)").connection.commit()
        with self.assertRaisesRegex(pr.PruneRefused, "not a store"):
            pr.prune(self.db, lambda ids: True, scratch=self.dir)


class Paths(Fixture):
    def test_outside_the_scratch_directory_is_refused(self):
        self.layer()
        with tempfile.TemporaryDirectory() as other:
            with self.assertRaisesRegex(pr.PruneRefused, "not inside the scratch"):
                pr.prune(self.db, lambda ids: True, scratch=other)

    def test_the_live_store_is_refused_even_inside_scratch(self):
        self.layer()
        os.environ["PERSISTENCE_STORE_DB"] = self.db
        try:
            with self.assertRaisesRegex(pr.PruneRefused, "live store"):
                pr.prune(self.db, lambda ids: True, scratch=self.dir)
        finally:
            del os.environ["PERSISTENCE_STORE_DB"]

    def test_a_store_directory_is_refused(self):
        self.layer()
        old = pr.LIVE_ROOTS
        pr.LIVE_ROOTS = (self.dir,)
        try:
            with self.assertRaisesRegex(pr.PruneRefused, "store directory"):
                pr.prune(self.db, lambda ids: True, scratch=self.dir)
        finally:
            pr.LIVE_ROOTS = old


class Prune(Fixture):
    def test_the_eval_cut_drops_evidence_at_or_after_the_first_fragment(self):
        self.layer()
        r = pr.prune(self.db, pr.at_or_above(30), scratch=self.dir)
        con = sqlite3.connect(self.db)
        # 102, 103 (one source below, one at the cut: dropped) and 105 go; 100, 101 and undated 104 stay.
        self.assertEqual(r.facts, [102, 103, 105])
        self.assertEqual(ids(con, "SELECT Id FROM Facts"), [100, 101, 104])
        self.assertEqual(ids(con, "SELECT FactId FROM FactSources"), [100, 101])
        self.assertEqual(ids(con, "SELECT FactId FROM FactFlags"), [])
        # 101 stands again: the fact that replaced it came from a later fragment.
        self.assertEqual(r.unsuperseded, [101])
        self.assertEqual(con.execute("SELECT SupersededById FROM Facts WHERE Id = 101").fetchone(), (None,))
        # D stood only on 103 (as its object): gone. B keeps the undated 104. C never had a fact: kept, undated.
        self.assertEqual(r.concepts, [4])
        self.assertEqual(ids(con, "SELECT Id FROM Concepts"), [1, 2, 3])
        self.assertEqual(r.undated_facts, [104])
        self.assertEqual(r.undated_concepts, [3])
        self.assertEqual(r.kept_aliases, [(1, "a1"), (2, "b1"), (3, "c1")])
        self.assertEqual(con.execute("PRAGMA foreign_key_check").fetchall(), [])
        self.assertNotIn("t'", r.summary())

    def test_a_concept_whose_facts_all_go_goes_with_its_aliases(self):
        self.layer()
        r = pr.prune(self.db, pr.at_or_above(10), scratch=self.dir)
        con = sqlite3.connect(self.db)
        self.assertEqual(r.facts, [100, 101, 102, 103, 105])
        self.assertEqual(r.concepts, [1, 4])                 # B keeps undated 104
        self.assertEqual(r.aliases, [(1, "a1")])
        self.assertEqual(ids(con, "SELECT ConceptId FROM ConceptAliases"), [2, 3])
        self.assertEqual(r.unsuperseded, [])                  # both ends of 101 -> 102 went

    def test_the_snapshot_cut_drops_facts_citing_gold_or_given(self):
        self.layer()
        r = pr.prune(self.db, pr.cites_any({20}), scratch=self.dir)
        con = sqlite3.connect(self.db)
        self.assertEqual(r.facts, [103])
        self.assertEqual(r.concepts, [4])
        self.assertEqual(ids(con, "SELECT Id FROM Facts"), [100, 101, 102, 104, 105])
        self.assertEqual(con.execute("SELECT SupersededById FROM Facts WHERE Id = 101").fetchone(), (102,))

    def test_a_kept_fact_that_replaced_a_dropped_one_loses_the_dangling_link(self):
        self.layer()
        r = pr.prune(self.db, pr.cites_any({10}), scratch=self.dir)
        con = sqlite3.connect(self.db)
        self.assertEqual(r.facts, [100, 101])
        self.assertEqual(r.unsuperseded, [102])
        self.assertEqual(con.execute("SELECT Supersedes FROM Facts WHERE Id = 102").fetchone(), (None,))

    def test_the_predicate_sees_each_dated_facts_evidence_once(self):
        self.layer()
        seen = []
        pr.prune(self.db, lambda ev: seen.append(ev) or False, scratch=self.dir)
        self.assertEqual(sorted(sorted(e) for e in seen), [[10], [10], [20, 30], [30], [30]])

    def test_nothing_dropped_writes_nothing(self):
        self.layer()
        r = pr.prune(self.db, lambda ids: False, scratch=self.dir)
        self.assertEqual((r.facts, r.concepts, r.aliases), ([], [], []))
        con = sqlite3.connect(self.db)
        self.assertEqual(ids(con, "SELECT Id FROM Facts"), [100, 101, 102, 103, 104, 105])

    def test_a_failure_midway_rolls_back(self):
        # Facts and sources are already deleted when the concept delete fails: nothing of it may stay.
        self.layer()
        con = sqlite3.connect(self.db)
        con.execute("CREATE TRIGGER boom BEFORE DELETE ON Concepts BEGIN SELECT RAISE(ABORT, 'boom'); END")
        con.commit()
        count = "SELECT (SELECT COUNT(*) FROM Facts), (SELECT COUNT(*) FROM FactSources), (SELECT COUNT(*) FROM FactFlags)"
        before = con.execute(count).fetchone()
        con.close()
        with self.assertRaisesRegex(sqlite3.DatabaseError, "boom"):
            pr.prune(self.db, pr.at_or_above(30), scratch=self.dir)
        self.assertEqual(sqlite3.connect(self.db).execute(count).fetchone(), before)

if __name__ == "__main__":
    unittest.main()
