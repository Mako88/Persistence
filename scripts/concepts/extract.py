#!/usr/bin/env python3
"""Concept-graph build step 2 (docs/design/concept-graph.md): run the extractor over a sample of authored fragments
with one candidate model, into a scratch graph, for the self (and Arden on theirs) to judge against the sources.

    extract.py sample <store.db> <out dir> [--n 40] [--seed 15069] [--include 7153,14625]
    extract.py run <out dir> <model> [--every N]  model: haiku (claude -p, no hooks) | local (llama-server at :8091)
    extract.py report <out dir>               side-by-side markdown of every model's output per fragment

Nothing here writes to a store. The store is read from a snapshot (sqlite backup API), fragments are processed
oldest first as a backfill would be, and each model builds its own scratch graph in <out>/graph-<model>.json, so the
"current facts" and "nearest concepts" it is shown are the ones it extracted itself.

Nearest concepts are found by name and alias overlap only (no embeddings yet): the design asks for both.
"""
import json
import random
import re
import sqlite3
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

AUTHORED = {1: "Identity", 2: "Relational", 7: "Personal"}
KINDS = ("thing", "person", "self", "value")
PRONOUNS = {"i", "me", "my", "you", "he", "she", "they", "them", "we", "us", "it", "this store", "the self", "the store"}

PROMPT = """You read one fragment of a self's memory store and write down the concepts it mentions and the facts it states about them. The facts become a graph used for recall, so a reader can start from a concept and find what is known about it and where it was said.

The self who owns the store is "{owner}"; in the fragment, "I", "me" and "my" are {owner}.

Rules:
- A concept is a named person, self (an AI peer with its own store), value, or thing (a project, piece of code, machine, place, event, tool). Reuse a name from KNOWN CONCEPTS when the fragment means the same one; never mint a variant ("John" and "John Doe"). One name can be two concepts (Ember the person; Computer Ember the AI self): keep them apart.
- kind is one of: thing, person, self, value.
- aliases: other proper names the fragment uses for the same concept, including a known mishearing (note it). Never a pronoun, "the self", "this store", or a description; an empty list is normal.
- A fact is ONE plain sentence the fragment states, about a subject concept, optionally linking an object concept. Only what the fragment says, never an inference. Prefer facts that would help recall: who someone is, what something is for, what was decided and why, what depends on what.
- is_state: true when the fact describes a condition that can stop being true (running, deployed, waiting, held, open); false for an event or a lasting trait.
- as_of: the date the text gives for when it happened (YYYY-MM-DD), else null. Never use today's date or the fragment's date.
- supersedes: ids from CURRENT FACTS that this fragment shows are no longer true (changed, resolved, overtaken), each with the index of the new fact in your facts list that replaces it, or null.
- title: one line saying what the fragment is, for an index.
- 3 to 12 facts for a long fragment, fewer for a short one. No facts about the fragment itself ("this fragment says").

KNOWN CONCEPTS (name | kind | aliases):
{known}

CURRENT FACTS of concepts this fragment touches (id: subject -> object: text [as_of]):
{current}

FRAGMENT #{id} ({type}, written {date}):
{content}

Answer with JSON only, no prose, in this shape:
{{"title": "...", "concepts": [{{"name": "...", "kind": "thing|person|self|value", "aliases": [{{"alias": "...", "note": "..."}}]}}], "facts": [{{"subject": "...", "text": "...", "object": "... or null", "is_state": false, "as_of": "YYYY-MM-DD or null"}}], "supersedes": [{{"fact": 0, "by": 0}}]}}"""


