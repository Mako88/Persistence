#!/usr/bin/env python3
"""The recall benchmark: what a turn really gets from recall, scored on a fixed, labelled case set, so a change to
recall (a relevance floor, per-session dedup, skipping automated turns) can be shown to help or not.

    bench.py snapshot <live db> <out db>        a consistent read-only copy, with every utterance embedded
    bench.py run <labels.jsonl> --db <snapshot> [--path semantic|words] [--layer hook|store] [--counts 3,5]
                 [--exe <persistence-store>] --out <results.jsonl>
    bench.py report <results.jsonl>...          the numbers, as markdown: ids and counts, never fragment text
    bench.py unjudged <results.jsonl>...        per case, the shown ids no label has judged yet

What is measured. By default (`--layer hook`) each case goes through the real `hook.py recall`, the
UserPromptSubmit hook, imported from scripts/claude-code: its gate on automated turns, its exclusion of what the
context window already holds (the core at session start, then everything shown), its wander and its past
utterances (--said). Only the hook's way to the store is replaced: every store call it makes runs the CLI
(`--exe`) on the snapshot instead of the served store, and the hook's state goes to a private temp dir. So a fix
made in the hook or in the store shows here, as it would in a session; the live store and its state are never
touched. `--layer store` calls the store's `recall` alone, as the hook would, without the hook's gate or memory.

Paths (f): `semantic` runs with the PERSISTENCE_EMBEDDING_* the served store runs with (defaults below are my
store's live settings since 2026-11-11: bge-small on :8081, query tail 1200, quality weight 0; override from the
environment). `words` turns embedding off: the exact-words fallback the store uses when the embedder refuses a
long message, scored on its own.

The labels file (README.md, "Labels") holds the cases. Labels hold real text, so they stay outside the repo.
"""
import argparse
import importlib.util
import json
import os
import re
import sqlite3
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
HOOK = HERE.parent / "claude-code" / "hook.py"

EXE = os.environ.get("PERSISTENCE_STORE_EXE_BENCH", "/opt/persistence/store/persistence-store")
LIVE_ENV = {  # as persistence-store-arden.service runs (2026-11-12); each is overridden by the environment
    "PERSISTENCE_EMBEDDING_ENABLED": "true",
    "PERSISTENCE_EMBEDDING_APIBASEURL": "http://127.0.0.1:8081/v1",
    "PERSISTENCE_EMBEDDING_MODEL": "bge-small-en-v1.5",
    "PERSISTENCE_EMBEDDING_QUERYTAILCHARS": "1200",
    "PERSISTENCE_EMBEDDING_QUALITYWEIGHT": "0",
}
ID = re.compile(r"^\[#(\d+) \|", re.M)


