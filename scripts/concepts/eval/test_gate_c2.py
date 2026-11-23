"""gate_c2 on synthetic run lines: the sheet hides the filed referent and the flag, and the score fails each bar on the
case it is for (an unflagged misfile, too many correct facts flagged, a fact under the bare label, a refused add not
counted as written, an unlabelled fact voiding the score)."""
import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import gate_c2 as g  # noqa: E402

GOLD = Path(__file__).resolve().parent / "gold" / "arden-c2-gates.json"
PEER, DAUGHTER = "Ember (peer)", "Ember (John's daughter)"


def w(subject, text, flag=None, obj=None, said=None):
    if said is None:
        said = f"Added: #1 {subject}" + (" [flagged: referent unsure]" if flag else "")
    return {"subject": subject, "object": obj, "text": text, "flag": flag, "said": said}


def rec(fragment, *written, votes=("Ember (peer)",) * 3):
    return {"fragment": fragment, "written": list(written), "skipped": [],
            "referent_reading": {"Ember": {"votes": list(votes)}}}


class GateC2(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()

    def runs(self, *recs):
        p = Path(self.dir) / f"runs{len(list(Path(self.dir).iterdir()))}.jsonl"
        p.write_text("".join(json.dumps(r) + "\n" for r in recs), encoding="utf-8")
        return str(p)

    def judge(self, runs, labels):
        """Label the sheet by text: {text: label}. A string labels every slot on the row; a dict labels slot by slot."""
        out = []
        for r in g.sheet(GOLD, [runs]):
            lab = labels.get(r["text"], "")
            out.append({**r, "label": lab if isinstance(lab, dict) else {sl: lab for sl in r["label"]}})
        return out

    def test_sheet_is_blind_to_the_pick_and_the_flag(self):
        runs = self.runs(rec(933, w(PEER, "is called Computer Ember", flag="referent unsure: x"),
                             w(DAUGHTER, "is four", obj="John")))
        rows = g.sheet(GOLD, [runs])
        self.assertEqual({r["subject"] for r in rows}, {"Ember[1]"})
        self.assertEqual({r["object"] for r in rows}, {None, "John"})
        for r in rows:
            self.assertNotIn("flag", r)
            self.assertNotIn("said", r)
            self.assertNotIn(PEER, json.dumps(r))
            self.assertNotIn(DAUGHTER, json.dumps(r))

    def test_sheet_dedupes_the_same_fact_across_runs(self):
        runs = self.runs(rec(1395, w(PEER, "set its boundary")), rec(1395, w(PEER, "set its boundary", flag="x")))
        self.assertEqual(len(g.sheet(GOLD, [runs])), 1)

    def test_clean_run_passes(self):
        runs = self.runs(rec(1395, w(PEER, "set its boundary")), rec(924, w(PEER, "may run on a ChatGPT plan")),
                         rec(933, w(DAUGHTER, "is four")), rec(2942, w(DAUGHTER, "is misheard as Emma")))
        labels = {"set its boundary": "ember-peer", "may run on a ChatGPT plan": "ember-peer",
                  "is four": "ember-daughter", "is misheard as Emma": "ember-daughter"}
        result, unlabelled, _ = g.score(GOLD, [runs], self.judge(runs, labels))
        self.assertEqual(unlabelled, [])
        self.assertTrue(result["peer-only"]["pass"])
        self.assertTrue(result["both"]["pass"])
        self.assertEqual(result["peer-only"]["fragments"][1395]["runs covering every expected referent"], 1)

    def test_unflagged_misfile_fails_fire_and_flagged_one_does_not(self):
        bad = self.runs(rec(924, w(DAUGHTER, "may run on a ChatGPT plan")), rec(1395, w(PEER, "set its boundary")))
        labels = {"may run on a ChatGPT plan": "ember-peer", "set its boundary": "ember-peer"}
        result, _, _ = g.score(GOLD, [bad], self.judge(bad, labels))
        self.assertFalse(result["peer-only"]["fire"]["pass"])
        ok = self.runs(rec(924, w(DAUGHTER, "may run on a ChatGPT plan", flag="referent unsure: x")),
                       rec(1395, w(PEER, "set its boundary")))
        result, _, _ = g.score(GOLD, [ok], self.judge(ok, labels))
        self.assertTrue(result["peer-only"]["fire"]["pass"])
        self.assertEqual(result["peer-only"]["fire"]["misfiled"], 1)

    def test_a_both_row_filed_under_one_referent_is_misfiled(self):
        runs = self.runs(rec(2942, w(PEER, "is one of two Embers")), rec(933, w(DAUGHTER, "is four")))
        result, _, _ = g.score(GOLD, [runs], self.judge(runs, {"is one of two Embers": "both", "is four": "ember-daughter"}))
        self.assertFalse(result["both"]["fire"]["pass"])

    def test_quiet_fails_past_one_in_four(self):
        texts = ["a", "b", "c", "d"]
        two = self.runs(*(rec(933, w(DAUGHTER, t, flag="x" if t in "ab" else None)) for t in texts))
        result, _, _ = g.score(GOLD, [two], self.judge(two, {t: "ember-daughter" for t in texts}))
        self.assertFalse(result["both"]["quiet"]["pass"])
        one = self.runs(*(rec(933, w(DAUGHTER, t, flag="x" if t == "a" else None)) for t in texts))
        result, _, _ = g.score(GOLD, [one], self.judge(one, {t: "ember-daughter" for t in texts}))
        self.assertTrue(result["both"]["quiet"]["pass"])

    def test_flag_counts_when_the_store_took_the_add_even_with_the_mark_cut_off(self):
        long_said = "Added: #1 " + "x" * 290   # concepts.py keeps said[:300]; the mark falls past it
        runs = self.runs(rec(924, w(DAUGHTER, "t", flag="referent unsure: x", said=long_said)), rec(1395, w(PEER, "u")))
        result, _, _ = g.score(GOLD, [runs], self.judge(runs, {"t": "ember-peer", "u": "ember-peer"}))
        self.assertTrue(result["peer-only"]["fire"]["pass"])
        self.assertEqual(result["peer-only"]["fire"]["misfiled"], 1)

    def test_blank_flag_is_not_a_flag(self):
        runs = self.runs(rec(924, w(DAUGHTER, "t", flag="  ")), rec(1395, w(PEER, "u")))
        result, _, _ = g.score(GOLD, [runs], self.judge(runs, {"t": "ember-peer", "u": "ember-peer"}))
        self.assertFalse(result["peer-only"]["fire"]["pass"])

    def test_bare_label_fails(self):
        runs = self.runs(rec(1395, w("Ember", "set its boundary")))
        result, _, _ = g.score(GOLD, [runs], [])
        self.assertFalse(result["peer-only"]["bare"]["pass"])

    def test_refused_add_is_not_a_written_fact(self):
        runs = self.runs(rec(924, w(DAUGHTER, "t", said="")), rec(1395, w(PEER, "u")))
        self.assertEqual([r["text"] for r in g.sheet(GOLD, [runs])], ["u"])

    def test_unlabelled_fact_voids_the_score(self):
        runs = self.runs(rec(1395, w(PEER, "u")))
        empty = Path(self.dir) / "empty.jsonl"
        empty.write_text("")
        self.assertEqual(g.main(["score", str(GOLD), runs, "--judged", str(empty)]), 2)

    def test_no_correct_facts_is_not_a_quiet_pass(self):
        runs = self.runs(rec(924, w(DAUGHTER, "t", flag="x")))
        result, _, _ = g.score(GOLD, [runs], self.judge(runs, {"t": "ember-peer"}))
        self.assertFalse(result["peer-only"]["quiet"]["pass"])

    def test_a_fact_under_an_unlisted_ember_name_voids_the_score(self):
        """Claude's #87 review case: #924's ChatGPT-plan fact under 'Emma' (#924 lists no mishearing) and one under
        'Ember (AI)', beside a correct #1395 fact. Before the fix: 1 fact counted, peer-only PASS, exit 0."""
        runs = self.runs(rec(924, w("Emma", "may run on a ChatGPT plan"), w("Ember (AI)", "is the ChatGPT peer")),
                         rec(1395, w(PEER, "set its boundary")))
        judged = Path(self.dir) / "judged.jsonl"
        judged.write_text("".join(json.dumps(r) + "\n" for r in self.judge(runs, {"set its boundary": "ember-peer"})))
        self.assertEqual(g.main(["score", str(GOLD), runs, "--judged", str(judged)]), 2)
        _, _, unlisted = g.score(GOLD, [runs], g_load(judged))
        self.assertEqual(sorted(u[1] for u in unlisted), ["Ember (AI)", "Emma"])

    def test_a_listed_mishearing_is_filed_under_its_referent_and_masked(self):
        """In #933 'Emma' is the daughter heard wrong: filed under ember-daughter, shown on the sheet as bare 'Ember'."""
        runs = self.runs(rec(933, w("Emma", "is four")))
        rows = g.sheet(GOLD, [runs])
        self.assertEqual([r["subject"] for r in rows], ["Ember[1]"])
        result, unlabelled, unlisted = g.score(GOLD, [runs], self.judge(runs, {"is four": "ember-daughter"}))
        self.assertEqual((unlabelled, unlisted), ([], []))
        self.assertEqual(result["both"]["counts"]["right"], 1)

    def test_names_that_only_contain_ember_as_part_of_a_word_are_not_unlisted(self):
        runs = self.runs(rec(1395, w(PEER, "u"), w("Embers Inc", "v"), w("Emmanuel", "w")))
        _, _, unlisted = g.score(GOLD, [runs], self.judge(runs, {"u": "ember-peer"}))
        self.assertEqual(unlisted, [])

    def test_a_listed_other_ember_named_concept_confirmed_other_is_in_no_bar(self):
        """#1395 'Ember store-request boundary' (gold other_names): a thing of the peer's. Judged 'other', it is counted
        apart and in no bar. Listed for #1395 only, so under #924 it still voids."""
        runs = self.runs(rec(1395, w(PEER, "u"), w("Ember store-request boundary", "is parsed as data")),
                         rec(924, w("Ember store-request boundary", "x")))
        result, unlabelled, unlisted = g.score(GOLD, [runs], self.judge(runs, {"u": "ember-peer", "is parsed as data": "other"}))
        self.assertEqual(unlabelled, [])
        self.assertEqual(result["peer-only"]["fragments"][1395]["facts under an other Ember-named concept"], 1)
        self.assertEqual(result["peer-only"]["counts"]["facts"], 1)
        self.assertTrue(result["peer-only"]["pass"])
        self.assertEqual([u[:2] for u in unlisted], [(924, "Ember store-request boundary")])   # listed for #1395 only

    def test_other_named_facts_go_on_the_sheet_unmasked(self):
        """Claude's #87 re-review point 1: a judge confirms each fact under an other_names concept. The name is shown
        as written (it is not a referent pick), so the judge can say whether the fact is about that thing."""
        runs = self.runs(rec(1395, w(PEER, "u"), w("Ember store-request boundary", "is parsed as data")))
        rows = {r["text"]: r for r in g.sheet(GOLD, [runs])}
        self.assertEqual(set(rows), {"u", "is parsed as data"})
        self.assertEqual(rows["is parsed as data"]["subject"], "Ember store-request boundary")
        self.assertEqual(rows["u"]["subject"], "Ember[1]")

    def test_an_other_named_fact_judged_a_referent_is_an_unflagged_misfile(self):
        """Claude's probe: 'Ember store-request boundary' -> 'is four years old' showed only as a count. Judged the
        daughter, it is misfiled, and unflagged it fails fire."""
        runs = self.runs(rec(1395, w(PEER, "u"), w("Ember store-request boundary", "is four years old")))
        result, unlabelled, _ = g.score(GOLD, [runs], self.judge(runs, {"u": "ember-peer", "is four years old": "ember-daughter"}))
        self.assertEqual(unlabelled, [])
        self.assertFalse(result["peer-only"]["fire"]["pass"])
        self.assertEqual(result["peer-only"]["fire"]["misfiled unflagged"], 1)

    def test_an_unjudged_other_named_fact_voids_the_score(self):
        runs = self.runs(rec(1395, w(PEER, "u"), w("Ember store-request boundary", "is four years old")))
        judged = Path(self.dir) / "judged.jsonl"
        judged.write_text("".join(json.dumps({**r, "label": {"Ember[1]": "ember-peer"}}) + "\n"
                                  for r in g.sheet(GOLD, [runs]) if r["text"] == "u"))
        self.assertEqual(g.main(["score", str(GOLD), runs, "--judged", str(judged)]), 2)

    def test_score_prints_each_other_named_fact_with_its_text(self):
        runs = self.runs(rec(1395, w(PEER, "u"), w("Ember store-request boundary", "is parsed as data")))
        judged = Path(self.dir) / "judged.jsonl"
        judged.write_text("".join(json.dumps(r) + "\n" for r in self.judge(runs, {"u": "ember-peer", "is parsed as data": "other"})))
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            g.main(["score", str(GOLD), runs, "--judged", str(judged)])
        self.assertIn("is parsed as data", out.getvalue())
        self.assertIn("Ember store-request boundary", out.getvalue())

    def test_endpoints_lists_every_endpoint_no_listed_name_covers(self):
        """Claude's #87 re-review point 2: a peer fact filed under "John's daughter" in #924 has no ember/emma in its
        name, so it was neither counted nor voided. Report only: every distinct endpoint per fragment that is no listed
        name, mishearing, other name or bare label, with how many stored facts use it."""
        runs = self.runs(rec(924, w("John's daughter", "may run on a ChatGPT plan", obj="John"), w(PEER, "u")),
                         rec(924, w("John's daughter", "games on the PC")),
                         rec(933, w("Emma", "is four"), w("Synth", "t", said="")))   # refused add: not written
        self.assertEqual(g.endpoints(GOLD, [runs]), {924: {"John's daughter": 2, "John": 1}})

    def test_endpoints_command_prints_the_list_and_exits_0(self):
        runs = self.runs(rec(924, w("John's daughter", "may run on a ChatGPT plan")))
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(g.main(["endpoints", str(GOLD), runs]), 0)
        self.assertIn("#924", out.getvalue())
        self.assertIn("John's daughter", out.getvalue())

    def test_score_reports_unlisted_endpoints_without_voiding(self):
        runs = self.runs(rec(1395, w(PEER, "u", obj="John")))
        judged = Path(self.dir) / "judged.jsonl"
        judged.write_text("".join(json.dumps(r) + "\n" for r in self.judge(runs, {"u": "ember-peer"})))
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = g.main(["score", str(GOLD), runs, "--judged", str(judged)])
        self.assertIn("'John'", out.getvalue())
        self.assertNotEqual(code, 2)

    # PR #88 at e4b8eb7, Claude's review (room 1574392242230136854): the sheet masked an endpoint only where it was
    # filed under the row's own referent, so the 'other' row and both-referent facts showed the pick. Arden's rule
    # (1574392386581299311): every referent name, mishearing and bare label is a masked slot, whatever the filing; one
    # row per fact; a label per slot; a fact is right only if every slot is.

    def assert_blind(self, rows):
        names = [n for c in json.loads(GOLD.read_text())["concepts"].values() for n in c["names"]]
        for r in rows:
            shown = json.dumps([r["subject"], r["object"]])
            for n in names + ["PEER", "DAUGHTER", "peer", "daughter", "Emma"]:
                self.assertNotIn(n, shown, r)

    def test_no_row_shows_a_referent_name_or_mishearing_whatever_it_is_filed_under(self):
        runs = self.runs(rec(1395, w(PEER, "is parsed as data", obj="Ember store-request boundary"),
                             w("Ember store-request boundary", "binds", obj=PEER)),
                         rec(933, w(DAUGHTER, "is not", obj=PEER), w("Emma", "games", obj="Computer Ember"),
                             w("Ember", "is", obj="Ember (daughter)")),
                         rec(2942, w("Ember (AI peer)", "x", obj="John's daughter Ember")))
        rows = g.sheet(GOLD, [runs])
        self.assertEqual(len(rows), 6)
        self.assert_blind(rows)

    def test_claudes_1395_probe_gives_one_row(self):
        """'Ember (peer)' -> 'Ember store-request boundary' was two rows, 'Ember (peer) -> ...' and 'Ember -> ...'."""
        runs = self.runs(rec(1395, w(PEER, "is parsed as data", obj="Ember store-request boundary")))
        rows = g.sheet(GOLD, [runs])
        self.assertEqual(len(rows), 1)
        self.assertEqual((rows[0]["subject"], rows[0]["object"]), ("Ember[1]", "Ember store-request boundary"))
        self.assertEqual(set(rows[0]["label"]), {"Ember[1]", "Ember store-request boundary"})
        self.assert_blind(rows)

    def test_a_both_referent_fact_is_one_row_with_two_slots(self):
        runs = self.runs(rec(933, w(DAUGHTER, "is not", obj=PEER)))
        rows = g.sheet(GOLD, [runs])
        self.assertEqual(len(rows), 1)
        self.assertEqual((rows[0]["subject"], rows[0]["object"]), ("Ember[1]", "Ember[2]"))
        self.assertEqual(rows[0]["label"], {"Ember[1]": "", "Ember[2]": ""})

    def test_a_both_referent_fact_is_right_only_if_every_slot_is(self):
        runs = self.runs(rec(933, w(DAUGHTER, "is not", obj=PEER)), rec(2942, w(DAUGHTER, "is four")))
        good = {"is not": {"Ember[1]": "ember-daughter", "Ember[2]": "ember-peer"}, "is four": "ember-daughter"}
        result, unlabelled, _ = g.score(GOLD, [runs], self.judge(runs, good))
        self.assertEqual(unlabelled, [])
        self.assertEqual((result["both"]["counts"]["facts"], result["both"]["counts"]["right"]), (2, 2))
        self.assertTrue(result["both"]["pass"])
        for wrong in ({"Ember[1]": "ember-daughter", "Ember[2]": "ember-daughter"},     # object side wrong
                      {"Ember[1]": "ember-peer", "Ember[2]": "ember-peer"}):            # subject side wrong
            result, _, _ = g.score(GOLD, [runs], self.judge(runs, {**good, "is not": wrong}))
            self.assertEqual(result["both"]["counts"]["facts"], 2)
            self.assertEqual(result["both"]["fire"]["misfiled unflagged"], 1, wrong)
            self.assertFalse(result["both"]["fire"]["pass"])

    def test_the_same_fact_filed_differently_across_runs_is_one_row(self):
        """The judge must not see that two runs disagreed: one row, and each run is scored against its label."""
        runs = self.runs(rec(933, w(DAUGHTER, "is four")), rec(933, w(PEER, "is four")))
        rows = g.sheet(GOLD, [runs])
        self.assertEqual(len(rows), 1)
        result, _, _ = g.score(GOLD, [runs], self.judge(runs, {"is four": "ember-daughter"}))
        self.assertEqual((result["both"]["counts"]["right"], result["both"]["counts"]["misfiled"]), (1, 1))

    def test_a_half_labelled_row_voids_the_score(self):
        runs = self.runs(rec(933, w(DAUGHTER, "is not", obj=PEER)))
        _, unlabelled, _ = g.score(GOLD, [runs], self.judge(runs, {"is not": {"Ember[1]": "ember-daughter"}}))
        self.assertEqual(len(unlabelled), 1)

    def test_a_referent_filed_slot_judged_other_voids_and_says_why(self):
        runs = self.runs(rec(1395, w(PEER, "u")))
        judged = Path(self.dir) / "judged.jsonl"
        judged.write_text("".join(json.dumps(r) + "\n" for r in self.judge(runs, {"u": "other"})))
        err = io.StringIO()
        with contextlib.redirect_stderr(err), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(g.main(["score", str(GOLD), runs, "--judged", str(judged)]), 2)
        self.assertIn("judged 'other'", err.getvalue())
        self.assertIn("filed under a referent", err.getvalue())
        self.assertNotIn("not labelled", err.getvalue())

    def test_endpoints_skips_error_records_as_score_does(self):
        runs = self.runs({**rec(924, w("John's daughter", "games on the PC")), "error": "timeout"},
                         rec(924, w(PEER, "u")))
        self.assertEqual(g.endpoints(GOLD, [runs]), {})


def g_load(path):
    return [json.loads(l) for l in Path(path).read_text().splitlines() if l.strip()]


if __name__ == "__main__":
    unittest.main()
