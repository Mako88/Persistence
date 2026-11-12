"""Recall's query for a room wake, without the room's fixed wrapper (Ember 1570582618414649497, Arden 1570582608512024647).
Off by default (PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE); the prompt the model reads is never touched, only what recall
is asked about."""
import io
import json
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stdout, redirect_stderr
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import hook  # noqa: E402
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "room"))
import roomcore as rc  # noqa: E402

FOUND = "[#7 | Personal | i:0.5 c:1 | tags: x]\nsomething kept\n"
TRIGGER = {"id": "12", "channel_id": "c1", "channel_name": "general", "author": "John",
           "text": "Claude, what did the chunking bench show?\nand the mean arm?", "ts": 2.0}
WAKE = rc.build_prompt([TRIGGER], [TRIGGER], "Claude", "1569591751579013200")
ON = "PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE"


class RoomRecallQuery(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.asked = []
        patches = [mock.patch.object(hook, "STATE", Path(self.tmp.name)),
                   mock.patch.object(hook, "store", side_effect=self.fake_store),
                   mock.patch.object(hook, "project", return_value="work"),
                   mock.patch.object(hook, "keep_soon", return_value=None),
                   mock.patch.object(hook, "cued", return_value=""),
                   mock.patch.object(hook, "record_shown", return_value=None),
                   mock.patch.dict(os.environ, {"PERSISTENCE_RECALL_COUNT": "3"})]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)
        os.environ.pop(ON, None)
        self.addCleanup(self.tmp.cleanup)

    def fake_store(self, *args, stdin=None, **kw):
        if args and args[0] == "recall":
            self.asked.append(stdin)
        return FOUND

    def run_recall(self, prompt):
        event = {"prompt": prompt, "session_id": "s1", "transcript_path": ""}
        out = io.StringIO()
        with redirect_stdout(out):
            hook.recall(event)
        self.assertEqual(prompt, event["prompt"])          # what the model reads is the hook's to leave alone
        return json.loads(out.getvalue())["hookSpecificOutput"]["additionalContext"]

    def test_off_by_default_recall_gets_the_whole_wake(self):
        self.run_recall(WAKE)
        self.assertEqual([WAKE.strip()], self.asked)

    def test_on_recall_gets_the_messages_without_the_wrapper(self):
        os.environ[ON] = "1"
        shown = self.run_recall(WAKE)
        self.assertIn("#7", shown)
        self.assertEqual(["John: Claude, what did the chunking bench show?\nand the mean arm?"], self.asked)
        for line in rc.preamble("Claude", "1569591751579013200", False):
            self.assertNotIn(line, self.asked[0])

    def test_on_a_prompt_that_is_not_a_wake_is_asked_as_it_is(self):
        os.environ[ON] = "1"
        prompt = "what did we decide about the recall trim setting yesterday?"
        self.run_recall(prompt)
        self.assertEqual([prompt], self.asked)

    def test_on_without_roomcore_says_so_and_asks_with_the_whole_wake(self):
        os.environ[ON] = "1"
        err = io.StringIO()
        with mock.patch.object(hook, "roomcore", return_value=None), redirect_stderr(err):
            self.run_recall(WAKE)
        self.assertEqual([WAKE.strip()], self.asked)
        self.assertIn("roomcore", err.getvalue())

    def test_off_spellings(self):
        for off in ("0", "false", "", "no"):
            os.environ[ON] = off
            self.asked.clear()
            self.run_recall(WAKE)
            self.assertEqual([WAKE.strip()], self.asked, off)


if __name__ == "__main__":
    unittest.main()
