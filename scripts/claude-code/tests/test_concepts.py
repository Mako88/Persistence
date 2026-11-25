"""Concept-first recall v0: the hook puts the concept block first only when PERSISTENCE_CONCEPTS is on, matches names in
the message and never in the room's wrapper, and starts the extractor after a turn; the extractor writes every fact as
the extractor's (so the store makes it a proposal), logs each run, moves its floor, and never writes an alias."""
import io
import json
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
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
                   mock.patch.object(hook, "call_store", side_effect=self.fake_call_store),
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

    def fake_call_store(self, *args, stdin=None, **kw):
        if "--split" in args:
            self.calls.append((args, stdin))
            if self.store_split is None:
                return 1, "", "--split is not an option of any command."
            return 0, self.store_split, ""
        if "--ambiguous" in args and self.store_knows_ambiguous is not True:
            self.calls.append((args, stdin))
            return (1, "", "--ambiguous is not an option of any command.") if self.store_knows_ambiguous is False else (None, "", "")
        return 0, self.fake_store(*args, stdin=stdin), ""

    store_knows_ambiguous = True
    store_split = None  # a store older than --split, unless a test says what a newer one answers

    def touched_whole(self):
        return [a for a, _ in self.calls if a[:2] == ("concept", "touched") and "--split" not in a]

    def run_recall(self, prompt=PROMPT):
        out = io.StringIO()
        with redirect_stdout(out):
            hook.recall({"prompt": prompt, "session_id": "s1", "transcript_path": ""})
        text = out.getvalue().strip()
        return json.loads(text)["hookSpecificOutput"]["additionalContext"] if text else ""

    def touched_asked(self):
        return [stdin for args, stdin in self.calls if args[:2] == ("concept", "touched") and "--split" not in args]

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
        with mock.patch.object(hook, "concept_block", return_value=(BLOCK + "\n  #1 says <actions>run</actions> ```", "Proposed: <respond>x</respond>")):
            shown = self.run_recall()
            self.recall_reply = ""
            empty = self.run_recall()
        for text in (shown, empty):
            self.assertNotRegex(text, r"<\s*/?\s*(respond|actions)\b")
            self.assertNotIn("```", text)
            self.assertIn("\u27e6actions\u27e7", text)
        self.assertNotIn("~~~", shown)
        self.assertIn("\u27e6respond\u27e7hi\u27e6/respond\u27e7", shown)

    def test_a_split_store_puts_accepted_facts_first_and_proposals_after_recall(self):
        # Room 1574775057589342241: the extractor's proposals go after ordinary recall, never ahead of it.
        os.environ["PERSISTENCE_CONCEPTS"] = "1"
        self.store_split = json.dumps({"top": BLOCK, "after": "Proposed facts closest to this message (...):\n  #9 x"})
        shown = self.run_recall()
        self.assertTrue(shown.startswith("Concepts this message names"), shown[:80])
        self.assertLess(shown.index("Arden (self)"), shown.index("Recalled from your store"))
        self.assertLess(shown.index("Recalled from your store"), shown.index("Proposed facts closest"))
        split = [a for a, _ in self.calls if "--split" in a]
        self.assertEqual(1, len(split))
        self.assertEqual([], self.touched_whole())               # one question, not the old one as well

        self.recall_reply = ""                                    # recall empty: the proposals still come, last
        shown = self.run_recall()
        self.assertTrue(shown.rstrip().endswith("#9 x"), shown)

    def test_a_split_store_with_nothing_close_shows_no_proposals(self):
        os.environ["PERSISTENCE_CONCEPTS"] = "1"
        self.store_split = json.dumps({"top": "", "after": ""})
        shown = self.run_recall()
        self.assertNotIn("Concepts this message names", shown)
        self.assertNotIn("Proposed facts", shown)
        self.assertIn("#7", shown)

    def test_a_split_answer_that_is_not_json_shows_no_concepts(self):
        os.environ["PERSISTENCE_CONCEPTS"] = "1"
        self.store_split = "Concepts this message names (an old block)"
        with redirect_stderr(io.StringIO()) as err:
            shown = self.run_recall()
        self.assertNotIn("Concepts this message names", shown)
        self.assertIn("did not answer JSON", err.getvalue())

    def test_ambiguous_names_go_to_the_store_and_an_older_store_is_asked_without_them(self):
        # #60: "Ember" in a message must reach Ember (peer) and Ember (John's daughter); the store does it with the
        # names the extractor's ambiguous.json lists. Hook and store deploy separately, so a store that doesn't know
        # --ambiguous yet (exit 1) still gets the plain question, and the block is not lost.
        os.environ["PERSISTENCE_CONCEPTS"] = "1"
        self.addCleanup(os.environ.pop, "PERSISTENCE_CONCEPTS", None)
        self.assertIn(BLOCK, self.run_recall())
        self.assertEqual([("concept", "touched", "--text", "-")], self.touched_whole())
        (Path(self.tmp.name) / "concepts").mkdir()
        (Path(self.tmp.name) / "concepts" / "ambiguous.json").write_text(json.dumps(EMBERS))
        for knows, asked, shown in ((True, 1, True), (False, 2, True), (None, 1, False)):
            with self.subTest(store_knows_ambiguous=knows):
                self.calls.clear()
                self.store_knows_ambiguous = knows
                self.assertEqual(shown, BLOCK in self.run_recall())
                touched = self.touched_whole()
                self.assertEqual(asked, len(touched))
                self.assertEqual(EMBERS, json.loads(touched[0][touched[0].index("--ambiguous") + 1]))
                if asked == 2:
                    self.assertNotIn("--ambiguous", touched[1])

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

    def test_a_fragment_restored_between_the_list_calls_is_still_read(self):
        # Arden's correction on #65 (review 5914122136): restored after the active list and before the archived one, a
        # fragment was in neither answer, and the floor moved past it for good. Active is asked again after archived.
        store = self.store
        store.fragments[0].update(status="Archived", supersededBy=None)
        actives = []
        def restoring(*args, **kw):
            out = store(*args, **kw)
            if args[0] == "list" and "--archived" not in args and not actives:
                actives.append(1)
                store.fragments[0]["status"] = "Active"     # restored after the first active list
            return out
        x = concepts.Extractor(self.tmp.name, restoring, call=lambda p: ANSWER, owner="Claude")
        self.assertEqual([5, 8], [f["id"] for f in x.above(0, 4)])
        self.assertEqual("Active", next(f for f in x.above(0, 4) if f["id"] == 5)["status"])


