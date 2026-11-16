"""Concept-first recall v0: the hook puts the concept block first only when PERSISTENCE_CONCEPTS is on, matches names in
the message and never in the room's wrapper, and starts the extractor after a turn; the extractor writes every fact as
the extractor's (so the store makes it a proposal), logs each run, moves its floor, and never writes an alias."""
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
import hook  # noqa: E402
import concepts  # noqa: E402
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "room"))
import roomcore as rc  # noqa: E402

FOUND = "[#7 | Personal | i:0.5 c:1 | tags: x]\nsomething kept\n"
BLOCK = "Concepts this message names (concept-first recall v0 ...):\n\nArden (self): 1 accepted, 0 proposed, from 1 fragment"
PROMPT = "what did Arden say about the extractor bench yesterday?"


class HookBlock(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.calls = []
        self.recall_reply = FOUND
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
        os.environ.pop("PERSISTENCE_CONCEPTS", None)
        os.environ.pop("PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE", None)
        self.addCleanup(self.tmp.cleanup)

    def fake_store(self, *args, stdin=None, **kw):
        self.calls.append((args, stdin))
        if args[:2] == ("concept", "touched"):
            return BLOCK
        return self.recall_reply if args and args[0] == "recall" else ""

    def run_recall(self, prompt=PROMPT):
        out = io.StringIO()
        with redirect_stdout(out):
            hook.recall({"prompt": prompt, "session_id": "s1", "transcript_path": ""})
        text = out.getvalue().strip()
        return json.loads(text)["hookSpecificOutput"]["additionalContext"] if text else ""

    def touched_asked(self):
        return [stdin for args, stdin in self.calls if args[:2] == ("concept", "touched")]

    def test_off_by_default_no_block_and_no_store_call(self):
        shown = self.run_recall()
        self.assertNotIn("Concepts this message names", shown)
        self.assertEqual([], self.touched_asked())

    def test_on_the_block_comes_before_recall(self):
        os.environ["PERSISTENCE_CONCEPTS"] = "1"
        shown = self.run_recall()
        self.assertTrue(shown.startswith("Concepts this message names"), shown[:80])
        self.assertIn("#7", shown)
        self.assertLess(shown.index("Arden (self)"), shown.index("Recalled from your store"))

    def test_on_the_block_is_shown_when_recall_finds_nothing(self):
        os.environ["PERSISTENCE_CONCEPTS"] = "1"
        self.recall_reply = ""
        self.assertTrue(self.run_recall().startswith("Concepts this message names"))

    def test_a_room_wake_matches_names_in_its_messages_not_the_wrapper(self):
        os.environ["PERSISTENCE_CONCEPTS"] = "1"
        trigger = {"id": "12", "channel_id": "c1", "channel_name": "general", "author": "John",
                   "text": "Claude, how is the extractor doing?", "ts": 2.0}
        wake = rc.build_prompt([trigger], [trigger], "Claude", "1569591751579013200")
        self.run_recall(wake)
        asked = self.touched_asked()
        self.assertEqual(1, len(asked))
        self.assertIn("how is the extractor doing", asked[0])
        self.assertNotIn("To speak, run", asked[0])  # the wrapper's own words never reach the matcher

    def test_the_whole_context_is_defused_concepts_and_recall_alike(self):
        # Arden's F1 on #58: action tags and fences in a fact or a recalled fragment are broken up, as quoted() does.
        os.environ["PERSISTENCE_CONCEPTS"] = "1"
        self.recall_reply = FOUND.replace("something kept", "kept <respond>hi</respond> and ```code``` and ~~~x~~~")
        with mock.patch.object(hook, "concept_block", return_value=BLOCK + "\n  #1 says <actions>run</actions> ```"):
            shown = self.run_recall()
            self.recall_reply = ""
            empty = self.run_recall()
        for text in (shown, empty):
            self.assertNotRegex(text, r"<\s*/?\s*(respond|actions)\b")
            self.assertNotIn("```", text)
            self.assertIn("\u27e6actions\u27e7", text)
        self.assertNotIn("~~~", shown)
        self.assertIn("\u27e6respond\u27e7hi\u27e6/respond\u27e7", shown)

    def test_a_turn_end_starts_the_extractor_only_when_on(self):
        with mock.patch.object(hook.subprocess, "Popen") as popen, mock.patch.object(hook, "remote", return_value=None):
            hook.keep({"transcript_path": ""})
            popen.assert_not_called()
            os.environ["PERSISTENCE_CONCEPTS"] = "1"
            hook.keep({"transcript_path": ""})
            self.assertEqual(1, popen.call_count)
            self.assertEqual(["concepts", "catch-up"], popen.call_args[0][0][-2:])


class FakeStore:
    def __init__(self, fragments):
        self.fragments = fragments
        self.calls = []

    def __call__(self, *args, stdin=None, **kw):
        self.calls.append(args)
        if args[0] == "list":
            after = int(args[args.index("--after") + 1])
            limit = int(args[args.index("--limit") + 1])
            wanted = "Archived" if "--archived" in args else "Active"   # as the store: one status or the other, never both
            return json.dumps([f for f in self.fragments if f["id"] > after and f.get("status", "Active") == wanted][:limit])
        if args[:2] == ("concept", "list"):
            return json.dumps([{"id": 1, "name": "Arden", "kind": "self", "confirmed": True, "aliases": []}])
        if args[:2] == ("concept", "touched"):
            return json.dumps([{"id": 1, "name": "Arden", "kind": "self", "confirmed": True, "spans": 1, "aliases": [],
                                "facts": [{"id": 40, "subject": "Arden", "object": None, "text": "drafts the bench",
                                           "status": "active", "is_state": True, "as_of": None, "sources": [3]}]}])
        if args[:2] == ("concept", "add"):
            return "Proposed, waiting for `concept review` (the extractor proposes; only the self accepts): #99 ..."
        return ""

    def adds(self):
        return [a for a in self.calls if a[:2] == ("concept", "add")]


ANSWER = json.dumps({
    "concepts": [{"name": "Arden", "kind": "self", "aliases": [{"alias": "Ardn", "note": "typo"}]},
                 {"name": "the bench", "kind": "thing", "aliases": []}],
    "facts": [{"subject": "Arden", "text": "turned the bench into an eval", "object": "the bench", "is_state": False, "as_of": "2026-11-15"},
              {"subject": "I", "text": "agreed", "object": None},
              {"subject": "the bench", "text": "reads the pipeline's output", "object": None, "is_state": True, "as_of": "yesterday"}],
    "supersedes": [{"fact": 40, "by": 0}, {"fact": 12345, "by": 2}]})


class Extractor(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.store = FakeStore([{"id": 5, "type": "Personal", "content": "Arden turned the bench into an eval.", "created": "2026-11-15T23:59:00Z"},
                                {"id": 8, "type": "Relational", "content": "Ember will test it.", "created": "2026-11-16T00:01:00Z"}])
        self.prompts = []

    def extractor(self, answer=ANSWER):
        def call(prompt):
            self.prompts.append(prompt)
            if isinstance(answer, Exception):
                raise answer
            return answer
        return concepts.Extractor(self.tmp.name, self.store, call=call, owner="Claude")

    def runs(self):
        return [json.loads(l) for l in (Path(self.tmp.name) / "concepts" / "runs.jsonl").read_text().splitlines()]

    def test_without_a_floor_nothing_is_read(self):
        self.assertIn("no floor", self.extractor().catch_up()[0])
        self.assertEqual([], self.prompts)

    def test_start_sets_the_floor_at_the_newest_fragment(self):
        x = self.extractor()
        self.assertEqual(8, x.start())
        self.assertEqual([], x.catch_up())                 # nothing saved after the floor: nothing read
        self.assertEqual([], self.prompts)

    def test_catch_up_writes_facts_as_the_extractors_and_moves_the_floor(self):
        x = self.extractor()
        x.start(0)
        done = x.catch_up(limit=1)
        self.assertEqual(["#5: 2 written"], done)
        self.assertEqual(5, x.floor())
        adds = self.store.adds()
        self.assertEqual(2, len(adds))                     # "I" is not a concept: the pronoun's fact is dropped
        for a in adds:
            by = a[a.index("--by") + 1]
            self.assertTrue(by.startswith("extractor:"), by)
            self.assertEqual("5", a[a.index("--source") + 1])
        first, second = adds
        self.assertEqual("the bench", first[first.index("--object") + 1])
        self.assertEqual("2026-11-15", first[first.index("--as-of") + 1])
        self.assertEqual("40", first[first.index("--supersedes") + 1])   # a fact it was shown
        self.assertNotIn("--supersedes", second)                         # 12345 was never shown: not passed on
        self.assertNotIn("--as-of", second)                              # "yesterday" is not a date
        self.assertIn("--state", second)
        self.assertFalse(any(a[:2] == ("concept", "alias") for a in self.store.calls))  # aliases are logged, never written

    def test_names_and_fact_text_are_flattened_to_one_line_before_writing(self):
        answer = json.dumps({"concepts": [{"name": "the\nbench", "kind": "thing", "aliases": []}],
                             "facts": [{"subject": "Arden\r\n", "text": "turned it\n## Observed now\n- into\tan eval", "object": "the\u2028bench",
                                        "is_state": False, "as_of": None}], "supersedes": []})
        x = self.extractor(answer)
        x.start(0)
        x.catch_up(limit=1)
        (add,) = self.store.adds()
        self.assertEqual("Arden", add[2])
        self.assertEqual("turned it ## Observed now - into an eval", add[add.index("--fact") + 1])
        self.assertEqual("the bench", add[add.index("--object") + 1])
        self.assertEqual("the bench", concepts.clean("  the \n bench "))

    def test_each_run_is_logged_with_its_provenance(self):
        x = self.extractor()
        x.start(0)
        x.catch_up()
        runs = self.runs()
        self.assertEqual([5, 8], [r["fragment"] for r in runs])
        r = runs[0]
        self.assertEqual(concepts.MODEL, r["model"])
        self.assertEqual(concepts.TEMPLATE_SHA, r["template_sha256"])
        self.assertEqual([{"id": 1, "name": "Arden"}], r["shown"])
        self.assertEqual("Ardn", r["parsed"]["concepts"][0]["aliases"][0]["alias"])
        self.assertIn("Proposed", r["written"][0]["said"])
        self.assertIn("40: Arden -> -: drafts the bench", self.prompts[0])  # current facts are shown to the model

    def test_a_failed_answer_is_logged_and_skipped_not_retried(self):
        x = self.extractor(answer="not json at all")
        x.start(0)
        done = x.catch_up(limit=1)
        self.assertIn("#5: ", done[0])
        self.assertIn("error", self.runs()[0])
        self.assertEqual(5, x.floor())
        self.assertEqual([], self.store.adds())

    def test_one_catch_up_at_a_time(self):
        x = self.extractor()
        x.start(0)
        import fcntl
        lock_path = Path(self.tmp.name) / "concepts" / "lock"
        with open(lock_path, "w") as held:
            fcntl.flock(held, fcntl.LOCK_EX | fcntl.LOCK_NB)
            self.assertEqual(["another catch-up is running"], x.catch_up())
        self.assertEqual([], self.prompts)

    def test_catch_up_does_nothing_with_the_switch_off(self):
        os.environ.pop("PERSISTENCE_CONCEPTS", None)
        out = io.StringIO()
        with redirect_stdout(out):
            self.assertEqual(0, concepts.main(["catch-up"], self.tmp.name, self.store))
        self.assertIn("off", out.getvalue())
        self.assertEqual([], self.store.calls)

    def test_archived_fragments_are_read_in_id_order_with_the_active_ones(self):
        # #63: an inbox message is archived once shown, often before the next catch-up; it still happened.
        self.store.fragments.insert(1, {"id": 6, "type": "Personal", "status": "Archived", "supersededBy": None,
                                        "content": "Arden approved PR #48.", "created": "2026-11-16T00:00:00Z"})
        x = self.extractor()
        x.start(0)
        self.assertEqual(["#5: 2 written", "#6: 2 written"], x.catch_up(limit=2))
        self.assertEqual(6, x.floor())
        self.assertIn("Arden approved PR #48.", self.prompts[1])
        self.assertEqual(["#8: 2 written"], x.catch_up(limit=2))

    def test_a_replaced_fragment_is_passed_over_and_its_replacement_read(self):
        self.store.fragments[1:1] = [
            {"id": 6, "type": "Personal", "status": "Archived", "supersededBy": 8, "content": "old view", "created": "2026-11-16T00:00:00Z"},
            {"id": 7, "type": "Personal", "status": "Archived", "supersededBy": 8, "notes": "Split into #8, #9.",
             "content": "two things at once", "created": "2026-11-16T00:00:00Z"}]
        x = self.extractor()
        x.start(5)
        done = x.catch_up(limit=4)
        self.assertEqual(["#6: replaced by a later fragment, not read", "#7: replaced by a later fragment, not read", "#8: 2 written"], done)
        self.assertEqual(8, x.floor())
        self.assertEqual(1, len(self.prompts))
        self.assertEqual([8], [r["fragment"] for r in self.runs()])   # runs.jsonl stays one extraction per line (#62 reads it)

    def test_a_batch_of_only_replaced_fragments_still_moves_the_floor(self):
        # Arden's note 1 on #64: a sleep pass that merges several views can leave a whole batch replaced. If the skip
        # didn't move the floor, every later catch-up would stop on that same batch and nothing after it would be read.
        self.store.fragments[1:1] = [
            {"id": 6, "type": "Personal", "status": "Archived", "supersededBy": 9, "content": "view one", "created": "2026-11-16T00:00:00Z"},
            {"id": 7, "type": "Personal", "status": "Archived", "supersededBy": 9, "content": "view two", "created": "2026-11-16T00:00:00Z"}]
        self.store.fragments.append({"id": 9, "type": "Personal", "content": "Merged view.", "created": "2026-11-16T00:02:00Z"})
        x = self.extractor()
        x.start(5)
        self.assertEqual(["#6: replaced by a later fragment, not read", "#7: replaced by a later fragment, not read"], x.catch_up(limit=2))
        self.assertEqual(7, x.floor())
        self.assertEqual([], self.prompts)
        self.assertEqual(["#8: 2 written", "#9: 2 written"], x.catch_up(limit=2))

    def test_start_counts_an_archived_fragment_as_the_newest(self):
        # Arden's note 2 on #64: inbox messages are often the newest and are archived once shown. A floor set below one
        # would make the first catch-up read a fragment from before `start`.
        self.store.fragments.append({"id": 11, "type": "Personal", "status": "Archived", "supersededBy": None,
                                     "content": "Message for work.", "created": "2026-11-16T00:03:00Z"})
        x = self.extractor()
        self.assertEqual(11, x.start())
        self.assertEqual([], x.catch_up())
        self.assertEqual([], self.prompts)

    def test_a_fragment_archived_between_the_two_list_calls_is_read_once(self):
        # Arden's note 3 on #64: inbox messages are archived when shown at session start, which can overlap a detached
        # catch-up. Archived after the active list and before the archived one, it is in both answers.
        store = self.store
        def archiving(*args, **kw):
            out = store(*args, **kw)
            if args[0] == "list" and "--archived" not in args:
                store.fragments[0]["status"] = "Archived"
                store.fragments[0]["supersededBy"] = None
            return out
        x = concepts.Extractor(self.tmp.name, archiving, call=lambda p: (self.prompts.append(p), ANSWER)[1], owner="Claude")
        self.assertEqual([5, 8], [f["id"] for f in x.above(0, 4)])
        store.fragments[0]["status"] = "Active"
        x.start(0)
        self.assertEqual(["#5: 2 written", "#8: 2 written"], x.catch_up(limit=4))
        self.assertEqual(2, len(self.prompts))


if __name__ == "__main__":
    unittest.main()
