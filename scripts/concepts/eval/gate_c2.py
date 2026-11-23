"""Condition 2's referent gates (gold/arden-c2-gates.json): the 'referent unsure' flag, both directions, on more than
one fragment. Reads `concept_eval.py run` output; never calls a model.

    gate_c2.py sheet <gold.json> <runs.jsonl>... --out sheet.jsonl [--seed 1]
        a blind sheet: one row per distinct written fact with an Ember endpoint, no flag, run or repeat on it,
        shuffled. It holds fact text: keep it private. Every referent name, mishearing and bare label is masked as a
        slot (subject Ember[1], object the next number), whatever the fact was filed under, so no row shows a pick.
        In the text, each of them (and any 'Ember (...)') is a plain 'Ember', and rows are deduped on the masked text.
        The store's self fills each slot in `label` with a referent key (ember-peer, ember-daughter) or 'both'. A
        name the gold lists in a fragment's other_names (a thing of the peer's, say) is a slot shown unmasked: the
        judge writes 'other' if the fact is about that thing, or the referent it is really about.
    gate_c2.py score <gold.json> <runs.jsonl>... --judged sheet.jsonl [--json]
        A fact is right only if every slot's label is what it was filed under; one wrong slot, subject or object
        side, makes it misfiled. Per gate: fire (0 misfiled facts written unflagged), quiet (at most 1 in 4 correctly
        filed facts flagged),
        bare (0 facts under the bare label); per fragment, report only: coverage, the reading's votes, facts dropped
        by the referent check. A fact on the runs with a slot not labelled on the sheet fails the score (exit 2), as
        does a referent-filed slot judged 'other', and a fact
        under a name that reads as an Ember (ember/emma, whole word) but is no name, bare label or mishearing the gold
        lists for that fragment. Facts judged 'other' are printed with their text and counted in no bar; one judged a
        referent is misfiled. The endpoints report follows.
    gate_c2.py endpoints <gold.json> <runs.jsonl>...
        report only: every distinct endpoint of a stored fact, per fragment, that is no name, mishearing, other name
        or bare label the gold lists for it, with how many facts use it. A fact filed under "John's daughter" has no
        Ember in its name, so neither the bars nor the void see it; this list is where a reader does.
"""
import argparse
import hashlib
import json
import random
import re
import sys
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from concept_eval import norm  # noqa: E402

QUIET = (1, 4)   # at most 1 in 4 correctly filed facts flagged
EMBERISH = re.compile(r"(?<!\w)(ember|emma)(?!\w)", re.I)   # an endpoint that may mean an Ember the gold doesn't list
TAGGED = r"(?:ember|emma)\s*\([^()]*\)"   # 'Ember (AI)' and the like in fact text, listed or not


def load_gold(path):
    g = json.loads(Path(path).read_text(encoding="utf-8"))
    names = {norm(n): k for k, c in g["concepts"].items() for n in c["names"]}
    bare = {norm(lab) for lab in g.get("ambiguous_labels") or {}}
    gates = {name: [int(f) for f in gate["fragments"]] for name, gate in g["gates"].items()}
    expect = {int(i["fragment"]): {m["referent"] for m in i.get("mentions") or []} for i in g["instances"]}
    # a mishearing is a fact about one fragment's text, so each instance lists its own; the top-level map is not read
    heard = {int(i["fragment"]): {norm(n): k for n, k in (i.get("mishearings") or {}).items()} for i in g["instances"]}
    # names with an Ember in them that name a different concept (a thing of the peer's, say); counted in no bar
    for i in g["instances"]:
        heard[int(i["fragment"])].update({norm(n): None for n in i.get("other_names") or {}})
    return names, bare, gates, expect, heard


def names_for(names, heard, fragment):
    """The names that file a fact under a referent in this fragment: the gold's names plus its own mishearings."""
    return {**names, **heard.get(int(fragment), {})}


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


REFERENTS = ("ember-peer", "ember-daughter", "both")


