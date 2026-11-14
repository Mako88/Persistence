"""bench.py's path accounting (Ember's CHANGES on f922439): a degraded recall parses as its own path, and a hook
turn whose recall calls disagree is `mixed`, never whichever came first. Run: python3 -m unittest test_bench (here)."""
import importlib.util
import shutil
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude

spec = importlib.util.spec_from_file_location("bench", Path(__file__).resolve().parent / "bench.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


class PathLine(unittest.TestCase):
    def test_degraded_is_its_own_path_with_why(self):
        err = "noise\nrecall-path: semantic-degraded (past utterances: recall of past utterances by meaning failed (x))\n"
        self.assertEqual(("semantic-degraded", "past utterances: recall of past utterances by meaning failed (x)"), bench.path_of(err))

    def test_the_others_still_parse(self):
        self.assertEqual(("semantic", None), bench.path_of("recall-path: semantic\n"))
        self.assertEqual(("words-fallback", "fragments: no vector"), bench.path_of("recall-path: words-fallback (fragments: no vector)"))


class TurnPath(unittest.TestCase):
    """Through HookTurns.turn() itself: the hook's recall bent to make two store calls with the paths given."""

    def turn(self, *stderrs):
        ht = bench.HookTurns("/nonexistent-exe", "semantic", diagnostics=True)
        self.addCleanup(shutil.rmtree, ht.state, True)
        replies = iter(stderrs)
        with mock.patch.object(bench, "cli", side_effect=lambda *a, **k: (0, "", next(replies))):
            ht.h.recall = lambda event: [ht.h.call_store("recall", "--text", "-") for _ in stderrs]
            return ht.turn("s1", "a prompt", 3)[3]

    def test_calls_that_agree_give_their_path(self):
        self.assertEqual(("semantic", None), self.turn("recall-path: semantic", "recall-path: semantic"))

    def test_calls_that_disagree_are_mixed_with_each_listed(self):
        path, why = self.turn("recall-path: semantic", "recall-path: words-fallback (fragments: no vector)")
        self.assertEqual("mixed", path)
        self.assertEqual("call 1: semantic | call 2: words-fallback (fragments: no vector)", why)

    def test_degraded_then_clean_is_mixed_not_semantic(self):
        self.assertEqual("mixed", self.turn("recall-path: semantic-degraded (past utterances: down)", "recall-path: semantic")[0])

    def test_no_recall_is_none(self):
        self.assertEqual(("none", None), self.turn())


class StrippedQueries(unittest.TestCase):
    """Rerunning tail/max/mean on queries without the room's wrapper (Ember 1570582618414649497): the same switch the
    hook reads, PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE, so a run measures what a store that turns it on would get."""

    def setUp(self):
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "room"))
        import roomcore
        t = {"id": "12", "channel_id": "c1", "author": "John", "text": "what did the chunking bench show?"}
        self.wake = roomcore.build_prompt([t], [t], "Claude", "100")

    def store_turn_asks(self, env):
        seen = []

        def cli(exe, db, args, stdin=None, env=None):
            seen.append(stdin)
            return 0, "", "recall-path: semantic\n"
        with mock.patch.dict(bench.os.environ), mock.patch.object(bench, "cli", cli):
            bench.os.environ.pop("PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE", None)
            bench.os.environ.update(env)
            bench.store_turn("exe", "db", {}, self.wake, "work", 3, set(), True)
        return seen

    def test_store_layer_strips_when_on(self):
        self.assertEqual(["[Addressed to you — #channel]\nJohn: what did the chunking bench show?"],
                         self.store_turn_asks({"PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE": "1"}))

    def test_store_layer_asks_whole_when_off(self):
        self.assertEqual([self.wake], self.store_turn_asks({}))

    def resolved(self, stored):
        """A `query_from` turn whose utterance the store kept as `stored` (trimmed, as the store keeps it)."""
        with mock.patch.object(bench, "fragment", return_value=(stored, "2026-11-13 01:00:00", "s1")):
            return bench.resolve({"query_from": 7}, "snap")[0]

    def test_a_stored_wake_is_stripped_when_on(self):
        # Arden's CHANGES on PR #19 (review 5884624117): the store trims the closing line break, the parser declines a
        # wake without it, and the stripped arm silently asked the whole prompt. Red if as_prompted is removed.
        q = self.resolved(self.wake.strip())
        with mock.patch.dict(bench.os.environ, {"PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE": "1"}):
            stripped = bench.recall_query(q)
        with mock.patch.dict(bench.os.environ):
            bench.os.environ.pop("PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE", None)
            whole = bench.recall_query(q)
        self.assertNotEqual(whole, stripped)
        self.assertEqual("[Addressed to you — #channel]\nJohn: what did the chunking bench show?", stripped)

    def test_a_stored_non_wake_comes_back_as_stored(self):
        self.assertEqual("what did the chunking bench show?", self.resolved("what did the chunking bench show?"))

    def test_a_stored_cut_wake_still_fails_closed(self):
        # The line break comes back, but a wake whose Addressed section is missing is still declined: only the
        # trimming is undone, never the parser's other checks.
        cut = self.wake.strip().rsplit("\n", 1)[0]
        with mock.patch.dict(bench.os.environ, {"PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE": "1"}):
            q = self.resolved(cut)
            self.assertEqual(q, bench.recall_query(q))

    def test_the_report_counts_stripped_turns(self):
        rows = [{"case": c, "kind": "gold", "path": "semantic", "layer": "hook", "count": 3, "actual": "semantic",
                 "prompt_chars": 900, "query_chars": qc, "shown": [], "gold": [1], "gold_in_core": [],
                 "gold_absent": [], "relevant": [], "unjudged": [], "repeats": [], "heads": [], "hit": False,
                 "chars": 0} for c, qc in (("a", 120), ("b", 900))]
        import json
        import tempfile
        with tempfile.NamedTemporaryFile("w", suffix=".jsonl", delete=False) as f:
            f.write("\n".join(json.dumps(r) for r in rows))
        with mock.patch.object(bench, "heads_of", return_value=set(), create=True):
            out = bench.report([f.name])
        self.assertIn("| query stripped (query shorter than prompt) / turns | 1/2 |", out)

    def test_the_setting_is_in_the_report_settings(self):
        with mock.patch.object(bench, "sha256", return_value="0" * 64), \
                mock.patch.dict(bench.os.environ, {"PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE": "1"}):
            knobs = bench.settings_of("exe", "semantic", "hook", True, "labels")["knobs"]
        self.assertEqual(("1", "env"), knobs["PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE"])

    def test_the_chunk_settings_are_in_the_report_settings(self):
        with mock.patch.object(bench, "sha256", return_value="0" * 64), \
                mock.patch.dict(bench.os.environ, {"PERSISTENCE_EMBEDDING_QUERYCHUNKCHARS": "1000",
                                                   "PERSISTENCE_EMBEDDING_QUERYCHUNKSCORE": "mean"}):
            knobs = bench.settings_of("exe", "semantic", "hook", True, "labels")["knobs"]
        self.assertEqual(("1000", "env"), knobs["PERSISTENCE_EMBEDDING_QUERYCHUNKCHARS"])
        self.assertEqual(("mean", "env"), knobs["PERSISTENCE_EMBEDDING_QUERYCHUNKSCORE"])