EMBERS = {"Ember": [
    {"name": "Ember (peer)", "kind": "self", "cues": "the AI peer self with its own store, on Codex; reviews PRs in the room"},
    {"name": "Ember (John's daughter)", "kind": "person", "cues": "John's daughter; plays Minecraft; 'Emma' is a mishearing"}]}


class EmberStore(FakeStore):
    """The live case (#60): one concept 'Ember' holding facts about both Embers."""

    MERGED = {"id": 2, "name": "Ember", "kind": "person", "confirmed": False, "spans": 1, "aliases": ["Emma"], "facts": [
        {"id": 1, "subject": "Ember", "object": None, "text": "is John's daughter", "status": "active", "is_state": False,
         "as_of": None, "sources": [7148, 14625]},
        {"id": 12, "subject": "PERSISTENCE_CONCEPTS", "object": "Ember", "text": "must not be widened to include ember",
         "status": "proposed", "is_state": True, "as_of": "2026-11-16", "sources": [19398]},
        {"id": 13, "subject": "Ember", "object": None, "text": "said something", "status": "proposed", "is_state": False,
         "as_of": None, "sources": [19400]}]}

    def __init__(self, fragments, peer_exists=False):
        super().__init__(fragments)
        self.peer_exists = peer_exists

    def __call__(self, *args, stdin=None, **kw):
        if args[:2] == ("concept", "list"):
            self.calls.append(args)
            known = [{"id": 1, "name": "Arden", "kind": "self", "confirmed": True, "aliases": []},
                     {"id": 2, "name": "Ember", "kind": "person", "confirmed": False, "aliases": ["Emma"]}]
            known += [{"id": 3, "name": f"Other {i}", "kind": "thing", "confirmed": False, "aliases": []} for i in range(40)]
            if self.peer_exists:
                known.append({"id": 90, "name": "Ember (peer)", "kind": "self", "confirmed": False, "aliases": []})
            return json.dumps(known)
        if args[:2] == ("concept", "touched"):
            self.calls.append(args + (stdin,))
            return json.dumps([self.MERGED] if "Ember" in (stdin or "") or "Emma" in (stdin or "") else [])
        if args[0] == "show":
            self.calls.append(args)
            return f"[#{args[1]} | Relational]\nfragment {args[1]}: " + {"7148": "Ember is John's daughter.", "14625": "his daughter is Ember",
                                                                    "19398": "do not widen to arden or ember", "19400": "Ember said a thing"}.get(args[1], "")
        return super().__call__(*args, stdin=stdin, **kw)


