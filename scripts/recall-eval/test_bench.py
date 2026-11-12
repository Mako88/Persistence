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
        self.assertEqual(["John: what did the chunking bench show?"],
                         self.store_turn_asks({"PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE": "1"}))

    def test_store_layer_asks_whole_when_off(self):
        self.assertEqual([self.wake], self.store_turn_asks({}))

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


if __name__ == "__main__":
    unittest.main()