class RelevanceFloor(unittest.TestCase):
    """Step B of the relevance-floor plan: the store's candidate line parses; an arm's floor is exactly what it says;
    the sweep pairs each arm with off per prompt and applies the pick and go rules."""

    LINE = ("recall-path: semantic\n"
            "recall-semantic: stored=100 mean=0.4000 sd=0.0500 top=0.8000 z=8.0000 floor=cos>=0.6500 dropped=1 "
            "emptied=no cands=12:0.8000,7:0.6000\n"
            "recall-semantic: stored=100 mean=0.4000 sd=0.0000 top=- z=- floor=z>=2.0000 dropped=0 emptied=yes cands=\n")

    def test_pool_lines_parse_to_ids_and_numbers(self):
        a, b = bench.pools_of(self.LINE, 2)
        self.assertEqual({"call": 2, "stored": 100, "mean": 0.4, "sd": 0.05, "top": 0.8, "z": 8.0,
                          "floor": "cos>=0.6500", "dropped": 1, "emptied": False, "cands": [[12, 0.8], [7, 0.6]]}, a)
        self.assertEqual((None, None, True, []), (b["top"], b["z"], b["emptied"], b["cands"]))
        self.assertEqual([], bench.pools_of("recall-path: semantic\n", 1))   # an older store: no pool, not an error

    def test_an_arm_sets_only_its_own_floor_and_never_inherits_one(self):
        with mock.patch.dict(bench.os.environ, {"PERSISTENCE_EMBEDDING_MINCOSINE": "0.9"}):
            off = bench.store_env("semantic", None)
            z = bench.store_env("semantic", bench.parse_floor("z=2.5"))
        self.assertEqual(("", ""), (off["PERSISTENCE_EMBEDDING_MINCOSINE"], off["PERSISTENCE_EMBEDDING_MINTOPZ"]))
        self.assertEqual(("", "2.5"), (z["PERSISTENCE_EMBEDDING_MINCOSINE"], z["PERSISTENCE_EMBEDDING_MINTOPZ"]))
        self.assertIsNone(bench.parse_floor("off"))
        self.assertEqual("cos=0.65", bench.floor_name(bench.parse_floor("cos=0.65")))
        with self.assertRaises(SystemExit):
            bench.parse_floor("bm25=3")

    @staticmethod
    def row(case, floor, kind, shown, chars, hit=None, actual="semantic", synthetic=False):
        return {"case": case, "floor": floor, "kind": kind, "shown": shown, "chars": chars, "hit": hit,
                "actual": actual, "synthetic": synthetic, "count": 3, "path": "semantic", "split": "tune",
                "snapshot_sha256": "a" * 64, "pool": [],
                "settings": {"exe_sha256": "e" * 16, "labels_sha256": "l" * 16}}

    def write(self, rows):
        import tempfile
        d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, d, True)
        path = Path(d) / "r.jsonl"
        path.write_text("".join(bench.json.dumps(r) + "\n" for r in rows))
        return str(path)

    def rows(self):
        r = self.row
        return [
            r("g1", "off", "gold", [1], 500, True), r("g2", "off", "gold", [2], 500, True),
            r("n1", "off", "none", [5], 1000), r("n2", "off", "none", [6], 1000),
            r("nw", "off", "none", [7], 999, actual="words-fallback"),
            # cos=0.7: loses one gold, empties both real none prompts
            r("g1", "cos=0.7", "gold", [1], 500, True), r("g2", "cos=0.7", "gold", [], 60, False),
            r("n1", "cos=0.7", "none", [], 0), r("n2", "cos=0.7", "none", [], 0),
            r("nw", "cos=0.7", "none", [7], 999, actual="words-fallback"),
            # z=3: loses both gold: not eligible however much it removes
            r("g1", "z=3", "gold", [], 0, False), r("g2", "z=3", "gold", [], 0, False),
            r("n1", "z=3", "none", [], 0), r("n2", "z=3", "none", [], 0),
            r("nw", "z=3", "none", [], 0, actual="words-fallback"),
        ]

    def test_sweep_pairs_per_prompt_leaves_out_words_turns_and_picks_within_one_lost_gold(self):
        out = bench.sweep([self.write(self.rows())])
        self.assertIn("| cos=0.7 | 1/2 | 1 / 0 | 560 | 0/2 | 0/2000 (0%) | 2 / 0 | 0/0 / 0 | 1 |", out)
        self.assertIn("| z=3 | 0/2 | 2 / 0 |", out)
        self.assertIn("**Pick (this split):** cos=0.7", out)

    def test_go_needs_at_most_one_gold_lost_and_none_chars_halved(self):
        self.assertIn("**GO.**", bench.sweep([self.write(self.rows())], go="cos=0.7"))
        self.assertIn("**NO GO.**", bench.sweep([self.write(self.rows())], go="z=3"))
        rows = [r for r in self.rows() if not (r["floor"] == "cos=0.7" and r["case"] == "n2")]
        rows.append(self.row("n2", "cos=0.7", "none", [6], 1000))     # 1000 of 2000: exactly halved counts
        self.assertIn("**GO.**", bench.sweep([self.write(rows)], go="cos=0.7"))
        rows[-1]["chars"] = 1001
        self.assertIn("**NO GO.**", bench.sweep([self.write(rows)], go="cos=0.7"))

    def test_said_before_and_ack_columns(self):
        rows = self.rows()
        for r in rows:
            if r["case"] == "n1":
                r["class"] = "ack"
            r["said_before"] = [i for i in r["shown"] if i == 5]       # #5, shown on n1 (an ack), is a past utterance
        out = bench.sweep([self.write(rows)])
        # off: 1 of the 2 items shown on real none is said before; acks 1000 of 1000, the rest 1000 of 1000
        self.assertIn("| off | 2/2 | 0 / 0 | 1000 | 2/2 | 2000/2000 (100%) | 0 / 0 | 0/0 / 0 | 1 | 1/2 | 1000/1000 (100%) (n 1) | 1000/1000 (100%) (n 1) |", out)
        self.assertIn("| 1 | 0/0 | 0/1000 (0%) (n 1) | 0/1000 (0%) (n 1) |", out)
        self.assertNotIn("no ack split", out)

    def test_no_class_or_said_before_shows_dashes_not_numbers(self):
        out = bench.sweep([self.write(self.rows())])
        self.assertIn("| 1 | - | - | - |", out)
        self.assertIn("no ack split", out)

    def test_older_rows_get_said_before_from_their_snapshot(self):
        import sqlite3
        import tempfile
        d = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, d, True)
        db = str(Path(d) / "snap.db")
        c = sqlite3.connect(db)
        c.execute("CREATE TABLE ContextFragments (Id INTEGER, FragmentType INTEGER)")
        c.executemany("INSERT INTO ContextFragments VALUES (?, ?)", [(5, 3), (6, 7)])
        c.commit()
        c.close()
        rows = self.rows()
        for r in rows:
            r["snapshot_sha256"] = bench.sha256(db)
        self.assertIn("| off | 2/2 | 0 / 0 | 1000 | 2/2 | 2000/2000 (100%) | 0 / 0 | 0/0 / 0 | 1 | 1/2 |",
                      bench.sweep([self.write(rows)], dbs=[db]))
        self.assertIn("| 1 | - |", bench.sweep([self.write(rows)]))              # no snapshot given: not a guess


if __name__ == "__main__":
    unittest.main()