def _eval():
    spec = importlib.util.spec_from_file_location("recall_eval", HERE / "eval.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def store_env(path):
    env = {k: os.environ.get(k, v) for k, v in LIVE_ENV.items()}
    if path == "words":
        env["PERSISTENCE_EMBEDDING_ENABLED"] = "false"
    return env


def cli(exe, db, args, stdin=None, env=None):
    """The CLI on the snapshot: (exit code, stdout, stderr). Never the served store, never the live file."""
    r = subprocess.run([exe, "--db", db, *args], input=stdin, capture_output=True, text=True, timeout=300,
                       env={**os.environ, **(env or {})})
    return r.returncode, r.stdout, r.stderr


# --- snapshot ---------------------------------------------------------------------------------------------------

def snapshot(live, out, exe):
    """A consistent copy (sqlite backup API, the live file opened read-only), then every past utterance embedded
    in the copy: recall embeds a few per call, so without this a case's result depends on the cases run before it."""
    _eval().snapshot(live, out)
    os.chmod(out, 0o600)
    code, so, se = cli(exe, out, ["embed-conversation"], env=store_env("semantic"))
    print((so or se).strip()[-300:], file=sys.stderr)


# --- one turn through the hook ----------------------------------------------------------------------------------

class HookTurns:
    """The real hook.py, imported once, its store calls bent to the CLI on a snapshot, its state in a temp dir."""

    def __init__(self, exe, path):
        self.state = tempfile.mkdtemp(prefix="recall-bench-state-")
        os.environ["PERSISTENCE_STATE_DIR"] = self.state      # before import: hook.STATE is read at import
        spec = importlib.util.spec_from_file_location("bench_hook", HOOK)
        h = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(h)
        # Belt and braces: nothing below may reach a served store, local or remote.
        h.SOCKET, h.PORT = None, 1
        h.remote = lambda: None
        h.keep_soon = lambda event, delay=5: None             # keeps the transcript into the store: not here
        h.cued = lambda event, moment, text: ""               # cues are a separate mechanism, not recall
        self.h, self.exe, self.env, self.db, self.project = h, exe, store_env(path), None, ""
        h.project = lambda event: self.project

        def call_store(*args, timeout=60, stdin=None, serve=True, cold=True, quiet=False):
            return cli(self.exe, self.db, [str(a) for a in args], stdin=stdin, env=self.env)

        def store(*args, timeout=60, stdin=None, serve=True, cold=True, quiet=False):
            code, out, _ = call_store(*args, stdin=stdin)
            return out if code == 0 else ""
        h.call_store, h.store = call_store, store

    def start(self, session, db, project, as_of):
        """A new context window, as session-start leaves it: the core's ids are already in front of the peer, and
        what was said from `as_of` on is this session's own."""
        self.db, self.project = db, project
        core = self.h.store("core", "--project", project, "--session", session)
        held = ID.findall(core)
        self.h.seen({"session_id": session}, reset=True, add=held)
        if as_of:
            (Path(self.state) / f"{session}.since").write_text(as_of.replace(" ", "T"))
        return held

    def turn(self, session, prompt, count):
        import contextlib
        import io
        os.environ["PERSISTENCE_RECALL_COUNT"] = str(count)
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            self.h.recall({"session_id": session, "prompt": prompt, "cwd": os.getcwd()})
        out = buf.getvalue().strip()
        if not out:
            return [], 0, None
        ctx = json.loads(out)["hookSpecificOutput"]["additionalContext"]
        recalled = ctx if ctx.startswith(("Recalled", "Cued")) else ""
        return [int(i) for i in ID.findall(recalled)], len(ctx), (None if recalled else ctx[:160])


def store_turn(exe, db, env, prompt, project, count, exclude):
    """`--layer store`: the store's recall as the hook calls it, without the hook's gate or memory."""
    code, out, err = cli(exe, db, ["recall", "--text", "-", "--limit", str(count), "--snippet", "300",
                                   "--project", project, "--wander", os.environ.get("PERSISTENCE_RECALL_WANDER", "1"),
                                   "--said", os.environ.get("PERSISTENCE_RECALL_SAID", "3") if len(prompt) >= 40 else "0",
                                   *(["--exclude", ",".join(map(str, exclude))] if exclude else [])],
                         stdin=prompt, env=env)
    if code != 0:
        raise RuntimeError(err[-400:])
    return [int(i) for i in ID.findall(out)], len(out.strip()), None


# --- cases ------------------------------------------------------------------------------------------------------

def load_labels(path):
    config, cases = {}, []
    for line in open(path, encoding="utf-8"):
        if not line.strip() or line.lstrip().startswith("//"):
            continue
        d = json.loads(line)
        if "config" in d:
            config.update(d["config"])
        else:
            cases.append(d)
    return config, cases


def fragment(db, fid):
    c = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        return c.execute("SELECT Content, CreatedUtc FROM ContextFragments WHERE Id=?", (fid,)).fetchone()
    finally:
        c.close()


def resolve(turn, snap):
    """A turn's text and when it was asked: given inline, or read from the utterance `query_from` in the snapshot
    (then `as_of` defaults to a second before it was kept, so the store never offers the message to itself)."""
    if "query" in turn:
        return turn["query"], turn.get("as_of")
    content, created = fragment(snap, turn["query_from"])
    from datetime import datetime, timedelta
    when = datetime.fromisoformat(created.replace("Z", "").replace(" ", "T")[:19]) - timedelta(seconds=1)
    return content, turn.get("as_of") or when.isoformat() + "Z"


def active(db, ids):
    """Of `ids`, those active in `db` (the store as of the question): gold that wasn't there yet can't be reached."""
    if not ids:
        return set()
    c = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        q = f"SELECT Id FROM ContextFragments WHERE Status=0 AND IsDeleted=0 AND Id IN ({','.join('?' * len(ids))})"
        return {r[0] for r in c.execute(q, list(ids))}
    finally:
        c.close()


def heads(db):
    c = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        return {r[0] for r in c.execute("""SELECT e.EntityId FROM EntityTags e JOIN Tags t ON t.Id=e.TagId
                                            WHERE t.Name='head' AND e.EntityType LIKE '%Fragment%'""")}
    finally:
        c.close()


def run(labels, snap, path, layer, counts, exe, out):
    config, cases = load_labels(labels)
    ev = _eval()
    hook = HookTurns(exe, path) if layer == "hook" else None
    env = store_env(path)
    rows = []
    for count in counts:
        for n, case in enumerate(cases):
            kind = case.get("kind", "gold")
            project = case.get("project", config.get("project", ""))
            turns = case["turns"] if kind == "session" else [case]
            first_q, first_asof = resolve(turns[0], snap)
            as_of = case.get("as_of") or first_asof
            db = ev.as_of_db(snap, as_of) if as_of else snap
            session = f"bench-{path}-{count}-{n}"
            held = hook.start(session, db, project, as_of) if hook else []
            shown_before, exclude = set(), set()
            for t, turn in enumerate(turns):
                q, _ = resolve(turn, snap) if t else (first_q, first_asof)
                if turn.get("compacted") and t:
                    # A compaction before this turn: the hook starts a new context window (the core again, its
                    # memory of what was shown reset), as session-start does after every compaction.
                    held = hook.start(session, db, project, as_of) if hook else []
                    exclude = set()
                if hook:
                    ids, chars, notice = hook.turn(session, q, count)
                else:
                    ids, chars, notice = store_turn(exe, db, env, q, project, count, exclude)
                    exclude.update(ids)
                gold = set(turn.get("gold", []))
                ok = set(turn.get("ok", [])) | gold
                judged = ok | set(turn.get("noise", []))
                row = {"case": case["id"] + (f"#{t + 1}" if kind == "session" else ""), "kind": kind, "path": path,
                       "layer": layer, "count": count, "shown": ids, "chars": chars, "notice": notice,
                       "gold": sorted(gold), "gold_in_core": sorted(gold & set(map(int, held))),
                       "gold_absent": sorted(gold - active(db, gold)),
                       "relevant": [i for i in ids if i in ok], "unjudged": [i for i in ids if i not in judged],
                       "repeats": [i for i in ids if i in shown_before], "heads": [i for i in ids if i in heads_of(db)]}
                live_gold = gold - set(row["gold_in_core"]) - set(row["gold_absent"])
                row["hit"] = bool(live_gold & set(ids)) if live_gold else None
                shown_before.update(ids)
                rows.append(row)
                mark = "-" if row["hit"] is None else ("HIT" if row["hit"] else "miss")
                print(f"{path:<8} @{count} {row['case']:<22} {kind:<9} {mark:<4} shown {ids} chars {chars}"
                      + (f" [{notice[:60]}]" if notice else ""), flush=True)
    with open(out, "a", encoding="utf-8") as f:
        for r in rows:
            f.write(json.dumps(r) + "\n")
    return rows


_heads = {}


def heads_of(db):
    if db not in _heads:
        _heads[db] = heads(db)
    return _heads[db]


# --- report -----------------------------------------------------------------------------------------------------

def pct(a, b):
    return f"{a}/{b} ({100 * a / b:.0f}%)" if b else "-"


def report(paths, watch=(8, 3, 145, 832, 22, 26)):
    rows = [json.loads(l) for p in paths for l in open(p, encoding="utf-8") if l.strip()]
    arms = sorted({(r["path"], r["layer"]) for r in rows})
    out = []
    for path, layer in arms:
        R = [r for r in rows if r["path"] == path and r["layer"] == layer]
        counts = sorted({r["count"] for r in R})
        out.append(f"## {path} path, {layer} layer\n")
        out.append("| metric | " + " | ".join(f"@{c}" for c in counts) + " |")
        out.append("|---|" + "---|" * len(counts))

        def line(name, f):
            out.append(f"| {name} | " + " | ".join(f([r for r in R if r["count"] == c]) for c in counts) + " |")

        gold = lambda X: [r for r in X if r["kind"] == "gold"]
        scored = lambda X: [r for r in gold(X) if r["hit"] is not None]
        line("gold cases: gold reached", lambda X: pct(sum(r["hit"] for r in scored(X)), len(scored(X))))
        line("gold cases: not scored (gold already in core)", lambda X: str(sum(1 for r in gold(X) if r["hit"] is None
                                                                            and r["gold_in_core"])))
        line("gold cases: LABEL ERROR (gold not active at as_of)", lambda X: str(sum(1 for r in gold(X) if r["gold_absent"])))
        line("gold cases: precision (judged relevant / shown, a floor while any are unjudged)", lambda X: pct(sum(len(r["relevant"]) for r in gold(X)),
                                                                     sum(len(r["shown"]) for r in gold(X))))
        line("gold cases: shown but not yet judged", lambda X: str(sum(len(r["unjudged"]) for r in gold(X))))
        line("gold cases: chars shown", lambda X: str(sum(r["chars"] for r in gold(X))))
        none = lambda X: [r for r in X if r["kind"] == "none"]
        line("nothing-relevant cases: showed anything", lambda X: pct(sum(bool(r["shown"]) for r in none(X)), len(none(X))))
        line("nothing-relevant cases: items shown", lambda X: str(sum(len(r["shown"]) for r in none(X))))
        line("nothing-relevant cases: chars shown", lambda X: str(sum(r["chars"] for r in none(X))))
        auto = lambda X: [r for r in X if r["kind"] == "automated"]
        line("automated turns: recall fired", lambda X: pct(sum(bool(r["shown"]) for r in auto(X)), len(auto(X))))
        line("automated turns: items shown", lambda X: str(sum(len(r["shown"]) for r in auto(X))))
        line("automated turns: chars shown", lambda X: str(sum(r["chars"] for r in auto(X))))
        sess = lambda X: [r for r in X if r["kind"] == "session"]
        line("session: turns", lambda X: str(len(sess(X))))
        line("session: items shown", lambda X: str(sum(len(r["shown"]) for r in sess(X))))
        line("session: repeats (shown on an earlier turn)", lambda X: str(sum(len(r["repeats"]) for r in sess(X))))
        line("session: relevant / shown (judged turns only)",
             lambda X: pct(sum(len(r["relevant"]) for r in sess(X) if r["shown"] and not r["unjudged"]),
                           sum(len(r["shown"]) for r in sess(X) if r["shown"] and not r["unjudged"]))
             if any(r["shown"] and not r["unjudged"] for r in sess(X)) else "unlabelled")
        line("session: chars shown", lambda X: str(sum(r["chars"] for r in sess(X))))
        single = lambda X: [r for r in X if r["kind"] in ("gold", "none")]
        line("head share: group heads / items shown (gold+none)",
             lambda X: pct(sum(len(r["heads"]) for r in single(X)), sum(len(r["shown"]) for r in single(X))))
        line(f"head share: watched {', '.join('#' + str(w) for w in watch)} / items shown",
             lambda X: pct(sum(1 for r in single(X) for i in r["shown"] if i in watch), sum(len(r["shown"]) for r in single(X))))
        out.append("")
        for c in counts:
            X = single([r for r in R if r["count"] == c])
            often = Counter(i for r in X for i in set(r["shown"])).most_common(8)
            out.append(f"Most often shown @{c} over {len(X)} single prompts: "
                       + ", ".join(f"#{i} on {k}" for i, k in often if k > 1))
        out.append("")
    return "\n".join(out)


def unjudged(paths):
    """Per case, the shown ids no label judges yet: add each to the case's `ok` or `noise` and the precision line
    stops being a lower bound. Ids only."""
    rows = [json.loads(l) for p in paths for l in open(p, encoding="utf-8") if l.strip()]
    todo = {}
    for r in rows:
        todo.setdefault(r["case"], set()).update(r["unjudged"])
    for case, ids in todo.items():
        if ids:
            print(f"{case}: {', '.join(map(str, sorted(ids)))}")


def main(argv):
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("snapshot")
    s.add_argument("live")
    s.add_argument("out")
    s.add_argument("--exe", default=EXE)
    r = sub.add_parser("run")
    r.add_argument("labels")
    r.add_argument("--db", required=True)
    r.add_argument("--path", choices=["semantic", "words"], default="semantic")
    r.add_argument("--layer", choices=["hook", "store"], default="hook")
    r.add_argument("--counts", default="3,5")
    r.add_argument("--exe", default=EXE)
    r.add_argument("--out", required=True)
    g = sub.add_parser("report")
    g.add_argument("results", nargs="+")
    g.add_argument("--watch", default="8,3,145,832,22,26")
    u = sub.add_parser("unjudged")
    u.add_argument("results", nargs="+")
    a = p.parse_args(argv)
    if a.cmd == "snapshot":
        snapshot(a.live, a.out, a.exe)
    elif a.cmd == "run":
        run(a.labels, a.db, a.path, a.layer, [int(c) for c in a.counts.split(",")], a.exe, a.out)
    elif a.cmd == "unjudged":
        unjudged(a.results)
    else:
        print(report(a.results, [int(w) for w in a.watch.split(",") if w]))


if __name__ == "__main__":
    main(sys.argv[1:])
