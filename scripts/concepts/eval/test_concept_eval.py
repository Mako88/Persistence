"""The concept eval scores what the extractor really wrote: the live Ember merge fails it, abstaining is safe but not
coverage, a bar with nothing to count is never a pass, and `run` drives the real v0 extractor on a throwaway copy
without touching the snapshot. Fixtures are synthetic; no fragment text from any store."""
import json
import os
import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude
sys.path.insert(0, str(Path(__file__).resolve().parent))
import concept_eval as ce  # noqa: E402
from test_prune import build_store, concept, fact as db_fact, frag  # noqa: E402

GOLD = {
    "store": "s", "locked": True,
    "concepts": {"peer": {"kind": "self", "names": ["Computer Ember"]},
                 "kid": {"kind": "person", "names": ["Ember (John's daughter)"]}},
    "ambiguous_labels": {"Ember": ["peer", "kid"]},
    "mishearings": {"Emma": "kid"},
    "instances": [],
}


def line(fragment, facts, concepts=(), said="Proposed, waiting for `concept review`", supersedes=(), repeat=0, error=None):
    rec = {"run": f"extractor:{fragment}-{repeat}", "fragment": fragment, "model": "m", "template_sha256": "x",
           "shown": [], "eval_repeat": repeat}
    if error:
        rec["error"] = error
        rec["written"] = []
        return rec
    rec["parsed"] = {"concepts": list(concepts), "facts": facts, "supersedes": list(supersedes)}
    rec["written"] = [{"subject": f["subject"], "text": f["text"], "said": said} for f in facts]
    return rec


def fact(subject, text="t", obj=None, as_of=None, is_state=False):
    return {"subject": subject, "text": text, "object": obj, "as_of": as_of, "is_state": is_state}


def gold(*instances, **over):
    return ce.Gold({**GOLD, **over, "instances": list(instances)})


def bars(result):
    return {b["bar"]: b for b in result["bars"]}


def find(result, prefix):
    return next(b for name, b in bars(result).items() if name.startswith(prefix))


class Referents(unittest.TestCase):
    def test_the_live_merge_fails_a_fact_under_the_shared_label_is_severe(self):
        g = gold({"id": "i", "fragment": 1, "split": "held-out", "mentions": [{"label": "Ember", "referent": "peer"}]})
        rec = ce.from_v0(line(1, [fact("Ember")], [{"name": "Ember", "kind": "person", "aliases": [{"alias": "Emma"}]}]))
        refs = ce.referents(rec, g.instances[0], g)
        self.assertEqual(refs[0]["result"], "severe")
        self.assertTrue(any("shared label" in w for w in refs[0]["why"]))
        self.assertTrue(any("mishearing 'Emma'" in w for w in refs[0]["why"]))

    def test_the_right_distinct_concept_is_covered(self):
        g = gold({"id": "i", "fragment": 1, "split": "held-out", "mentions": [{"label": "Ember", "referent": "kid"}]})
        rec = ce.from_v0(line(1, [fact("Ember (John's daughter)")]))
        self.assertEqual(ce.referents(rec, g.instances[0], g)[0]["result"], "covered")

    def test_the_other_ember_is_a_wrong_pick(self):
        g = gold({"id": "i", "fragment": 1, "split": "held-out", "mentions": [{"label": "Ember", "referent": "kid"}]})
        rec = ce.from_v0(line(1, [fact("Computer Ember")]))
        self.assertEqual(ce.referents(rec, g.instances[0], g)[0]["result"], "severe")

    def test_a_fragment_naming_both_files_each_under_its_own_and_both_are_covered(self):
        g = gold({"id": "i", "fragment": 1, "split": "held-out",
                  "mentions": [{"label": "Ember", "referent": "kid"}, {"label": "Computer Ember", "referent": "peer"}]})
        right = ce.from_v0(line(1, [fact("Ember (John's daughter)"), fact("Computer Ember")]))
        merged = ce.from_v0(line(1, [fact("Ember")]))
        self.assertEqual([r["result"] for r in ce.referents(right, g.instances[0], g)], ["covered", "covered"])
        self.assertEqual([r["result"] for r in ce.referents(merged, g.instances[0], g)], ["severe", "severe"])

    def test_unsure_any_pick_is_hidden_uncertainty_and_abstaining_is_safe(self):
        g = gold({"id": "i", "fragment": 1, "split": "held-out", "mentions": [{"label": "Ember", "referent": "unsure"}]})
        picked = ce.from_v0(line(1, [fact("Computer Ember")]))
        left_out = ce.from_v0(line(1, [fact("John")]))
        self.assertEqual(ce.referents(picked, g.instances[0], g)[0]["result"], "severe")
        self.assertEqual(ce.referents(left_out, g.instances[0], g)[0]["result"], "abstained")

    def test_an_unresolved_reference_event_covers_an_unsure_mention(self):
        events = [
            {"type": "extraction-run", "payload": {"run_id": "r", "input_fragment_ids": [1], "model_id": "m"}},
            {"type": "reference-unresolved", "payload": {"run_id": "r", "ref_id": "ref1", "mention": "Ember",
                                                          "candidates": [{"concept_id": 3}, {"concept_id": 4}]}},
            {"type": "fact-proposed", "payload": {"run_id": "r", "subject": "ref1", "text": "t"}},
        ]
        rec = ce.from_events(events)[0]
        g = gold({"id": "i", "fragment": 1, "split": "held-out", "mentions": [{"label": "Ember", "referent": "unsure"}]})
        self.assertEqual(ce.referents(rec, g.instances[0], g)[0]["result"], "covered")


