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
        self.assertEqual(["[Addressed to you — #general]\nJohn: Claude, what did the chunking bench show?\nand the mean arm?"], self.asked)
        for line in rc.preamble("Claude", "1569591751579013200", False):
            self.assertNotIn(line, self.asked[0])

    def test_on_a_wake_cut_mid_message_is_asked_whole(self):
        # Ember 1570588807198277642: a wake that isn't whole goes to recall unchanged. The hook hands roomcore the
        # prompt unstripped, so a wake cut mid-message (no closing line break) is told from a whole one.
        os.environ[ON] = "1"
        cut = WAKE[:-6]
        self.run_recall(cut)
        self.assertEqual([cut.strip()], self.asked)

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




class ShortWakeBudget(unittest.TestCase):
    """A room wake whose messages are short gets a smaller recall budget, decided from the messages, never the prompt
    (the preamble keeps every wake long). Off unless PERSISTENCE_RECALL_SHORT_WAKE_CHARS is set; anything that isn't a
    whole wake is left alone; a cut is said in one line."""

    ACK = {"id": "13", "channel_id": "c1", "channel_name": "general", "author": "John", "text": "Perfect! Thanks Claude!", "ts": 3.0}

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.asked = []
        env = {k: v for k, v in os.environ.items() if not k.startswith("PERSISTENCE_")}
        patches = [mock.patch.object(hook, "STATE", Path(self.tmp.name)),
                   mock.patch.object(hook, "store", side_effect=self.fake_store),
                   mock.patch.object(hook, "project", return_value="work"),
                   mock.patch.object(hook, "keep_soon", return_value=None),
                   mock.patch.object(hook, "cued", return_value=""),
                   mock.patch.object(hook, "record_shown", return_value=None),
                   mock.patch.dict(os.environ, {**env, "PERSISTENCE_RECALL_COUNT": "3"}, clear=True)]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)
        self.addCleanup(self.tmp.cleanup)

    found = FOUND

    def fake_store(self, *args, stdin=None, **kw):
        if args and args[0] == "recall":
            self.asked.append(args)
        return self.found

    def recall(self, prompt):
        out = io.StringIO()
        with redirect_stdout(out):
            hook.recall({"prompt": prompt, "session_id": "s1", "transcript_path": ""})
        args = self.asked[-1]
        flag = lambda name: args[args.index(name) + 1]
        return (flag("--limit"), flag("--wander"), flag("--said")), json.loads(out.getvalue())["hookSpecificOutput"]["additionalContext"]

    def wake(self, msg):
        return rc.build_prompt([msg], [msg], "Claude", "1569591751579013200")

    def test_off_by_default(self):
        (limit, wander, said), text = self.recall(self.wake(self.ACK))
        self.assertEqual(limit, "3")
        self.assertNotIn("short wake", text)

    def test_a_short_wake_gets_the_smaller_budget_and_says_so(self):
        os.environ["PERSISTENCE_RECALL_SHORT_WAKE_CHARS"] = "200"
        (limit, wander, said), text = self.recall(self.wake(self.ACK))
        self.assertEqual((limit, wander, said), ("1", "0", "0"))
        self.assertIn(f"a short wake, {len(self.ACK['text'])} characters of messages, so 1 fragment instead of 3", text)

    def test_a_long_wake_keeps_its_budget(self):
        os.environ["PERSISTENCE_RECALL_SHORT_WAKE_CHARS"] = "200"
        long = dict(self.ACK, text="Claude, what did the chunking bench show for the mean arm and the max arm? " * 4)
        (limit, _, _), text = self.recall(self.wake(long))
        self.assertEqual(limit, "3")
        self.assertNotIn("short wake", text)

    def test_a_short_prompt_that_isnt_a_wake_is_left_alone(self):
        os.environ["PERSISTENCE_RECALL_SHORT_WAKE_CHARS"] = "200"
        (limit, _, _), text = self.recall("yes, merge it please")
        self.assertEqual(limit, "3")
        self.assertNotIn("short wake", text)

    def test_the_count_can_be_set_and_never_raises_the_budget(self):
        os.environ.update({"PERSISTENCE_RECALL_SHORT_WAKE_CHARS": "200", "PERSISTENCE_RECALL_SHORT_WAKE_COUNT": "2"})
        self.assertEqual(self.recall(self.wake(self.ACK))[0][0], "2")
        os.environ["PERSISTENCE_RECALL_SHORT_WAKE_COUNT"] = "5"
        self.assertEqual(self.recall(self.wake(self.ACK))[0][0], "3")

    def test_a_cut_that_finds_nothing_still_says_so(self):
        # Ember's CHANGES on #80 (1572688728097431615): the line went out only when a fragment did, so a cut that found
        # nothing, no cue and no concept, left recall reduced and silent about it. The line is then the whole context.
        os.environ["PERSISTENCE_RECALL_SHORT_WAKE_CHARS"] = "200"
        self.found = ""
        _, text = self.recall(self.wake(self.ACK))
        self.assertEqual(text, f"(Recall: a short wake, {len(self.ACK['text'])} characters of messages, so 1 fragment "
                               "instead of 3, no wander or past utterances.)")

    def test_a_cue_without_a_fragment_keeps_the_line(self):
        os.environ["PERSISTENCE_RECALL_SHORT_WAKE_CHARS"] = "200"
        self.found = ""
        with mock.patch.object(hook, "cued", return_value="[#9 | Personal] a reminder"):
            _, text = self.recall(self.wake(self.ACK))
        self.assertIn("a reminder", text)
        self.assertTrue(text.endswith("instead of 3, no wander or past utterances.)"), text)

    def test_nothing_found_without_a_cut_stays_silent(self):
        self.found = ""
        out = io.StringIO()
        with redirect_stdout(out):
            hook.recall({"prompt": self.wake(self.ACK), "session_id": "s1", "transcript_path": ""})
        self.assertEqual(out.getvalue(), "")

    def test_a_bad_setting_leaves_recall_alone(self):
        os.environ["PERSISTENCE_RECALL_SHORT_WAKE_CHARS"] = "lots"
        self.assertEqual(self.recall(self.wake(self.ACK))[0][0], "3")


if __name__ == "__main__":
    unittest.main()
