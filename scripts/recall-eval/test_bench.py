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


if __name__ == "__main__":
    unittest.main()
