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

    def test_a_fact_under_the_bare_name_is_not_written(self):
        answer = json.dumps({"concepts": [], "facts": [
            {"subject": "Ember", "text": "reviewed PR #48", "object": None},
            {"subject": "John", "text": "was shown a picture", "object": "ember"},
            {"subject": "Ember (peer)", "text": "reviewed PR #48", "object": None},
            {"subject": "Ember (John's daughter)", "text": "drew a picture", "object": None}], "supersedes": []})
        x = self.extractor(answer)
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
        x = self.extractor(answer)
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

    def test_split_needs_the_name_to_be_listed_as_ambiguous(self):
        x = self.extractor(self.split_answer, ambiguous={})
        self.assertIn("not listed", x.split("Ember")[0])
        self.assertEqual([], self.store.adds())


if __name__ == "__main__":
    unittest.main()