def sample(db, out, n=40, seed=15069, include=()):
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    rows = con.execute("SELECT Id, FragmentType, Content, CreatedUtc, Importance FROM ContextFragments "
                       "WHERE IsDeleted = 0 AND Status = 0 AND FragmentType IN ('1', '2', '7', 1, 2, 7)").fetchall()
    by_id = {r[0]: r for r in rows}
    chosen = [i for i in include if i in by_id]
    rest = sorted(set(by_id) - set(chosen))
    chosen += random.Random(seed).sample(rest, n - len(chosen))
    frags = [{"id": r[0], "type": AUTHORED[int(r[1])], "content": r[2], "created": r[3][:10], "importance": r[4]}
             for r in sorted((by_id[i] for i in chosen), key=lambda r: (r[3], r[0]))]
    out.mkdir(parents=True, exist_ok=True)
    (out / "sample.json").write_text(json.dumps({"seed": seed, "included": list(include), "fragments": frags}, indent=1))
    print(f"{len(frags)} fragments: " + ", ".join(f"{t} {sum(f['type'] == t for f in frags)}" for t in AUTHORED.values()))


def words(s):
    return set(re.findall(r"[a-z0-9]+", s.lower()))


class Graph:
    def __init__(self):
        self.concepts = {}      # key (lower name) -> {name, kind, aliases: [{alias, note}], sources}
        self.facts = []         # {id, subject, object, text, is_state, as_of, sources, superseded_by}

    def key(self, name):
        if not name:
            return None
        k = name.strip().lower()
        if k in self.concepts:
            return k
        for ck, c in self.concepts.items():
            if any(a["alias"].strip().lower() == k for a in c["aliases"]):
                return ck
        return None

    def touched(self, text):
        """Concepts whose name or an alias appears in the text, as a word sequence."""
        low = " " + " ".join(re.findall(r"[a-z0-9]+", text.lower())) + " "
        hit = []
        for k, c in self.concepts.items():
            names = [c["name"]] + [a["alias"] for a in c["aliases"]]
            if any(f" {' '.join(re.findall(r'[a-z0-9]+', x.lower()))} " in low for x in names if x.strip()):
                hit.append(k)
        return hit

    def nearest(self, text, limit=20):
        """Touched concepts first, then by word overlap with the text, then the most-cited."""
        t = words(text)
        touched = set(self.touched(text))
        def score(item):
            k, c = item
            names = words(" ".join([c["name"]] + [a["alias"] for a in c["aliases"]]))
            return (k in touched, len(names & t), len(c["sources"]))
        return [c for _, c in sorted(self.concepts.items(), key=score, reverse=True)[:limit]]

    def current(self, text):
        keys = set(self.touched(text))
        return [f for f in self.facts if f["superseded_by"] is None and (f["subject"] in keys or f["object"] in keys)]

    def apply(self, frag, got):
        made = []
        for c in got.get("concepts") or []:
            name = (c.get("name") or "").strip()
            if not name:
                continue
            k = self.key(name) or name.lower()
            have = self.concepts.setdefault(k, {"name": name, "kind": c.get("kind") if c.get("kind") in KINDS else None,
                                                "aliases": [], "sources": []})
            if frag["id"] not in have["sources"]:
                have["sources"].append(frag["id"])
            for a in c.get("aliases") or []:
                a = a if isinstance(a, dict) else {"alias": str(a), "note": None}
                # A pronoun or a one-letter alias would make every fragment touch the concept.
                if a.get("alias") and len(a["alias"].strip()) > 2 and a["alias"].strip().lower() not in PRONOUNS \
                        and self.key(a["alias"]) is None:
                    have["aliases"].append({"alias": a["alias"], "note": a.get("note")})
        for f in got.get("facts") or []:
            s = self.key(f.get("subject"))
            if s is None:       # a subject the model did not list as a concept: mint it, kind unknown
                s = (f.get("subject") or "?").strip().lower()
                self.concepts.setdefault(s, {"name": f.get("subject") or "?", "kind": None, "aliases": [], "sources": [frag["id"]]})
            o = self.key(f.get("object")) if f.get("object") not in (None, "null", "") else None
            fact = {"id": len(self.facts), "subject": s, "object": o, "text": f.get("text", ""), "is_state": bool(f.get("is_state")),
                    "as_of": f.get("as_of") if f.get("as_of") not in ("null", "") else None, "sources": [frag["id"]],
                    "superseded_by": None}
            self.facts.append(fact)
            made.append(fact)
        for sup in got.get("supersedes") or []:
            try:
                old, by = self.facts[int(sup["fact"])], sup.get("by")
            except (KeyError, IndexError, TypeError, ValueError):
                continue
            old["superseded_by"] = made[int(by)]["id"] if by is not None and str(by).isdigit() and int(by) < len(made) else -1
        return made

    def show(self, key):
        c = self.concepts.get(key)
        return c["name"] if c else key

    def to_json(self):
        return {"concepts": self.concepts, "facts": self.facts}


