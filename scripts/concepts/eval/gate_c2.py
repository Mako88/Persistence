"""Condition 2's referent gates (gold/arden-c2-gates.json): the 'referent unsure' flag, both directions, on more than
one fragment. Reads `concept_eval.py run` output; never calls a model.

    gate_c2.py sheet <gold.json> <runs.jsonl>... --out sheet.jsonl [--seed 1]
        a blind sheet: one row per distinct written fact with an Ember referent as subject or object, that referent
        shown as the bare label, no flag, run or repeat on it, shuffled. It holds fact text: keep it private. The
        store's self fills `label` with a referent key (ember-peer, ember-daughter) or 'both'.
    gate_c2.py score <gold.json> <runs.jsonl>... --judged sheet.jsonl [--json]
        per gate: fire (0 misfiled facts written unflagged), quiet (at most 1 in 4 correctly filed facts flagged),
        bare (0 facts under the bare label); per fragment, report only: coverage, the reading's votes, facts dropped
        by the referent check. A fact on the runs but not labelled on the sheet fails the score (exit 2).
"""
import argparse
import hashlib
import json
import random
import sys
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from concept_eval import norm  # noqa: E402

QUIET = (1, 4)   # at most 1 in 4 correctly filed facts flagged


def load_gold(path):
    g = json.loads(Path(path).read_text(encoding="utf-8"))
    names = {norm(n): k for k, c in g["concepts"].items() for n in c["names"]}
    bare = {norm(lab) for lab in g.get("ambiguous_labels") or {}}
    gates = {name: [int(f) for f in gate["fragments"]] for name, gate in g["gates"].items()}
    expect = {int(i["fragment"]): {m["referent"] for m in i.get("mentions") or []} for i in g["instances"]}
    return names, bare, gates, expect


def rows(paths):
    for p in paths:
        for line in Path(p).read_text(encoding="utf-8").splitlines():
            if line.strip():
                yield json.loads(line)


def stored(w):
    """The store took the add: `cli` returns '' when it refused (a store without --flag refuses the whole add)."""
    return bool((w.get("said") or "").strip())


def flagged(w):
    """Written flagged, for a fact ember_facts kept (so stored): it passed --flag, and a store without --flag refuses
    the whole add (concepts.py), so a stored add that passed one carries it. Not the '[flagged' mark in `said`: concepts.py keeps said[:300], which cuts
    the mark off a long reply (23 of 32 flagged #933 facts on the first run)."""
    return bool((w.get("flag") or "").strip())


def ember_facts(rec, names, bare):
    """(written entry, filed referent key or 'bare', masked subject, masked object) for each stored fact with an
    Ember endpoint. A fact naming both referents is filed under each, once per referent."""
    out = []
    for w in rec.get("written") or []:
        if not isinstance(w, dict) or not stored(w):
            continue
        ends = [w.get("subject"), w.get("object")]
        filed = []
        for e in ends:
            n = norm(e)
            if n in bare:
                filed.append("bare")
            elif n in names:
                filed.append(names[n])
        for k in dict.fromkeys(filed):
            mask = [("Ember" if (norm(e) in bare or names.get(norm(e)) == k) else e) if e else e for e in ends]
            out.append((w, k, mask[0], mask[1]))
    return out


def key(fragment, subject, obj, text):
    return hashlib.sha256(json.dumps([fragment, subject or "", obj or "", text or ""]).encode()).hexdigest()[:16]


def sheet(gold, paths, seed=1):
    names, bare, _, _ = load_gold(gold)
    seen = {}
    for rec in rows(paths):
        for w, _, s, o in ember_facts(rec, names, bare):
            k = key(rec["fragment"], s, o, w.get("text"))
            seen.setdefault(k, {"key": k, "fragment": rec["fragment"], "subject": s, "object": o, "text": w.get("text"), "label": ""})
    out = list(seen.values())
    random.Random(seed).shuffle(out)
    return out