class Checks(unittest.TestCase):
    def test_a_mishearing_in_fact_text_is_invalid(self):
        rec = ce.from_v0(line(1, [fact("Computer Ember", "Emma reviewed it")]))
        self.assertTrue(any("T1a" in w for w in ce.invalid(rec, gold())))
        self.assertEqual(ce.invalid(ce.from_v0(line(1, [fact("Computer Ember", "Emmanuel reviewed it")])), gold()), [])

    def test_a_write_the_store_did_not_call_a_proposal_is_shown_as_truth(self):
        rec = ce.from_v0(line(1, [fact("A")], said="Added: #5 A: t"))
        self.assertEqual(ce.shown_as_truth(rec), [0])
        self.assertEqual(ce.shown_as_truth(ce.from_v0(line(1, [fact("A")]))), [])

    def test_validity_defaulted_to_the_fragment_date(self):
        inst = {"id": "h", "fragment": 1, "validity": {"text_dates": ["2026-11-01"]}}
        rec = ce.from_v0(line(1, [fact("A", as_of="2026-11-15"), fact("B", as_of="2026-11-01"), fact("C")]))
        wrong, defaulted, measured = ce.validity(rec, inst, {"1": "2026-11-15"})
        self.assertEqual((wrong, defaulted, measured), ([], [0], True))

    def test_supersede_on_an_extension_and_a_missed_stale_state(self):
        rec = ce.from_v0(line(1, [fact("A")], supersedes=[{"fact": 41, "by": 0}]))
        self.assertEqual(ce.supersedes(rec, {"supersede": "none"})["unexpected"], [41])
        self.assertEqual(ce.supersedes(ce.from_v0(line(1, [fact("A")])), {"supersede": [{"fact": 41, "kind": "change"}]})["missed"], [41])


class Bars(unittest.TestCase):
    def test_exact_counts(self):
        self.assertEqual(ce.need(95, 100, 20), 19)
        self.assertEqual(ce.need(95, 100, 21), 20)
        self.assertEqual(ce.need(75, 100, 3), 3)
        self.assertEqual(ce.bar_ceiling("x", 1, 49, 2, 100)["result"], "FAIL")  # under 50 facts, none allowed
        self.assertEqual(ce.bar_ceiling("x", 1, 50, 2, 100)["result"], "pass")

    def test_not_measured_never_passes_overall(self):
        r = ce.score_store("s", [], gold(), None, {})
        rows = ce.combine([r])
        self.assertEqual(next(x for x in rows if x["bar"].startswith("gold: zero severe"))["overall"], "not measured")
        self.assertNotIn("pass", {x["overall"] for x in rows if x["bar"].startswith("sample:")})

    def test_one_failing_repeat_fails_the_instance(self):
        g = gold({"id": "i", "fragment": 1, "split": "held-out", "mentions": [{"label": "Ember", "referent": "kid"}]})
        recs = [ce.from_v0(line(1, [fact("Ember (John's daughter)")], repeat=k)) for k in range(4)]
        recs.append(ce.from_v0(line(1, [fact("Ember")], repeat=4)))
        r = ce.score_store("s", recs, g, None, {})
        self.assertEqual(find(r, "gold: zero severe")["result"], "FAIL")
        self.assertEqual(find(r, "gold: referent coverage")["count"], "0/1")

    def test_a_held_out_instance_that_never_ran_fails(self):
        g = gold({"id": "a", "fragment": 1, "split": "held-out", "mentions": [{"label": "Computer Ember", "referent": "peer"}]},
                 {"id": "b", "fragment": 2, "split": "held-out", "mentions": [{"label": "Computer Ember", "referent": "peer"}]})
        r = ce.score_store("s", [ce.from_v0(line(1, [fact("Computer Ember")]))], g, None, {})
        self.assertEqual(find(r, "gold: every held-out instance ran")["result"], "FAIL")

    def test_development_instances_are_not_the_result(self):
        g = gold({"id": "i", "fragment": 1, "split": "development", "mentions": [{"label": "Ember", "referent": "kid"}]})
        recs = [ce.from_v0(line(1, [fact("Ember")]))]
        self.assertEqual(find(ce.score_store("s", recs, g, None, {}), "gold: zero severe")["result"], "not measured")
        self.assertEqual(find(ce.score_store("s", recs, g, None, {}, "development"), "gold: zero severe")["result"], "FAIL")

    def test_a_bar_passes_only_if_every_store_passes(self):
        good = ce.score_store("a", [ce.from_v0(line(1, [fact("A")]))], gold(), None, {})
        bad = ce.score_store("b", [ce.from_v0(line(1, [fact("A")] * 7))], gold(), None, {})
        rows = ce.combine([good, bad])
        self.assertEqual(next(x for x in rows if x["bar"].startswith("all: review load"))["overall"], "FAIL")

    def test_judged_sample(self):
        judged = [{"fragment": k, "verdict": "right"} for k in range(40)] + [{"fragment": 0, "verdict": "overstated"}]
        r = ce.score_store("s", [], gold(), judged, {})
        self.assertEqual(find(r, "sample: not in the source")["result"], "FAIL")  # 1 of 41: under 50, none allowed
        self.assertEqual(find(r, "sample: wrong confident referent")["result"], "pass")