def masker(names):
    """A function that masks fact text for one fragment. Every referent name and listed mishearing, and any
    'Ember (...)', becomes a plain 'Ember': whole name, case-insensitive, any whitespace, with any '(...)' after it,
    longest first (which matters only once one listed name is a prefix of another). An other_names name is kept as
    written. Never the slot: 'Ember[1]' where the text names the referent the slot is filed under and 'Ember' where it
    doesn't would tell a judge whether text and filing agree."""
    alts = sorted(names, key=len, reverse=True)
    pat = re.compile(r"(?<!\w)(?P<n>" + "|".join(r"\s+".join(map(re.escape, n.split())) for n in alts) + "|" + TAGGED
                     + r")(?:\s*\([^()]*\))?(?!\w)", re.I)   # a name may carry its own tag: "John's daughter Ember (peer)"

    def mask(text):
        if not text:
            return text
        return pat.sub(lambda m: m.group(0) if names.get(norm(m.group("n")), "") is None else "Ember", text)
    return mask


def ember_facts(rec, names, bare, unlisted=None):
    """(written entry, slots, shown subject, shown object, shown text) for each stored fact with an Ember endpoint:
    one per fact, however many referents it names. Each Ember endpoint is a slot (shown name, filed: a referent key, 'other' or
    'bare'). Every referent name, mishearing and bare label is masked, whatever it is filed under: the subject is
    Ember[1], an object is the next number. An other_names endpoint is a slot shown as written (it names a thing, not
    a pick). An endpoint that reads as an Ember (EMBERISH) but is no listed name, mishearing or bare label is not a
    slot; it is appended to `unlisted` as (fact, name). The text is masked by `masker`."""
    mask = masker(names)
    out = []
    for w in rec.get("written") or []:
        if not isinstance(w, dict) or not stored(w):
            continue
        shown, slots = [], []
        for e in (w.get("subject"), w.get("object")):
            n = norm(e)
            if e and (n in bare or (n in names and names[n] is not None)):
                sl = f"Ember[{sum(1 for x in slots if x[0].startswith('Ember['))+1}]"
                slots.append((sl, "bare" if n in bare else names[n]))
                shown.append(sl)
                continue
            if e and n in names:   # an other_names concept
                slots.append((e, "other"))
            elif e and EMBERISH.search(e) and unlisted is not None:
                unlisted.append((w, e))
            shown.append(e)
        if slots:
            out.append((w, slots, shown[0], shown[1], mask(w.get("text"))))
    return out


def key(fragment, subject, obj, text):
    return hashlib.sha256(json.dumps([fragment, subject or "", obj or "", text or ""]).encode()).hexdigest()[:16]


def sheet(gold, paths, seed=1):
    """One row per distinct fact as shown (masked), so a fact filed differently across runs is one row. `label` has a
    slot per Ember endpoint for the judge to fill: a referent key, 'both', or 'other' for an other_names slot."""
    names, bare, _, _, heard = load_gold(gold)
    seen = {}
    for rec in rows(paths):
        for w, slots, s, o, t in ember_facts(rec, names_for(names, heard, rec["fragment"]), bare):
            k = key(rec["fragment"], s, o, t)
            seen.setdefault(k, {"key": k, "fragment": rec["fragment"], "subject": s, "object": o, "text": t,
                                "label": {sl: "" for sl, _ in slots}})
    out = list(seen.values())
    random.Random(seed).shuffle(out)
    return out


def score(gold, paths, judged):
    names, bare, gates, expect, heard = load_gold(gold)
    labels = {r["key"]: r.get("label") for r in judged}
    by_frag = {}
    for rec in rows(paths):
        by_frag.setdefault(int(rec["fragment"]), []).append(rec)
    unlabelled, unlisted, result = [], [], {}   # unlabelled: (fid, s, o, text, why)
    for gate, frags in gates.items():
        c = Counter()
        per = {}
        others = []
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
                odd = []
                found = ember_facts(rec, names_for(names, heard, fid), bare, odd)
                unlisted.extend((fid, e, w.get("text")) for w, e in odd)
                for w, slots, s, o, t in found:
                    fact = (fid, s, o, t)
                    if any(f == "bare" for _, f in slots):
                        c["bare"] += 1
                    judged_slots = [(sl, f) for sl, f in slots if f != "bare"]   # a bare slot has no pick to judge
                    if not judged_slots:
                        continue
                    label = labels.get(key(fid, s, o, t))
                    label = label if isinstance(label, dict) else {}
                    got = [(f, label.get(sl)) for sl, f in judged_slots]
                    if any(lab not in REFERENTS + ("other",) for _, lab in got):
                        unlabelled.append((*fact, "blank"))
                        continue
                    if any(f != "other" and lab == "other" for f, lab in got):
                        unlabelled.append((*fact, "referent judged other"))
                        continue
                    if all(lab == "other" for _, lab in got):
                        fc["facts under an other Ember-named concept"] += 1
                        others.append(fact)
                        continue
                    # in a bar: right only if every slot is (an other slot judged 'other' is right)
                    c["facts"] += 1
                    if all(f == lab for f, lab in got):
                        covered.update(f for f, _ in got if f != "other")
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
            "other": [{"fragment": f, "subject": s, "object": o, "text": t} for f, s, o, t in others],
        }
        result[gate]["pass"] = all(result[gate][b]["pass"] for b in ("fire", "quiet", "bare"))
    return result, unlabelled, unlisted