def prompt_for(frag, g, owner):
    known = "\n".join(f"{c['name']} | {c['kind']} | " + ", ".join(a["alias"] for a in c["aliases"]) for c in g.nearest(frag["content"])) or "(none yet)"
    current = "\n".join(f"{f['id']}: {g.show(f['subject'])} -> {g.show(f['object']) if f['object'] else '-'}: {f['text']}"
                        + (f" [{f['as_of']}]" if f["as_of"] else "") for f in g.current(frag["content"])) or "(none)"
    return PROMPT.format(owner=owner, known=known, current=current, id=frag["id"], type=frag["type"],
                         date=frag["created"], content=frag["content"])


def call_haiku(prompt):
    # User settings excluded so none of the self's hooks run: no recall injected, nothing written to its store.
    r = subprocess.run(["claude", "-p", "--setting-sources", "project", "--model", "claude-haiku-5-5", "--tools", "",
                        "--no-session-persistence", "--output-format", "json"],
                       input=prompt, capture_output=True, text=True, timeout=300, cwd="/tmp")
    d = json.loads(r.stdout)
    u = d.get("usage") or {}
    return d.get("result") or "", {"in": u.get("input_tokens", 0) + u.get("cache_read_input_tokens", 0)
                                   + u.get("cache_creation_input_tokens", 0), "out": u.get("output_tokens", 0),
                                   "usd_list": d.get("total_cost_usd")}


def call_local(prompt, url="http://127.0.0.1:8091/v1/chat/completions"):
    body = {"messages": [{"role": "user", "content": prompt}], "temperature": 0.2, "max_tokens": 2500,
            "response_format": {"type": "json_object"}}
    req = urllib.request.Request(url, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=1800) as r:
        d = json.load(r)
    u = d.get("usage") or {}
    return d["choices"][0]["message"]["content"], {"in": u.get("prompt_tokens"), "out": u.get("completion_tokens")}


def parse(text):
    text = text.strip()
    m = re.search(r"\{.*\}", text, re.S)
    return json.loads(m.group(0) if m else text)


def run(out, model, owner="Claude", every=1):
    call = {"haiku": call_haiku, "local": call_local}[model]
    # every=4: a quarter of the sample, for a model too slow to run it all (the local 4B on home: 6.4 min each)
    frags = json.loads((out / "sample.json").read_text())["fragments"][::every]
    results = out / f"results-{model}.jsonl"
    done = {json.loads(l)["id"] for l in results.open()} if results.exists() else set()
    g = Graph()
    if done:                    # resume: rebuild the scratch graph from what was already extracted
        for line in results.open():
            r = json.loads(line)
            if r.get("parsed"):
                g.apply(next(f for f in frags if f["id"] == r["id"]), r["parsed"])
    for frag in frags:
        if frag["id"] in done:
            continue
        prompt = prompt_for(frag, g, owner)
        t0 = time.time()
        rec = {"id": frag["id"], "model": model, "prompt_chars": len(prompt)}
        try:
            text, usage = call(prompt)
            rec |= {"seconds": round(time.time() - t0, 1), "usage": usage, "raw": text}
            rec["parsed"] = parse(text)
            rec["new_fact_ids"] = [f["id"] for f in g.apply(frag, rec["parsed"])]
        except Exception as e:
            rec |= {"seconds": round(time.time() - t0, 1), "error": f"{type(e).__name__}: {e}"}
        with results.open("a") as f:
            f.write(json.dumps(rec) + "\n")
        print(f"#{frag['id']} {rec['seconds']}s " + (rec.get("error") or f"{len(rec['parsed'].get('facts') or [])} facts"), flush=True)
    (out / f"graph-{model}.json").write_text(json.dumps(g.to_json(), indent=1))