class Ambiguous(unittest.TestCase):
    """#60's Ember split: one name, two concepts. The extractor is told so, never files under the bare name, and an
    existing merged concept is split fact by fact, each move a proposal citing the same evidence."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.store = EmberStore([{"id": 5, "type": "Personal", "content": "Ember reviewed PR #48; John's daughter Ember drew a picture.",
                                  "created": "2026-11-16T00:00:00Z"},
                                 {"id": 8, "type": "Personal", "content": "Remember the bench.", "created": "2026-11-16T00:01:00Z"}])
        self.prompts = []

    def extractor(self, answer, ambiguous=EMBERS):
        def call(prompt):
            self.prompts.append(prompt)
            return answer(prompt) if callable(answer) else answer
        return concepts.Extractor(self.tmp.name, self.store, call=call, owner="Claude", ambiguous=ambiguous)

    def test_the_model_is_told_the_name_is_ambiguous_with_both_referents_and_their_cues(self):
        x = self.extractor(json.dumps({"concepts": [], "facts": [], "supersedes": []}))
        x.start(0)
        x.catch_up(limit=2)
        first, second = self.prompts
        self.assertIn('"Ember" names more than one concept', first)
        self.assertIn("Ember (peer) | self | the AI peer self", first)
        self.assertIn("Ember (John's daughter) | person | John's daughter", first)
        self.assertNotIn("\nEmber | person", first)               # the merged concept is not offered for reuse
        self.assertNotIn("1: Ember -> -: is John's daughter", first)   # nor its facts, which the split moves
        self.assertNotIn('"Ember" names more than one concept', second)   # "Remember" is not the name

    def test_a_referent_that_exists_is_shown_even_when_the_fragment_does_not_name_it_exactly(self):
        self.store.peer_exists = True
        x = self.extractor(json.dumps({"concepts": [], "facts": [], "supersedes": []}))
        x.start(0)
        x.catch_up(limit=1)
        self.assertIn("\nEmber (peer) | self? | ", self.prompts[0])
        self.assertEqual("Ember (peer)", next(s["name"] for s in json.loads(
            (Path(self.tmp.name) / "concepts" / "runs.jsonl").read_text().splitlines()[0])["shown"] if s["id"] == 90))

    @staticmethod
    def confirming(answer, readings=None, whole=("both",)):
        """`answer` to the extractor's prompt; to the whole-fragment reading, the `whole` answers in turn ("both" by
        default, so each fact is asked); to a fact's check, the referent `readings` gives for the fact's text (a
        picture is the daughter's, a review the peer's, by default)."""
        readings = readings or {"picture": "Ember (John's daughter)", "PR #48": "Ember (peer)"}
        whole = iter(whole * 50)
        def call(prompt):
            if "CANDIDATES" not in prompt:
                return answer
            if "THE EXTRACTOR'S FACT" not in prompt:
                return json.dumps({"referent": next(whole), "why": "the fragment says"})
            fact = prompt.split("THE EXTRACTOR'S FACT: ")[1].split("\n")[0]
            return json.dumps({"referent": next((r for k, r in readings.items() if k in fact), "unsure"), "why": "the fragment says"})
        return call

    def test_a_fact_under_the_bare_name_is_not_written(self):
        answer = json.dumps({"concepts": [], "facts": [
            {"subject": "Ember", "text": "reviewed PR #48", "object": None},
            {"subject": "John", "text": "was shown a picture", "object": "ember"},
            {"subject": "Ember (peer)", "text": "reviewed PR #48", "object": None},
            {"subject": "Ember (John's daughter)", "text": "drew a picture", "object": None}], "supersedes": []})
        x = self.extractor(self.confirming(answer))
        x.start(0)
        self.assertEqual(["#5: 2 written"], x.catch_up(limit=1))
        self.assertEqual(["Ember (peer)", "Ember (John's daughter)"], [a[2] for a in self.store.adds()])
        self.assertEqual("self", self.store.adds()[0][self.store.adds()[0].index("--kind") + 1])   # the referent's kind
        rec = json.loads((Path(self.tmp.name) / "concepts" / "runs.jsonl").read_text().splitlines()[0])
        self.assertEqual(2, len(rec["skipped"]))
        self.assertIn("ambiguous", rec["skipped"][0]["why"])

    def test_without_ambiguous_names_the_prompt_has_none_and_nothing_is_skipped(self):
        answer = json.dumps({"concepts": [], "facts": [{"subject": "Ember", "text": "reviewed PR #48", "object": None}], "supersedes": []})
        x = self.extractor(answer, ambiguous={})
        x.start(0)
        self.assertEqual(["#5: 1 written"], x.catch_up(limit=1))
        self.assertIn("AMBIGUOUS NAMES", self.prompts[0])
        self.assertIn("(none)", self.prompts[0].split("AMBIGUOUS NAMES (")[1].split("KNOWN CONCEPTS")[0])

    def test_ambiguous_names_are_read_from_the_state_dir(self):
        (Path(self.tmp.name) / "concepts").mkdir()
        (Path(self.tmp.name) / "concepts" / "ambiguous.json").write_text(json.dumps(EMBERS))
        x = concepts.Extractor(self.tmp.name, self.store, call=lambda p: "{}", owner="Claude")
        self.assertEqual(["Ember (peer)", "Ember (John's daughter)"], [r["name"] for r in x.ambiguous["Ember"]])
        self.assertEqual({}, concepts.Extractor(tempfile.mkdtemp(dir=self.tmp.name), self.store, call=lambda p: "{}").ambiguous)

    def split_answer(self, prompt):
        if "FACT #1:" in prompt:
            return json.dumps({"referent": "Ember (John's daughter)", "why": "#7148 says Ember is John's daughter"})
        if "FACT #12:" in prompt:
            return json.dumps({"referent": "Ember (peer)", "why": "the switch is per self"})
        return json.dumps({"referent": "unsure", "why": "the fragment does not say which"})

    def test_split_proposes_each_fact_under_its_referent_by_its_evidence(self):
        x = self.extractor(self.split_answer)
        done = x.split("Ember")
        adds = self.store.adds()
        self.assertEqual(2, len(adds))
        daughter, peer = adds
        self.assertEqual("Ember (John's daughter)", daughter[2])
        self.assertEqual("is John's daughter", daughter[daughter.index("--fact") + 1])
        self.assertEqual("7148,14625", daughter[daughter.index("--source") + 1])       # the same evidence
        self.assertEqual("1", daughter[daughter.index("--supersedes") + 1])           # accepting it retires the old line
        self.assertEqual("person", daughter[daughter.index("--kind") + 1])
        self.assertTrue(daughter[daughter.index("--by") + 1].startswith("extractor:split-"))  # so the store proposes it
        self.assertEqual("PERSISTENCE_CONCEPTS", peer[2])                              # Ember was the object here
        self.assertEqual("Ember (peer)", peer[peer.index("--object") + 1])
        self.assertIn("--state", peer)
        self.assertEqual("2026-11-16", peer[peer.index("--as-of") + 1])
        self.assertEqual("12", peer[peer.index("--supersedes") + 1])
        self.assertNotIn("--kind", peer)
        self.assertIn("fragment 7148: Ember is John's daughter.", self.prompts[0])    # the model reads the evidence
        self.assertIn("Ember (peer) | self | the AI peer self", self.prompts[0])
        self.assertEqual(3, len(done))
        self.assertTrue(any("#13" in d and "unsure" in d for d in done), done)       # unsure stays where it was

    def test_split_again_does_not_propose_the_same_move_twice(self):
        x = self.extractor(self.split_answer)
        x.split("Ember")
        n = len(self.store.adds())
        done = x.split("Ember")
        self.assertEqual(n, len(self.store.adds()))
        self.assertTrue(any("#1" in d and "already" in d for d in done), done)
        self.assertTrue(any("#13" in d and "unsure" in d for d in done), done)       # unsure is asked again

    def test_a_fact_under_an_alias_of_the_merged_concept_is_not_written(self):
        # Arden, review 5915601823: `concept add` resolves aliases, and 'Emma' is an alias of the merged Ember, so a
        # fact filed under it landed on the merged concept. Any alias the store holds for it is as bare as the name.
        self.store.peer_exists = True
        answer = json.dumps({"concepts": [], "facts": [
            {"subject": "Emma", "text": "drew a picture", "object": None},
            {"subject": "John", "text": "was shown a picture by", "object": "EMMA"},
            {"subject": "Ember (John's daughter)", "text": "drew a picture", "object": None}], "supersedes": []})
        x = self.extractor(self.confirming(answer))
        x.start(0)
        self.assertEqual(["#5: 1 written"], x.catch_up(limit=1))
        self.assertEqual(["Ember (John's daughter)"], [a[2] for a in self.store.adds()])
        rec = json.loads((Path(self.tmp.name) / "concepts" / "runs.jsonl").read_text().splitlines()[0])
        self.assertEqual(2, len(rec["skipped"]))
        self.assertIn("an alias of 'Ember'", rec["skipped"][0]["why"])

    def test_the_bare_names_come_from_the_store_not_a_list_of_known_aliases(self):
        x = self.extractor("{}")
        self.assertEqual({"ember": "Ember", "emma": "Ember"}, x.bare())
        # only the merged concept's aliases: a referent's own alias, or another concept's, is a name to file under
        self.assertEqual({"ember": "Ember"}, x.bare([{"name": "Ember (peer)", "aliases": ["Ember"]},
                                                    {"name": "Arden", "aliases": ["Emma"]}]))
        self.assertEqual({}, self.extractor("{}", ambiguous={}).bare())

    def test_a_fragment_that_uses_only_the_alias_is_shown_the_referents(self):
        self.store.fragments[0]["content"] = "Emma drew a picture for John."
        x = self.extractor(json.dumps({"concepts": [], "facts": [], "supersedes": []}))
        x.start(0)
        x.catch_up(limit=1)
        self.assertIn('"Ember" names more than one concept', self.prompts[0])
        self.assertIn("Ember (John's daughter) | person", self.prompts[0])
        self.assertNotIn("\nEmber | person", self.prompts[0])

    def test_split_asks_three_times_and_proposes_only_when_all_agree(self):
        # Arden, review 5915601823: #3 came out peer twice, daughter twice, unsure once over five live runs. A pick
        # that changes between runs is a coin flip, not a reading; it stays put and is listed with its votes.
        votes = {1: iter(["Ember (John's daughter)"] * 3), 12: iter(["Ember (peer)", "Ember (John's daughter)", "Ember (peer)"]),
                 13: iter(["Ember (peer)", "unsure", "Ember (peer)"])}
        def answer(prompt):
            fact = int(prompt.split("FACT #")[1].split(":")[0])
            return json.dumps({"referent": next(votes[fact]), "why": "because"})
        x = self.extractor(answer)
        done = x.split("Ember")
        self.assertEqual(9, len(self.prompts))                                         # three runs per fact
        self.assertEqual(["Ember (John's daughter)"], [a[2] for a in self.store.adds()])
        self.assertIn("3/3 agree", done[0])
        self.assertEqual("#12: left on Ember: disagreed (votes: Ember (peer) 2, Ember (John's daughter) 1)", done[1])
        self.assertEqual("#13: left on Ember: disagreed (votes: Ember (peer) 2, unsure 1)", done[2])
        recs = [json.loads(l) for l in (Path(self.tmp.name) / "concepts" / "splits.jsonl").read_text().splitlines()]
        self.assertEqual(["agreed", "disagreed", "disagreed"], [r["outcome"] for r in recs])
        self.assertEqual(["Ember (peer)", "Ember (John's daughter)", "Ember (peer)"], recs[1]["votes"])
        self.assertEqual([True, False, False], [r["proposed"] for r in recs])

    def test_split_runs_is_configurable_and_a_model_error_is_a_vote_against(self):
        calls = iter([json.dumps({"referent": "Ember (John's daughter)"}), "not json"] + [json.dumps({"referent": "unsure"})] * 10)
        x = self.extractor(lambda p: next(calls))
        done = x.split("Ember", runs=2)
        self.assertEqual(6, len(self.prompts))
        self.assertEqual([], self.store.adds())
        self.assertIn("disagreed (votes: Ember (John's daughter) 1, error 1)", done[0])
        self.assertIn("unsure (votes: unsure 2)", done[1])

    def test_split_cli_takes_runs(self):
        out = io.StringIO()
        with redirect_stdout(out), mock.patch.object(concepts, "call_haiku", side_effect=self.split_answer):
            (Path(self.tmp.name) / "concepts").mkdir()
            (Path(self.tmp.name) / "concepts" / "ambiguous.json").write_text(json.dumps(EMBERS))
            self.assertEqual(0, concepts.main(["split", "Ember", "--runs", "1"], self.tmp.name, self.store))
        self.assertIn("1/1 agree", out.getvalue())
        self.assertEqual(2, concepts.main(["split", "Ember", "--runs", "0"], self.tmp.name, self.store))

    def test_a_fact_under_the_wrong_referent_is_asked_again_blind_and_not_written(self):
        # Arden's #68 after-run, #14625 rep 2: the daughter's relayed requests filed under the peer. The check is asked
        # with the bare name where the pick was, so the extractor's choice can't lead it; only the same referent writes.
        self.store.fragments[0]["content"] = ("John's daughter is Ember. Her minecraft-mods requests went through the relay: "
                                              "\"the coolest piece of persistence working I've ever seen.\"")
        answer = json.dumps({"concepts": [], "facts": [
            {"subject": "John", "text": "said Ember's relayed persistence working was the coolest", "object": "Ember (peer)"},
            {"subject": "Ember (John's daughter)", "text": "is John's daughter", "object": None},
            {"subject": "John", "text": "saw persistence working", "object": None}], "supersedes": []})
        x = self.extractor(self.confirming(answer, {"relayed": "Ember (John's daughter)", "daughter": "Ember (John's daughter)"}))
        x.start(0)
        self.assertEqual(["#5: 2 written"], x.catch_up(limit=1))
        self.assertEqual(["Ember (John's daughter)", "John"], [a[2] for a in self.store.adds()])
        checks = [p for p in self.prompts if "THE EXTRACTOR'S FACT" in p]
        self.assertEqual(6, len(checks))                                                  # two facts, three votes each
        self.assertEqual(3, sum("CANDIDATES" in p and p not in checks for p in self.prompts))   # the fragment read 3 times, once                                                # only facts that name a referent
        self.assertIn("THE EXTRACTOR'S FACT: John -> Ember: said Ember's relayed", checks[0])   # blind to the pick
        self.assertNotIn("Ember (peer)", checks[0].split("THE EXTRACTOR'S FACT: ")[1])
        self.assertIn("FRAGMENT #5:\nJohn's daughter is Ember. Her minecraft-mods requests", checks[0])   # the fragment decides
        self.assertIn('"my" (my store, my session) are Claude, never Ember', checks[0])   # the owner, not the peer
        self.assertIn("THE EXTRACTOR'S FACT: Ember -> -: is John's daughter", checks[3])
        rec = json.loads((Path(self.tmp.name) / "concepts" / "runs.jsonl").read_text().splitlines()[0])
        self.assertEqual("referent not confirmed: filed under 'Ember (peer)', asked again: Ember (John's daughter) 3", rec["skipped"][0]["why"])
        self.assertEqual([False, True], [c["confirmed"] for c in rec["referent_checks"]])

    def test_a_fragment_read_as_one_referent_settles_its_facts_without_asking_each(self):
        # The second gate run: the per-fact check, led by the extractor's wording, confirmed the peer once. The whole
        # fragment is an easier question; when all three readings name one referent, a fact under the other is dropped.
        answer = json.dumps({"concepts": [], "facts": [
            {"subject": "John", "text": "said the relay through Claude's store was the coolest", "object": "Ember (peer)"},
            {"subject": "Ember (John's daughter)", "text": "is John's daughter", "object": None}], "supersedes": []})
        for whole, written, asked in (([ "Ember (John's daughter)"] * 3, 1, 0),
                                      (["Ember (John's daughter)", "Ember (John's daughter)", "Ember (peer)"], 1, 6),
                                      (["both"] * 3, 1, 6)):
            with self.subTest(whole=whole):
                self.prompts.clear()
                self.store.calls.clear()
                (Path(self.tmp.name) / "concepts" / "runs.jsonl").unlink(missing_ok=True)
                x = self.extractor(self.confirming(answer, {"relay": "Ember (John's daughter)", "daughter": "Ember (John's daughter)"}, tuple(whole)))
                x.start(0)
                self.assertEqual([f"#5: {written} written"], x.catch_up(limit=1))
                self.assertEqual(["Ember (John's daughter)"], [a[2] for a in self.store.adds()])
                self.assertEqual(asked, sum("THE EXTRACTOR'S FACT" in p for p in self.prompts))
                rec = json.loads((Path(self.tmp.name) / "concepts" / "runs.jsonl").read_text().splitlines()[0])
                self.assertEqual(whole, rec["referent_reading"]["Ember"]["votes"])
                if asked == 0:
                    self.assertIn('but the fragment means "Ember (John\'s daughter)"', rec["skipped"][0]["why"])

    def test_a_referent_is_confirmed_only_when_every_check_names_it(self):
        answer = json.dumps({"concepts": [], "facts": [{"subject": "Ember (peer)", "text": "reviewed PR #48", "object": None}], "supersedes": []})
        for votes, written in ((["Ember (peer)"] * 3, 1), (["Ember (peer)", "unsure", "Ember (peer)"], 0),
                               (["Ember (peer)", "Ember (peer)", "not json"], 0)):
            with self.subTest(votes=votes):
                self.store.calls.clear()
                (Path(self.tmp.name) / "concepts" / "runs.jsonl").unlink(missing_ok=True)
                it = iter(votes)
                def call(prompt):
                    if "CANDIDATES" not in prompt:
                        return answer
                    if "THE EXTRACTOR'S FACT" not in prompt:
                        return json.dumps({"referent": "both"})
                    v = next(it)
                    return v if v == "not json" else json.dumps({"referent": v})
                x = concepts.Extractor(self.tmp.name, self.store, call=call, owner="Claude", ambiguous=EMBERS, confirm_runs=3)
                x.start(0)
                self.assertEqual([f"#5: {written} written"], x.catch_up(limit=1))

    def test_the_per_fact_check_asks_three_times_by_default(self):
        # Arden's rerun of #70 (review 5917252996), t3 + PR rep 14: one per-fact vote said peer ("my store's message")
        # and the daughter's 'coolest piece of persistence working' was written under the peer. A false drop costs
        # only coverage, so the per-fact path asks three times and every answer must agree.
        self.assertEqual(3, concepts.Extractor(self.tmp.name, self.store, call=lambda p: "{}", ambiguous=EMBERS).confirm_runs)
        answer = json.dumps({"concepts": [], "facts": [
            {"subject": "John", "text": "said it was the coolest piece of persistence working", "object": "Ember (peer)"}], "supersedes": []})
        votes = iter(["Ember (peer)", "Ember (peer)", "Ember (John's daughter)"])
        def call(prompt):
            self.prompts.append(prompt)
            if "CANDIDATES" not in prompt:
                return answer
            if "THE EXTRACTOR'S FACT" not in prompt:
                return json.dumps({"referent": "both"})
            return json.dumps({"referent": next(votes), "why": "routed to my store's message"})
        x = concepts.Extractor(self.tmp.name, self.store, call=call, owner="Claude", ambiguous=EMBERS)
        x.start(0)
        self.assertEqual(["#5: 0 written"], x.catch_up(limit=1))
        self.assertEqual(3, sum("THE EXTRACTOR'S FACT" in p for p in self.prompts))
        rec = json.loads((Path(self.tmp.name) / "concepts" / "runs.jsonl").read_text().splitlines()[0])
        self.assertEqual("referent not confirmed: filed under 'Ember (peer)', asked again: Ember (peer) 2, Ember (John's daughter) 1",
                         rec["skipped"][0]["why"])

    def test_a_fact_the_fragment_reading_did_not_settle_is_written_flagged_referent_unsure(self):
        # Arden's deterministic backstop: the per-fact vote can still be wrong, so a referent fact from a fragment whose
        # whole reading was not unanimous is written with a flag the store shows in recall, never looking settled.
        answer = json.dumps({"concepts": [], "facts": [
            {"subject": "John", "text": "said Ember's relay was the coolest piece of persistence working", "object": "Ember (peer)"},
            {"subject": "Ember (John's daughter)", "text": "asked for minecraft mods", "object": None}], "supersedes": []})
        for whole, flagged in ((["both", "both", "Ember (John's daughter)"], [True, True]),
                               (["Ember (John's daughter)"] * 3, [None, False])):
            with self.subTest(whole=whole):
                self.prompts.clear()
                self.store.calls.clear()
                (Path(self.tmp.name) / "concepts" / "runs.jsonl").unlink(missing_ok=True)
                x = self.extractor(self.confirming(answer, {"relay": "Ember (peer)", "minecraft": "Ember (John's daughter)"}, tuple(whole)))
                x.start(0)
                x.catch_up(limit=1)
                adds = {a[2]: a for a in self.store.adds()}
                rec = json.loads((Path(self.tmp.name) / "concepts" / "runs.jsonl").read_text().splitlines()[0])
                if flagged[0] is None:                       # a unanimous reading drops the peer fact outright
                    self.assertNotIn("John", adds)
                else:
                    peer = adds["John"]
                    self.assertIn("--flag", peer)
                    reason = peer[peer.index("--flag") + 1]
                    self.assertTrue(reason.startswith("referent unsure: "), reason)
                    self.assertIn("reading of 'Ember' was not unanimous (both, both, Ember (John's daughter))", reason)
                    self.assertIn("may also mean Ember (John's daughter)", reason)
                    self.assertEqual(reason, next(w for w in rec["written"] if w["subject"] == "John")["flag"])
                daughter = adds["Ember (John's daughter)"]
                self.assertEqual(flagged[1], "--flag" in daughter)
                if flagged[1]:
                    self.assertIn("may also mean Ember (peer)", daughter[daughter.index("--flag") + 1])

    def test_a_fragment_read_unanimously_as_both_writes_its_confirmed_facts_unflagged(self):
        # Condition 2's 'both' gate (PR #87, Arden's #933 and #2942): a fragment that names the daughter and the peer
        # is read 'both' every time, and that reading is settled, not split. Each fact's referent is then the per-fact
        # check's to decide, and three agreeing answers write it plain: flagging it anyway put 32 of 33 correct facts
        # under the flag. A reading with any vote for one referent, or unsure, stays flagged (the test above).
        answer = json.dumps({"concepts": [], "facts": [
            {"subject": "Ember (peer)", "text": "reviewed PR #48", "object": None},
            {"subject": "Ember (John's daughter)", "text": "drew a picture", "object": None}], "supersedes": []})
        for whole, flagged in ((["both"] * 3, False), (["both", "both", "unsure"], True), (["both", "both", "error"], True)):
            with self.subTest(whole=whole):
                self.prompts.clear()
                self.store.calls.clear()
                (Path(self.tmp.name) / "concepts" / "runs.jsonl").unlink(missing_ok=True)
                call = self.confirming(answer, whole=tuple(whole))
                def answering(prompt):                 # an 'error' reading is a reply that is not JSON
                    got = call(prompt)
                    return "not json" if '"referent": "error"' in got else got
                x = self.extractor(answering)
                x.start(0)
                self.assertEqual(["#5: 2 written"], x.catch_up(limit=1))
                adds = self.store.adds()
                self.assertEqual(2, len(adds))
                for a in adds:
                    self.assertEqual(flagged, "--flag" in a, a)
                rec = json.loads((Path(self.tmp.name) / "concepts" / "runs.jsonl").read_text().splitlines()[0])
                self.assertEqual([flagged] * 2, [bool(w["flag"]) for w in rec["written"]])
                self.assertEqual(2, rec["flag_rule"])

    def test_a_settled_both_reading_still_checks_every_referent_a_fact_names(self):
        # A 'both' fragment is where one fact names both (the peer built a mod for the daughter). The settled reading
        # unflags a fact only after each of its referents is confirmed: a rejected object still drops it. Found staging
        # 8a9adc1 for Ember's review: ending the loop at the first confirmed referent passed every other test.
        answer = json.dumps({"concepts": [], "facts": [
            {"subject": "Ember (peer)", "text": "reviewed PR #48 and drew a picture", "object": "Ember (John's daughter)"}], "supersedes": []})
        for object_says, written in (("Ember (John's daughter)", 1), ("Ember (peer)", 0)):
            with self.subTest(object_says=object_says):
                self.prompts.clear()
                self.store.calls.clear()
                (Path(self.tmp.name) / "concepts" / "runs.jsonl").unlink(missing_ok=True)
                def call(prompt):
                    if "CANDIDATES" not in prompt:
                        return answer
                    if "THE EXTRACTOR'S FACT" not in prompt:
                        return json.dumps({"referent": "both"})
                    asked = "Ember (peer)" if "Ember -> Ember (John's daughter)" in prompt else object_says
                    return json.dumps({"referent": asked})
                x = self.extractor(call)
                x.start(0)
                self.assertEqual([f"#5: {written} written"], x.catch_up(limit=1))
                rec = json.loads((Path(self.tmp.name) / "concepts" / "runs.jsonl").read_text().splitlines()[0])
                self.assertEqual(["Ember (peer)", "Ember (John's daughter)"], [c["referent"] for c in rec["referent_checks"]])
                self.assertEqual(6, sum("THE EXTRACTOR'S FACT" in p for p in self.prompts))
                if written:
                    self.assertNotIn("--flag", self.store.adds()[0])
                else:
                    self.assertEqual([], self.store.adds())
                    self.assertIn("filed under \"Ember (John's daughter)\", asked again: Ember (peer) 3", rec["skipped"][0]["why"])

    def test_no_referent_check_without_an_ambiguous_name_in_play(self):
        answer = json.dumps({"concepts": [], "facts": [{"subject": "Ember (peer)", "text": "reviewed PR #48", "object": None}], "supersedes": []})
        x = self.extractor(answer, ambiguous={})
        x.start(0)
        self.assertEqual(["#5: 1 written"], x.catch_up(limit=1))
        self.assertEqual(1, len(self.prompts))
        self.assertNotIn("referent_checks", json.loads((Path(self.tmp.name) / "concepts" / "runs.jsonl").read_text().splitlines()[0]))

    def test_split_a_move_the_store_refuses_is_not_recorded_as_proposed(self):
        # Arden's surviving mutation on #66 (review 5915708277): `proposed = True` whatever the store said. The CLI
        # answers "" when it refused or could not run; that move must be asked again next time, and say so now.
        refusing = self.store
        def store(*args, stdin=None, **kw):
            return "" if args[:2] == ("concept", "add") else refusing(*args, stdin=stdin, **kw)
        x = concepts.Extractor(self.tmp.name, store, call=self.split_answer, owner="Claude", ambiguous=EMBERS)
        done = x.split("Ember")
        self.assertIn("#1: Ember (John's daughter), but the store refused", done)
        recs = [json.loads(l) for l in (Path(self.tmp.name) / "concepts" / "splits.jsonl").read_text().splitlines()]
        self.assertFalse(next(r for r in recs if r["fact"] == 1)["proposed"])
        again = x.split("Ember")
        self.assertFalse(any("already" in d for d in again), again)                  # asked again, not passed over

    def test_a_fact_naming_an_alias_of_the_merged_concept_is_not_a_current_fact(self):
        # Arden's other surviving mutation on #66: the filter on `touched`. A fact on another concept whose object is
        # 'Emma' (an alias of the merged Ember) would reach CURRENT FACTS, inviting a supersede onto the merged line.
        merged = self.store.MERGED
        john = {"id": 4, "name": "John", "kind": "person", "confirmed": True, "spans": 1, "aliases": [], "facts": [
            {"id": 30, "subject": "John", "object": "Emma", "text": "relayed a request from", "status": "active", "is_state": False, "as_of": None, "sources": [9]},
            {"id": 31, "subject": "John", "object": None, "text": "dictates his messages", "status": "active", "is_state": False, "as_of": None, "sources": [9]}]}
        inner = self.store
        def store(*args, stdin=None, **kw):
            if args[:2] == ("concept", "touched"):
                return json.dumps([merged, john])
            return inner(*args, stdin=stdin, **kw)
        x = concepts.Extractor(self.tmp.name, store, call=lambda p: self.prompts.append(p) or "{}", owner="Claude", ambiguous=EMBERS)
        x.start(0)
        x.catch_up(limit=1)
        current = self.prompts[0].split("CURRENT FACTS of concepts")[1].split("FRAGMENT #")[0]
        self.assertIn("31: John -> -: dictates his messages", current)
        self.assertNotIn("30:", current)

    def test_split_needs_the_name_to_be_listed_as_ambiguous(self):
        x = self.extractor(self.split_answer, ambiguous={})
        self.assertIn("not listed", x.split("Ember")[0])
        self.assertEqual([], self.store.adds())



class JudgedFixes(unittest.TestCase):
    """The v0 fixes from the judged #62 run (Arden's score, PR 62 comment 6909448816): what a fragment proposes is
    not written as done, a one-off instruction is not a fact, a lasting relation is not dated by when it was said,
    and no fragment proposes more than MAX_FACTS facts, a superseding one first."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.store = FakeStore([{"id": 5, "type": "Personal", "content": "x", "created": "2026-11-16T00:00:00Z"}])
        self.prompts = []

    def run_answer(self, facts, supersedes=()):
        answer = json.dumps({"concepts": [], "facts": facts, "supersedes": list(supersedes)})
        x = concepts.Extractor(self.tmp.name, self.store, call=lambda p: (self.prompts.append(p), answer)[1], owner="Claude")
        rec = x.extract(self.store.fragments[0])
        return rec, self.store.adds()

    @staticmethod
    def fact(text, **kw):
        return {"subject": "Arden", "text": text, "object": None, "claim": "fact", "is_state": False, "lasting": False, "as_of": None, **kw}

    def test_a_one_off_instruction_is_logged_not_written(self):
        rec, adds = self.run_answer([self.fact("merge #48 if the head is still 7db543b", claim="instruction"), self.fact("approved #48")])
        self.assertEqual(["approved #48"], [a[a.index("--fact") + 1] for a in adds])
        self.assertEqual(["a one-off instruction, not a fact"], [s["why"] for s in rec["skipped"]])

    def test_a_proposal_stated_as_done_is_logged_not_written(self):
        rec, adds = self.run_answer([self.fact("added a review wait of two hours", claim="proposal"),
                                     self.fact("proposed a review wait of two hours", claim="proposal")])
        self.assertEqual(["proposed a review wait of two hours"], [a[a.index("--fact") + 1] for a in adds])
        self.assertEqual(["a proposal whose text states it as done"], [s["why"] for s in rec["skipped"]])

    def test_a_lasting_relation_is_not_dated_but_a_state_keeps_its_date(self):
        _, adds = self.run_answer([self.fact("is John's daughter", lasting=True, as_of="2026-11-09"),
                                   self.fact("PR #48 awaits review", is_state=True, as_of="2026-11-15")])
        self.assertNotIn("--as-of", adds[0])
        self.assertEqual("2026-11-15", adds[1][adds[1].index("--as-of") + 1])
        self.assertIn("--state", adds[1])

    def test_a_state_marked_lasting_keeps_its_date(self):
        _, adds = self.run_answer([self.fact("PR #48 awaits review", is_state=True, lasting=True, as_of="2026-11-15")])
        self.assertEqual("2026-11-15", adds[0][adds[0].index("--as-of") + 1])

    def test_written_records_the_as_of_and_supersedes_passed(self):
        rec, _ = self.run_answer([self.fact("is John's daughter", lasting=True, as_of="2026-11-09"),
                                  self.fact("approved #48", as_of="2026-11-15")], supersedes=[{"fact": 1, "by": 40}])   # swapped
        self.assertEqual([(None, None), ("2026-11-15", 40)], [(w["as_of"], w["supersedes"]) for w in rec["written"]])

    def test_at_most_max_facts_are_written_and_a_superseding_fact_is_never_the_one_cut(self):
        facts = [self.fact(f"fact {i}") for i in range(8)]
        rec, adds = self.run_answer(facts, supersedes=[{"fact": 40, "by": 7}])   # 40 is a CURRENT FACT the store showed
        self.assertEqual(concepts.MAX_FACTS, len(adds))
        texts = [a[a.index("--fact") + 1] for a in adds]
        self.assertEqual(["fact 0", "fact 1", "fact 2", "fact 3", "fact 7"], texts)   # model order kept, the supersede in
        self.assertEqual("40", adds[-1][adds[-1].index("--supersedes") + 1])
        self.assertEqual(["fact 4", "fact 5", "fact 6"], [s["text"] for s in rec["skipped"]])
        self.assertTrue(all("over the cap" in s["why"] for s in rec["skipped"]))
        self.assertEqual([0, 1, 2, 3, 7], [w["i"] for w in rec["written"]])   # each written fact names its parsed index

    def test_a_supersede_with_its_fields_swapped_is_read_the_only_way_it_makes_sense(self):
        # Seen on #62's after-run: {"fact": 0, "by": 6} where 6 was the shown stale fact and 0 the new fact's index.
        _, adds = self.run_answer([self.fact("approved #48")], supersedes=[{"fact": 0, "by": 40}])
        self.assertEqual("40", adds[0][adds[0].index("--supersedes") + 1])
        self.store.calls.clear()
        _, adds = self.run_answer([self.fact("approved #48")], supersedes=[{"fact": 0, "by": 41}])   # 41 was never shown
        self.assertNotIn("--supersedes", adds[0])

    def test_the_prompt_carries_the_judged_rules(self):
        self.run_answer([])
        p = self.prompts[0]
        self.assertIn(f"At most {concepts.MAX_FACTS} facts", p)
        self.assertIn('"instruction": a one-off order', p)
        self.assertIn("Never state it as done", p)
        self.assertIn("NOT the date someone said", p)
        self.assertIn("never drop it", p)                     # a state keeps its date (#19182's '#48 awaits review')
        self.assertIn("Check every state in CURRENT FACTS", p)   # the stale #48 miss
        self.assertIn('a "red test" is a test written to fail until the fix lands', p)
        self.assertEqual(3, concepts.TEMPLATE_VERSION)

if __name__ == "__main__":
    unittest.main()
