"""Recall on exact words alone must never be silent (Ember's requirement for the query trim, 2026-11-11). The store
prints the notice at the head of `recall`; the hook dropped the whole output when nothing matched, so the notice was
invisible exactly when recall came back empty (Arden, msg 1570105836691267786)."""
import io
import json
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import hook

NOTICE = "(Recall ran on exact words alone, without meaning: the embedder refused the message (HTTP 413).)"
FOUND = "[#7 | Personal | i:0.5 c:1 | tags: x]\nsomething kept\n"
PROMPT = "what did we decide about the recall trim setting yesterday?"


class RecallFallback(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.reply = ""
        patches = [mock.patch.object(hook, "STATE", Path(self.tmp.name)),
                   mock.patch.object(hook, "store", side_effect=lambda *a, **k: self.reply),
                   mock.patch.object(hook, "project", return_value="work"),
                   mock.patch.object(hook, "keep_soon", return_value=None),
                   mock.patch.object(hook, "cued", return_value=""),
                   mock.patch.object(hook, "record_shown", return_value=None),
                   mock.patch.dict(os.environ, {"PERSISTENCE_RECALL_COUNT": "3"})]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)
        self.addCleanup(self.tmp.cleanup)

    def run_recall(self):
        out = io.StringIO()
        with redirect_stdout(out):
            hook.recall({"prompt": PROMPT, "session_id": "s1", "transcript_path": ""})
        text = out.getvalue().strip()
        return json.loads(text)["hookSpecificOutput"]["additionalContext"] if text else ""

    def test_empty_recall_on_words_alone_shows_the_notice(self):
        self.reply = NOTICE + "\n\n"
        shown = self.run_recall()
        self.assertIn("exact words alone", shown)
        self.assertIn("nothing", shown.lower())

    def test_empty_recall_with_meaning_prints_nothing(self):
        self.reply = ""
        self.assertEqual("", self.run_recall())

    def test_found_recall_on_words_alone_keeps_the_notice(self):
        self.reply = NOTICE + "\n\n" + FOUND
        shown = self.run_recall()
        self.assertIn("exact words alone", shown)
        self.assertIn("#7", shown)

    def test_a_reason_over_several_lines_is_still_shown(self):
        self.reply = "(Recall ran on exact words alone, without meaning: semantic recall failed (line one\nline two).)\n\n"
        shown = self.run_recall()
        self.assertIn("line two", shown)

    def test_already_in_window_on_words_alone_says_both(self):
        replies = iter([NOTICE + "\n\n", FOUND])  # first ask excludes #7; the second, without exclusion, finds it
        hook.store.side_effect = lambda *a, **k: next(replies)
        hook.seen({"session_id": "s1"}, add=["7"])
        shown = self.run_recall()
        self.assertIn("already in this window", shown)
        self.assertIn("exact words alone", shown)

    def test_cue_matched_and_recall_empty_on_words_alone_shows_the_notice(self):
        # The cue branch skipped the empty-result path, so a matched cue hid the notice (Arden, hook.py:1342 on 9a1e8e7).
        hook.cued.return_value = "[#9 | Personal | i:0.5 c:1 | tags: cue]\na reminder\n"
        self.reply = NOTICE + "\n\n"
        shown = self.run_recall()
        self.assertIn("a reminder", shown)
        self.assertIn("exact words alone", shown)


if __name__ == "__main__":
    unittest.main()