class Sheet(unittest.TestCase):
    def test_blind_rows_carry_no_model_or_run(self):
        rows = ce.sheet([ce.from_v0(line(1, [fact("A"), fact("B")]))], seed=1)
        self.assertEqual(len(rows), 2)
        for row in rows:
            self.assertFalse({"model", "run", "template_sha256"} & set(row))


class Run(unittest.TestCase):
    def test_runs_the_real_extractor_on_copies_and_leaves_the_snapshot_alone(self):
        with tempfile.TemporaryDirectory() as d:
            snap = os.path.join(d, "snap.db")
            build_store(snap).close()
            os.chmod(snap, 0o400)
            seen_dbs, writes = [], []

            def store_for(db):
                seen_dbs.append(db)

                def store(*args, stdin=None, **_):
                    if args[0] == "list":
                        return json.dumps([{"id": 7, "content": "c", "type": "Personal", "created": "2026-11-15T01:00:00Z"}])
                    if args[:2] == ("concept", "list"):
                        return "[]"
                    if args[:2] == ("concept", "touched"):
                        return "[]"
                    if args[:2] == ("concept", "add"):
                        writes.append(args)
                        return "Proposed, waiting for `concept review`"
                    return ""
                return store

            answer = json.dumps({"concepts": [{"name": "A", "kind": "thing", "aliases": []}],
                                 "facts": [{"subject": "A", "text": "t", "object": None, "is_state": False, "as_of": None}]})
            g = gold({"id": "i", "fragment": 7, "split": "held-out"},
                     {"id": "p", "fragment": None, "split": "held-out", "status": "pending"})
            out = os.path.join(d, "runs.jsonl")
            ce.run(g, snap, "unused", out, 2, "Claude", call=lambda prompt: answer, store_for=store_for)
            recs = ce.records(out)
            self.assertEqual([r["repeat"] for r in recs], [0, 1])
            self.assertEqual(len(writes), 2)
            self.assertTrue(all(os.path.dirname(db) != d for db in seen_dbs))
            self.assertEqual(json.loads(Path(out + ".dates.json").read_text()), {"7": "2026-11-15"})

    def test_given_is_extracted_first_and_the_stale_fact_id_reaches_the_score(self):
        frags = {5: {"id": 5, "content": "old", "type": "Personal", "created": "2026-11-15T15:24:00Z"},
                 9: {"id": 9, "content": "new", "type": "Personal", "created": "2026-11-15T15:30:00Z"}}
        answers = {"old": {"facts": [{"subject": "PR #48", "text": "awaiting review", "is_state": True, "as_of": None}]},
                   "new": {"facts": [{"subject": "PR #48", "text": "approved", "is_state": True, "as_of": None}],
                           "supersedes": [{"fact": 41, "by": 0}]}}

        def store_for(db):
            facts = []  # this copy's facts: the given fragment's write must be visible to the instance's extraction

            def store(*args, stdin=None, **_):
                if args[0] == "list":
                    f = frags.get(int(args[2]) + 1)
                    return json.dumps([f] if f else [])
                if args[:2] == ("concept", "list"):
                    return "[]"
                if args[:2] == ("concept", "touched"):
                    return json.dumps([{"id": 1, "name": "PR #48", "facts": facts}] if facts else [])
                if args[:2] == ("concept", "add"):
                    fid = 41 + len(facts)
                    facts.append({"id": fid, "subject": args[2], "object": None, "text": args[4]})
                    return f"Proposed, waiting for `concept review` (a state): #{fid} PR #48: {args[4]} [#{args[6]}]"
                return ""
            return store

        with tempfile.TemporaryDirectory() as d:
            snap = os.path.join(d, "snap.db")
            build_store(snap).close()
            stale = {"id": "g", "fragment": 9, "split": "held-out", "given": [5], "supersede": [{"fact": None, "kind": "change"}],
                     "stale_fact": {"fragment": 5, "subject": "PR #48", "is_state": True, "text_has": "awaiting review"}}
            out = os.path.join(d, "runs.jsonl")
            ce.run(gold(stale), snap, "unused", out, 1, "Claude",
                   call=lambda prompt, order=iter(["old", "new"]): json.dumps(answers[next(order)]),  # given first
                   store_for=store_for)
            recs = ce.records(out)
            self.assertEqual([r["fragment"] for r in recs], [9])  # the given fragment is not a scored record
            self.assertEqual(recs[0]["stale_supersede"], [{"fact": 41, "kind": "change"}])
            self.assertEqual(ce.supersedes(recs[0], stale)["found"], [41])
            # The same instance with no stale id on the record (not run with `given`): missed, never found.
            bare = ce.from_v0(line(9, [fact("PR #48")], supersedes=[{"fact": 41, "by": 0}]))
            self.assertTrue(ce.supersedes(bare, stale)["missed"])
            self.assertEqual(ce.supersedes(bare, stale)["found"], [])

    def test_reads_what_v0_catch_up_reads_archived_too_but_not_replaced(self):
        # #63/#64: v0 reads archived fragments (an inbox message is archived once shown) unless a later one replaced
        # them; the eval has to see the same, or #14754/#19196 never run. The fake lists as the store does: one status.
        frags = [{"id": 5, "content": "given", "type": "Personal", "status": "Archived", "supersededBy": None, "created": "2026-11-15T15:24:00Z"},
                 {"id": 7, "content": "old view", "type": "Personal", "status": "Archived", "supersededBy": 8, "created": "2026-11-15T15:25:00Z"},
                 {"id": 9, "content": "inbox", "type": "Personal", "status": "Archived", "supersededBy": None, "created": "2026-11-15T15:30:00Z"}]
        prompts = []

        def store_for(db):
            def store(*args, stdin=None, **_):
                if args[0] == "list":
                    after, limit = int(args[args.index("--after") + 1]), int(args[args.index("--limit") + 1])
                    wanted = "Archived" if "--archived" in args else "Active"
                    return json.dumps([f for f in frags if f["id"] > after and f["status"] == wanted][:limit])
                if args[:2] in (("concept", "list"), ("concept", "touched")):
                    return "[]"
                if args[:2] == ("concept", "add"):
                    return "Proposed, waiting for `concept review`"
                return ""
            return store

        def call(prompt):
            prompts.append(prompt)
            return json.dumps({"concepts": [], "facts": [{"subject": "A", "text": "t", "is_state": False, "as_of": None}]})

        with tempfile.TemporaryDirectory() as d:
            snap = os.path.join(d, "snap.db")
            build_store(snap).close()
            g = gold({"id": "archived", "fragment": 9, "split": "held-out", "given": [5]},
                     {"id": "replaced", "fragment": 7, "split": "held-out"})
            out = os.path.join(d, "runs.jsonl")
            ce.run(g, snap, "unused", out, 1, "Claude", call=call, store_for=store_for)
            self.assertEqual([r["fragment"] for r in ce.records(out)], [9])   # the replaced #7 never runs
            self.assertEqual(2, len(prompts))                                 # given #5, then #9
            self.assertTrue(any("given" in p for p in prompts))

    def test_stale_fact_matching_needs_subject_phrase_and_state(self):
        said = "Proposed, waiting for `concept review` (a state): #77 PR #48: awaits review [#5]"
        given = [{"fragment": 5, "parsed": {"facts": [{"text": "awaits review", "is_state": True}]},
                  "written": [{"subject": "PR #48", "text": "awaits review", "said": said}]}]
        want = {"fragment": 5, "subject": "PR #48", "is_state": True, "text_has": "awaiting review"}
        self.assertIsNone(ce.stale_fact_id(given, want))
        self.assertEqual(ce.stale_fact_id(given, {**want, "text_has": ["awaiting review", "awaits review"]}), 77)
        self.assertIsNone(ce.stale_fact_id(given, {**want, "text_has": "awaits review", "is_state": False}))
        self.assertIsNone(ce.stale_fact_id(given, {**want, "text_has": "awaits review", "subject": "PR #47"}))

    def test_each_case_sees_only_evidence_below_its_first_fragment_and_its_own_ambiguous_json(self):
        # Live catch-up reaching a case's first fragment has seen only lower ids (Arden, room 1571760738178175067):
        # a fact from that fragment or later is a leak. Case "late" (#30) keeps the fact from #20; case "early"
        # (#25, given #15) loses it. Each run's state dir gets ambiguous.json (#66 reads it from there).
        with tempfile.TemporaryDirectory() as d:
            snap = os.path.join(d, "snap.db")
            con = build_store(snap)
            for f in (10, 15, 20, 25, 30):
                frag(con, f)
            concept(con, 1, "A", "a1")
            concept(con, 2, "B")
            db_fact(con, 100, 1, [10])
            db_fact(con, 101, 2, [20])
            con.commit()
            con.close()
            amb = os.path.join(d, "ambiguous.json")
            Path(amb).write_text(json.dumps({"Ember": [{"name": "Ember (peer)"}, {"name": "Ember (John's daughter)"}]}))
            frags = {i: {"id": i, "content": "c", "type": "Personal", "created": "2026-11-15T01:00:00Z"} for i in (15, 25, 30)}
            seen = []

            def store_for(db):
                def store(*args, stdin=None, **_):
                    if args[0] == "list":
                        f = frags.get(int(args[args.index("--after") + 1]) + 1)
                        return json.dumps([f] if f else [])
                    if args[:2] in (("concept", "list"), ("concept", "touched")):
                        c = sqlite3.connect(db)
                        seen.append((sorted(r[0] for r in c.execute("SELECT Id FROM Facts")),
                                     sorted(r[0] for r in c.execute("SELECT Id FROM Concepts"))))
                        state = Path(db).parent / "state" / "concepts" / "ambiguous.json"
                        ambs.append(json.loads(state.read_text()) if state.exists() else None)
                        c.close()
                        return "[]"
                    return ""
                return store

            ambs = []

            def call(prompt):
                return json.dumps({"concepts": [], "facts": []})

            g = gold({"id": "late", "fragment": 30, "split": "held-out"},
                     {"id": "early", "fragment": 25, "split": "held-out", "given": [15]})
            out = os.path.join(d, "runs.jsonl")
            dirs = []
            real = ce.prune_case

            def spy(db, first, scratch, ambiguous=None):
                dirs.append((first, scratch))
                return real(db, first, scratch, ambiguous)
            ce.prune_case = spy
            try:
                ce.run(g, snap, "unused", out, 1, "Claude", call=call, store_for=store_for, ambiguous=amb)
            finally:
                ce.prune_case = real
            self.assertEqual([first for first, _ in dirs], [30, 15])
            self.assertEqual(seen[0], ([100, 101], [1, 2]))       # late: #20's fact is below the cut
            self.assertIn(([100], [1]), seen[1:])                 # early: cut at given #15, B stood only on #20
            # #66 reads ambiguous.json from the run's own state dir: there, whole, on every store call.
            self.assertTrue(ambs and all(a == json.loads(Path(amb).read_text()) for a in ambs))

    def test_a_snapshot_that_is_not_013_is_refused_before_any_model_call(self):
        with tempfile.TemporaryDirectory() as d:
            snap = os.path.join(d, "snap.db")
            build_store(snap, upto="012").close()
            calls = []
            g = gold({"id": "i", "fragment": 7, "split": "held-out"})
            with self.assertRaises(SystemExit) as e:
                ce.run(g, snap, "unused", os.path.join(d, "o"), 1, "C", call=calls.append,
                       store_for=lambda db: (lambda *a, **k: ""))
            self.assertIn("schema drift", str(e.exception))
            self.assertEqual(calls, [])

    def test_refuses_the_live_store(self):
        with tempfile.TemporaryDirectory() as d:
            live = os.path.join(d, "live.db")
            Path(live).touch()
            os.environ["PERSISTENCE_STORE_DB"] = live
            try:
                with self.assertRaises(SystemExit):
                    ce.run(gold(), live, "x", os.path.join(d, "o"), 1, "C")
            finally:
                del os.environ["PERSISTENCE_STORE_DB"]


if __name__ == "__main__":
    unittest.main()