def endpoints(gold, paths):
    """{fragment: {endpoint: stored facts using it}} for every endpoint no listed name, mishearing, other name or bare
    label covers in that fragment. Report only."""
    names, bare, _, _, heard = load_gold(gold)
    out = {}
    for rec in rows(paths):
        if rec.get("error"):
            continue   # as score skips them
        fid = int(rec["fragment"])
        known = names_for(names, heard, fid)
        for w in rec.get("written") or []:
            if not isinstance(w, dict) or not stored(w):
                continue
            for e in dict.fromkeys(x for x in (w.get("subject"), w.get("object")) if x):
                if norm(e) not in known and norm(e) not in bare:
                    out.setdefault(fid, Counter())[e] += 1
    return {f: dict(c) for f, c in sorted(out.items())}


def print_endpoints(ends):
    print("endpoints no listed name covers (report only; read them for a misfiling the gate can't see):")
    if not ends:
        print("  none")
    for fid, c in ends.items():
        print(f"  #{fid}: " + ", ".join(f"{e!r} {n}" for e, n in sorted(c.items(), key=lambda x: (-x[1], x[0]))))


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
    en = sub.add_parser("endpoints")
    en.add_argument("gold")
    en.add_argument("runs", nargs="+")
    a = ap.parse_args(argv)
    if a.cmd == "sheet":
        out = sheet(a.gold, a.runs, a.seed)
        Path(a.out).write_text("".join(json.dumps(r) + "\n" for r in out), encoding="utf-8")
        print(f"{len(out)} rows to {a.out}", file=sys.stderr)
        return 0
    if a.cmd == "endpoints":
        print_endpoints(endpoints(a.gold, a.runs))
        return 0
    judged = [json.loads(l) for l in Path(a.judged).read_text(encoding="utf-8").splitlines() if l.strip()]
    result, unlabelled, unlisted = score(a.gold, a.runs, judged)
    blank = [u for u in unlabelled if u[-1] == "blank"]
    judged_other = [u for u in unlabelled if u[-1] != "blank"]
    ends = endpoints(a.gold, a.runs)
    if a.json:
        print(json.dumps({"gates": result, "unlabelled": len(unlabelled), "unlisted": len(unlisted),
                          "endpoints": {str(f): c for f, c in ends.items()}}, indent=1))
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
            for o in r["other"]:
                print(f"  other (judged 'other', in no bar) #{o['fragment']}: {o['subject']!r} -> {o['object']!r}: {o['text']}")
        print_endpoints(ends)
    if blank:
        print(f"{len(blank)} written facts are not labelled on the sheet (a slot left blank or "
              f"not a label): the score is void", file=sys.stderr)
    if judged_other:
        print(f"{len(judged_other)} written facts have a slot filed under a referent but judged 'other': 'other' is "
              f"for an other_names slot only, so relabel it with the referent it is about: the score is void",
              file=sys.stderr)
    if unlisted:
        names = sorted({e for _, e, _ in unlisted})
        print(f"{len(unlisted)} written facts are filed under an Ember name the gold doesn't list for their fragment "
              f"({', '.join(map(repr, names))}): the score is void", file=sys.stderr)
    if unlabelled or unlisted:
        return 2
    return 0 if all(r["pass"] for r in result.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