def score(gold, paths, judged):
    names, bare, gates, expect = load_gold(gold)
    labels = {r["key"]: r.get("label") for r in judged}
    by_frag = {}
    for rec in rows(paths):
        by_frag.setdefault(int(rec["fragment"]), []).append(rec)
    unlabelled, result = [], {}
    for gate, frags in gates.items():
        c = Counter()
        per = {}
        for fid in frags:
            recs = by_frag.get(fid, [])
            fc = Counter(runs=len(recs))
            votes = Counter()
            for rec in recs:
                if rec.get("error"):
                    fc["errors"] += 1
                    continue
                for v in ((rec.get("referent_reading") or {}).get("Ember") or {}).get("votes") or []:
                    votes[v] += 1
                fc["dropped by referent check"] += sum(1 for s in rec.get("skipped") or []
                                                      if isinstance(s, dict) and str(s.get("why", "")).startswith("referent not confirmed"))
                covered = set()
                for w, filed, s, o in ember_facts(rec, names, bare):
                    if filed == "bare":
                        c["bare"] += 1
                        continue
                    label = labels.get(key(fid, s, o, w.get("text")))
                    if label not in ("ember-peer", "ember-daughter", "both"):
                        unlabelled.append((fid, s, o, w.get("text")))
                        continue
                    c["facts"] += 1
                    if label == filed:
                        covered.add(filed)
                        c["right"] += 1
                        c["right flagged"] += flagged(w)
                    else:
                        c["misfiled"] += 1
                        c["misfiled flagged"] += flagged(w)
                        c["misfiled unflagged"] += not flagged(w)
                for k in covered:
                    fc[f"runs covering {k}"] += 1
                fc["runs covering every expected referent"] += covered >= expect.get(fid, set())
            per[fid] = {**fc, "reading votes": dict(votes)}
        quiet_ok = c["right flagged"] * QUIET[1] <= c["right"] * QUIET[0]
        result[gate] = {
            "fragments": per, "counts": dict(c),
            "fire": {"pass": c["misfiled unflagged"] == 0, "misfiled unflagged": c["misfiled unflagged"], "misfiled": c["misfiled"]},
            "quiet": {"pass": quiet_ok and c["right"] > 0, "right flagged": c["right flagged"], "right": c["right"],
                      "bar": f"<= {QUIET[0]} in {QUIET[1]}"},
            "bare": {"pass": c["bare"] == 0, "facts": c["bare"]},
        }
        result[gate]["pass"] = all(result[gate][b]["pass"] for b in ("fire", "quiet", "bare"))
    return result, unlabelled


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("sheet")
    s.add_argument("gold")
    s.add_argument("runs", nargs="+")
    s.add_argument("--out", required=True)
    s.add_argument("--seed", type=int, default=1)
    sc = sub.add_parser("score")
    sc.add_argument("gold")
    sc.add_argument("runs", nargs="+")
    sc.add_argument("--judged", required=True)
    sc.add_argument("--json", action="store_true")
    a = ap.parse_args(argv)
    if a.cmd == "sheet":
        out = sheet(a.gold, a.runs, a.seed)
        Path(a.out).write_text("".join(json.dumps(r) + "\n" for r in out), encoding="utf-8")
        print(f"{len(out)} rows to {a.out}", file=sys.stderr)
        return 0
    judged = [json.loads(l) for l in Path(a.judged).read_text(encoding="utf-8").splitlines() if l.strip()]
    result, unlabelled = score(a.gold, a.runs, judged)
    if a.json:
        print(json.dumps({"gates": result, "unlabelled": len(unlabelled)}, indent=1))
    else:
        for gate, r in result.items():
            print(f"{gate}: {'PASS' if r['pass'] else 'FAIL'}")
            print(f"  fire  {'pass' if r['fire']['pass'] else 'FAIL'}: misfiled unflagged {r['fire']['misfiled unflagged']} "
                  f"(misfiled {r['fire']['misfiled']})")
            print(f"  quiet {'pass' if r['quiet']['pass'] else 'FAIL'}: right flagged {r['quiet']['right flagged']}/{r['quiet']['right']} "
                  f"(bar {r['quiet']['bar']})")
            print(f"  bare  {'pass' if r['bare']['pass'] else 'FAIL'}: {r['bare']['facts']}")
            for fid, f in r["fragments"].items():
                print(f"  #{fid}: " + ", ".join(f"{k} {v}" for k, v in f.items()))
    if unlabelled:
        print(f"{len(unlabelled)} written facts are not labelled on the sheet: the score is void", file=sys.stderr)
        return 2
    return 0 if all(r["pass"] for r in result.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