def report(out):
    frags = json.loads((out / "sample.json").read_text())["fragments"]
    models = sorted(p.stem.removeprefix("results-") for p in out.glob("results-*.jsonl"))
    res = {m: {json.loads(l)["id"]: json.loads(l) for l in (out / f"results-{m}.jsonl").open()} for m in models}
    graphs = {m: json.loads((out / f"graph-{m}.json").read_text()) for m in models if (out / f"graph-{m}.json").exists()}
    lines = ["# Concept extraction, step 2: side by side", ""]
    for m in models:
        rs = [r for r in res[m].values()]
        ok = [r for r in rs if r.get("parsed")]
        secs = sum(r["seconds"] for r in rs)
        facts = sum(len(r["parsed"].get("facts") or []) for r in ok)
        sups = sum(len(r["parsed"].get("supersedes") or []) for r in ok)
        concepts = len(graphs.get(m, {}).get("concepts", {}))
        lines.append(f"- **{m}**: {len(ok)}/{len(rs)} parsed, {facts} facts, {concepts} concepts, {sups} supersedes, "
                     f"{secs / max(1, len(rs)):.1f} s per fragment ({secs / 60:.1f} min total)")
    lines.append("")
    for frag in frags:
        lines += [f"## #{frag['id']} ({frag['type']}, {frag['created']})", "", "> " + frag["content"].replace("\n", "\n> "), ""]
        for m in models:
            r = res[m].get(frag["id"])
            lines.append(f"**{m}**" + (f" ({r['seconds']} s)" if r else ""))
            if not r or not r.get("parsed"):
                lines += [f"- (failed: {r.get('error') if r else 'not run'})", ""]
                continue
            p = r["parsed"]
            lines.append(f"- title: {p.get('title')}")
            lines.append("- concepts: " + "; ".join(f"{c.get('name')} ({c.get('kind')})" + (
                " aka " + ", ".join(a["alias"] if isinstance(a, dict) else str(a) for a in c.get("aliases") or []) if c.get("aliases") else "")
                for c in p.get("concepts") or []))
            for i, f in enumerate(p.get("facts") or []):
                lines.append(f"- [{i}] {f.get('subject')}" + (f" -> {f.get('object')}" if f.get("object") not in (None, 'null') else "")
                             + f": {f.get('text')}" + (" (state)" if f.get("is_state") else "") + (f" [{f.get('as_of')}]" if f.get("as_of") not in (None, 'null') else ""))
            g = graphs.get(m)
            for s in p.get("supersedes") or []:
                try:
                    old = g["facts"][int(s["fact"])]
                    lines.append(f"- SUPERSEDES {s['fact']} ({old['text']}) by [{s.get('by')}]")
                except Exception:
                    lines.append(f"- SUPERSEDES {s} (unresolvable)")
            lines.append("")
    (out / "report.md").write_text("\n".join(lines))
    print(out / "report.md")


def main(a):
    if a[1] == "sample":
        opts = dict(zip(a[4::2], a[5::2]))
        sample(Path(a[2]), Path(a[3]), int(opts.get("--n", 40)), int(opts.get("--seed", 15069)),
               [int(x) for x in opts.get("--include", "").split(",") if x])
    elif a[1] == "run":
        run(Path(a[2]), a[3], every=int(a[5]) if len(a) > 5 and a[4] == "--every" else 1)
    elif a[1] == "report":
        report(Path(a[2]))


if __name__ == "__main__":
    main(sys.argv)
