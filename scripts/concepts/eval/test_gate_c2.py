"""gate_c2 on synthetic run lines: the sheet hides the filed referent and the flag, and the score fails each bar on the
case it is for (an unflagged misfile, too many correct facts flagged, a fact under the bare label, a refused add not
counted as written, an unlabelled fact voiding the score)."""
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
        """Label the sheet by text: {text: label}."""
        return [{**r, "label": labels[r["text"]]} for r in g.sheet(GOLD, [runs])]

    def test_sheet_is_blind_to_the_pick_and_the_flag(self):
        runs = self.runs(rec(933, w(PEER, "is called Computer Ember", flag="referent unsure: x"),
                             w(DAUGHTER, "is four", obj="John")))
        rows = g.sheet(GOLD, [runs])
        self.assertEqual({r["subject"] for r in rows}, {"Ember"})
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
        result, unlabelled = g.score(GOLD, [runs], self.judge(runs, labels))
        self.assertEqual(unlabelled, [])
        self.assertTrue(result["peer-only"]["pass"])
        self.assertTrue(result["both"]["pass"])
        self.assertEqual(result["peer-only"]["fragments"][1395]["runs covering every expected referent"], 1)

    def test_unflagged_misfile_fails_fire_and_flagged_one_does_not(self):
        bad = self.runs(rec(924, w(DAUGHTER, "may run on a ChatGPT plan")), rec(1395, w(PEER, "set its boundary")))
        labels = {"may run on a ChatGPT plan": "ember-peer", "set its boundary": "ember-peer"}
        result, _ = g.score(GOLD, [bad], self.judge(bad, labels))
        self.assertFalse(result["peer-only"]["fire"]["pass"])
        ok = self.runs(rec(924, w(DAUGHTER, "may run on a ChatGPT plan", flag="referent unsure: x")),
                       rec(1395, w(PEER, "set its boundary")))
        result, _ = g.score(GOLD, [ok], self.judge(ok, labels))
        self.assertTrue(result["peer-only"]["fire"]["pass"])
        self.assertEqual(result["peer-only"]["fire"]["misfiled"], 1)

    def test_a_both_row_filed_under_one_referent_is_misfiled(self):
        runs = self.runs(rec(2942, w(PEER, "is one of two Embers")), rec(933, w(DAUGHTER, "is four")))
        result, _ = g.score(GOLD, [runs], self.judge(runs, {"is one of two Embers": "both", "is four": "ember-daughter"}))
        self.assertFalse(result["both"]["fire"]["pass"])

    def test_quiet_fails_past_one_in_four(self):
        texts = ["a", "b", "c", "d"]
        two = self.runs(*(rec(933, w(DAUGHTER, t, flag="x" if t in "ab" else None)) for t in texts))
        result, _ = g.score(GOLD, [two], self.judge(two, {t: "ember-daughter" for t in texts}))
        self.assertFalse(result["both"]["quiet"]["pass"])
        one = self.runs(*(rec(933, w(DAUGHTER, t, flag="x" if t == "a" else None)) for t in texts))
        result, _ = g.score(GOLD, [one], self.judge(one, {t: "ember-daughter" for t in texts}))
        self.assertTrue(result["both"]["quiet"]["pass"])

    def test_flag_counts_when_the_store_took_the_add_even_with_the_mark_cut_off(self):
        long_said = "Added: #1 " + "x" * 290   # concepts.py keeps said[:300]; the mark falls past it
        runs = self.runs(rec(924, w(DAUGHTER, "t", flag="referent unsure: x", said=long_said)), rec(1395, w(PEER, "u")))
        result, _ = g.score(GOLD, [runs], self.judge(runs, {"t": "ember-peer", "u": "ember-peer"}))
        self.assertTrue(result["peer-only"]["fire"]["pass"])
        self.assertEqual(result["peer-only"]["fire"]["misfiled"], 1)

    def test_blank_flag_is_not_a_flag(self):
        runs = self.runs(rec(924, w(DAUGHTER, "t", flag="  ")), rec(1395, w(PEER, "u")))
        result, _ = g.score(GOLD, [runs], self.judge(runs, {"t": "ember-peer", "u": "ember-peer"}))
        self.assertFalse(result["peer-only"]["fire"]["pass"])

    def test_bare_label_fails(self):
        runs = self.runs(rec(1395, w("Ember", "set its boundary")))
        result, _ = g.score(GOLD, [runs], [])
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
        result, _ = g.score(GOLD, [runs], self.judge(runs, {"t": "ember-peer"}))
        self.assertFalse(result["peer-only"]["quiet"]["pass"])


if __name__ == "__main__":
    unittest.main()
