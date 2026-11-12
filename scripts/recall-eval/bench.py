#!/usr/bin/env python3
"""The recall benchmark: what a turn really gets from recall, scored on a fixed, labelled case set, so a change to
recall (a relevance floor, per-session dedup, skipping automated turns) can be shown to help or not.

    bench.py snapshot <live db> <out db>        a consistent read-only copy, with every utterance embedded
    bench.py run <labels.jsonl> --db <snapshot> [--path semantic|words] [--layer hook|store] [--counts 3,5]
                 [--exe <persistence-store>] --out <results.jsonl>
    bench.py report <results.jsonl>... [--labels <labels.jsonl>] [--watch <id,id>]
                                                the numbers, as markdown: ids and counts, never fragment text
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

Which path a turn ACTUALLY took is recorded per turn (`actual`), from the store's own account of that recall
(`recall --diagnostics`, one `recall-path:` line on stderr): `semantic`, `words-setting` (embedding off),
`words-fallback` (meaning asked for, not used; with why), `unknown` (the store gave no account: an older exe without
--diagnostics, or none seen), or `none` (the hook made no recall, as on an automated turn). Never inferred from the prompt's length or the arm's settings (Ember, 2026-11-12). The report
scores each arm only over turns whose actual path is the arm's (or none), and shows the rest as its own numbers.

Isolation. Recall writes as it reads (LastAccessedUtc on every fragment it hydrates; vectors it backfills for
fragments the as-of rebuild rolled back). So every case, and every turn of an evolving session, runs on its own
throwaway copy of a pristine, read-only base (the snapshot, or the snapshot as of a time), and nothing one arm or
case does reaches another. The bases are built in a private work dir, never beside the snapshot, and the snapshot's
hash is checked unchanged at the end.

Sessions. A `session` case runs twice: `evolving`, the store rebuilt as of each turn (what was kept between turns is
there), the hook's memory of the window carried across turns, under the real session's id when the turns are its
utterances (so the store's rule that a session's own words after it started aren't "said before" applies as it did);
and `fixed`, every turn against the store as of the first turn: a repeat test of the same queries on fixed state.

The labels file (README.md, "Labels") holds the cases. Labels hold real text, so they stay outside the repo.
"""
import argparse
import importlib.util
import json
import os
import hashlib
import re
import shutil
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

    def __init__(self, exe, path, diagnostics):
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
        self.diagnostics = diagnostics
        h.project = lambda event: self.project

        self.paths = []                                         # the store's account of each recall this turn

        self.errors = []                                        # store calls that failed: the run is void

        def call_store(*args, timeout=60, stdin=None, serve=True, cold=True, quiet=False):
            args = [str(a) for a in args]
            recall = bool(args) and args[0] == "recall"
            code, out, err = cli(self.exe, self.db, [*args, *(["--diagnostics"] if recall and self.diagnostics else [])],
                                 stdin=stdin, env=self.env)
            if recall:
                self.paths.append(path_of(err) if self.diagnostics else ("unknown", "store gave no account (no --diagnostics)"))
            if code != 0 and args and args[0] in ("recall", "core"):
                # The hook reads a failed call as "nothing" (so a live turn goes on); here it would score as recall
                # showing nothing, silently (a full disk did exactly that, 2026-11-12). Never a number: stop the run.
                self.errors.append(f"{args[0]} exited {code}: {err.strip()[-300:]}")
            return code, out, err

        def store(*args, timeout=60, stdin=None, serve=True, cold=True, quiet=False):
            code, out, _ = call_store(*args, stdin=stdin)
            return out if code == 0 else ""
        h.call_store, h.store = call_store, store

    def check(self):
        if self.errors:
            raise RuntimeError("a store call failed, so this run measures nothing: " + "; ".join(self.errors))

    def start(self, session, db, project, as_of):
        """A new context window, as session-start leaves it: the core's ids are already in front of the peer, and
        what was said from `as_of` on is this session's own."""
        self.db, self.project = db, project
        core = self.h.store("core", "--project", project, "--session", session)
        self.check()
        held = ID.findall(core)
        self.h.seen({"session_id": session}, reset=True, add=held)
        if as_of:
            (Path(self.state) / f"{session}.since").write_text(as_of.replace(" ", "T"))
        return held

    def turn(self, session, prompt, count):
        import contextlib
        import io
        os.environ["PERSISTENCE_RECALL_COUNT"] = str(count)
        self.paths, self.said_words = [], False
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            self.h.recall({"session_id": session, "prompt": prompt, "cwd": os.getcwd()})
        out = buf.getvalue().strip()
        self.check()
        # The turn's path is its first recall's: a second (the hook asking again without its exclusion, to say
        # "all already in this window") is the same text, so the same path, and shows nothing new.
        actual = self.paths[0] if self.paths else ("none", None)
        if not out:
            return [], 0, None, actual
        ctx = json.loads(out)["hookSpecificOutput"]["additionalContext"]
        # What the peer was told: the store's words-only notice, kept even when items were shown (Claude's review).
        self.said_words = "Recall ran on exact words alone" in ctx
        recalled = ctx if ctx.startswith(("Recalled", "Cued")) else ""
        return [int(i) for i in ID.findall(recalled)], len(ctx), (None if recalled else ctx[:160]), actual


def store_turn(exe, db, env, prompt, project, count, exclude, diagnostics):
    """`--layer store`: the store's recall as the hook calls it, without the hook's gate or memory."""
    code, out, err = cli(exe, db, ["recall", "--text", "-", "--limit", str(count), "--snippet", "300",
                                   "--project", project, "--wander", os.environ.get("PERSISTENCE_RECALL_WANDER", "1"),
                                   "--said", os.environ.get("PERSISTENCE_RECALL_SAID", "3") if len(prompt) >= 40 else "0",
                                   *(["--exclude", ",".join(map(str, exclude))] if exclude else []),
                                   *(["--diagnostics"] if diagnostics else [])],
                         stdin=prompt, env=env)
    if code != 0:
        raise RuntimeError(err[-400:])
    actual = path_of(err) if diagnostics else ("unknown", "store gave no account (no --diagnostics)")
    return [int(i) for i in ID.findall(out)], len(out.strip()), None, actual


PATH_LINE = re.compile(r"^recall-path: (semantic|words-setting|words-fallback|unknown)(?: \((.*)\))?\s*$", re.M)
EXPECTED = {"semantic": "semantic", "words": "words-setting"}   # the actual path each arm asks for


def path_of(stderr):
    """The store's own account of the path one recall took, from its `recall-path:` line; unknown without one."""
    m = PATH_LINE.search(stderr or "")
    return (m.group(1), m.group(2)) if m else ("unknown", "no recall-path line from the store")


def has_diagnostics(exe, db):
    """Whether this store exe gives an account of its path: an older one refuses the option as unknown."""
    probe = tempfile.mkdtemp(prefix="recall-bench-probe-")
    try:
        copy = fresh(db, probe)
        code, _, err = cli(exe, copy, ["recall", "--text", "-", "--limit", "1", "--diagnostics"], stdin="probe",
                           env=store_env("words"))
        return code == 0 and bool(PATH_LINE.search(err))
    finally:
        shutil.rmtree(probe, ignore_errors=True)


def settings_of(exe, path, layer, diagnostics, labels):
    """The settings in effect, each with where it came from, for the top of the report: a default left in place
    (my store's settings) can hide a finding, as query tail 1200 would have hidden Claude's fallback rate."""
    env = store_env(path)
    knobs = {k: (env[k], "env" if k in os.environ else "bench default") for k in LIVE_ENV}
    for k, v in (("PERSISTENCE_RECALL_WANDER", "1"), ("PERSISTENCE_RECALL_SAID", "3")):
        knobs[k] = (os.environ.get(k, v), "env" if k in os.environ else "hook default")
    return {"exe": exe, "exe_sha256": sha256(exe)[:16], "diagnostics": diagnostics, "layer": layer,
            "labels_sha256": sha256(labels)[:16], "knobs": knobs}


# --- isolation --------------------------------------------------------------------------------------------------

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


class Bases:
    """Pristine read-only bases in a private work dir: the snapshot itself, and the snapshot as of each time asked.
    The store is never given one: it gets `fresh` copies, so nothing it writes outlives one case or turn."""

    def __init__(self, snap, work):
        self.snap, self.work, self.built = snap, work, {}
        self.link = os.path.join(work, "snapshot.db")   # as_of_db writes beside its input: beside this, in `work`
        os.symlink(os.path.abspath(snap), self.link)

    def of(self, ev, as_of):
        if not as_of:
            return self.snap
        if as_of not in self.built:
            base = ev.as_of_db(self.link, as_of)
            os.chmod(base, 0o400)
            self.built[as_of] = base
        return self.built[as_of]


def fresh(base, work):
    """A throwaway copy of `base` for one case or turn (0600, in `work`)."""
    fd, path = tempfile.mkstemp(prefix="turn-", suffix=".db", dir=work)
    os.close(fd)
    shutil.copyfile(base, path)
    os.chmod(path, 0o600)
    return path


def drop(path):
    for p in (path, path + "-wal", path + "-shm", path + "-journal"):
        try:
            os.remove(p)
        except FileNotFoundError:
            pass


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
        return c.execute("SELECT Content, CreatedUtc, SessionId FROM ContextFragments WHERE Id=?", (fid,)).fetchone()
    finally:
        c.close()


def resolve(turn, snap):
    """A turn's text, when it was asked, and the session that asked it: given inline (no session), or read from the
    utterance `query_from` in the snapshot (then `as_of` defaults to a second before it was kept, so the store never
    offers the message to itself)."""
    if "query" in turn:
        return turn["query"], turn.get("as_of"), None
    content, created, session = fragment(snap, turn["query_from"])
    from datetime import datetime, timedelta
    when = datetime.fromisoformat(created.replace("Z", "").replace(" ", "T")[:19]) - timedelta(seconds=1)
    return content, turn.get("as_of") or when.isoformat() + "Z", session


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


def run(labels, snap, path, layer, counts, exe, out, modes=("evolving", "fixed")):
    config, cases = load_labels(labels)
    ev = _eval()
    before = sha256(snap)
    work = tempfile.mkdtemp(prefix="recall-bench-work-")
    try:
        bases = Bases(snap, work)
        diagnostics = has_diagnostics(exe, snap)
        if not diagnostics:
            print("(this store exe gives no account of its path: every turn's actual path is 'unknown')", file=sys.stderr)
        hook = HookTurns(exe, path, diagnostics) if layer == "hook" else None
        settings = settings_of(exe, path, layer, diagnostics, labels)
        env = store_env(path)
        rows = []
        for count in counts:
            for n, case in enumerate(cases):
                kind = case.get("kind", "gold")
                for mode in (modes if kind == "session" else (None,)):
                    rows += run_case(ev, bases, work, hook, env, exe, diagnostics, config, case, n, kind, mode, path,
                                     layer, count)
        if sha256(snap) != before:
            raise RuntimeError(f"the snapshot {snap} changed during the run: results are not to be trusted")
    finally:
        shutil.rmtree(work, ignore_errors=True)
        if hook:
            shutil.rmtree(hook.state, ignore_errors=True)
    with open(out, "a", encoding="utf-8") as f:
        for r in rows:
            f.write(json.dumps({**r, "snapshot_sha256": before, "settings": settings}) + "\n")
    return rows


def run_case(ev, bases, work, hook, env, exe, diagnostics, config, case, n, kind, mode, path, layer, count):
    """One case (one turn, or a session's turns), each turn on a fresh copy: of the store as of that turn
    (`evolving`) or as of the first (`fixed`, and every single-turn case)."""
    project = case.get("project", config.get("project", ""))
    turns = case["turns"] if kind == "session" else [case]
    asked = [resolve(t, bases.snap) for t in turns]
    first_asof = case.get("as_of") or asked[0][1]
    real = asked[0][2] if mode == "evolving" and len({a[2] for a in asked}) == 1 else None
    # Under the real session's id when every turn is that session's: the store then holds its own words after it
    # started out of "said before", as it did live. Otherwise (fixed, inline queries) a bench id, as the baseline.
    session = real or f"bench-{path}-{count}-{n}" + (f"-{mode}" if mode else "")
    since = first_asof
    rows, shown_before, exclude, held, db = [], set(), set(), [], None
    try:
        for t, (turn, (q, turn_asof, _)) in enumerate(zip(turns, asked)):
            as_of = (turn_asof or first_asof) if mode == "evolving" else first_asof
            if db is None or mode == "evolving":
                if db:
                    drop(db)
                base = bases.of(ev, as_of)
                db = fresh(base, work)
            if t == 0 or turn.get("compacted"):
                # A new context window, as session-start leaves it (and after a compaction: the core again, the
                # hook's memory of what was shown reset).
                held = hook.start(session, db, project, since) if hook else []
                exclude = set()
            elif hook:
                hook.db = db
            if hook:
                ids, chars, notice, actual = hook.turn(session, q, count)
                said_words = hook.said_words
            else:
                ids, chars, notice, actual = store_turn(exe, db, env, q, project, count, exclude, diagnostics)
                exclude.update(ids)
                said_words = None
            gold = set(turn.get("gold", []))
            ok = set(turn.get("ok", [])) | gold
            judged = ok | set(turn.get("noise", []))
            row = {"case": case["id"] + (f"#{t + 1}" if kind == "session" else ""), "kind": kind, "path": path,
                   "layer": layer, "count": count, "session_store": mode, "as_of": as_of,
                   "actual": actual[0], "actual_why": actual[1], "prompt_chars": len(q),
                   "synthetic": case.get("synthetic", "query" in turn),
                   "shown": ids, "chars": chars, "notice": notice, "words_notice": said_words,
                   "held": sorted(map(int, held)),
                   "gold": sorted(gold), "gold_in_core": sorted(gold & set(map(int, held))),
                   "gold_absent": sorted(gold - active(base, gold)),
                   "relevant": [i for i in ids if i in ok], "unjudged": [i for i in ids if i not in judged],
                   "repeats": [i for i in ids if i in shown_before], "heads": [i for i in ids if i in heads_of(base)]}
            live_gold = gold - set(row["gold_in_core"]) - set(row["gold_absent"])
            row["hit"] = bool(live_gold & set(ids)) if live_gold else None
            shown_before.update(ids)
            rows.append(row)
            mark = "-" if row["hit"] is None else ("HIT" if row["hit"] else "miss")
            print(f"{path:<8} @{count} {row['case']:<22} {kind:<9}{(' ' + mode) if mode else ''} {mark:<4} "
                  f"path {actual[0]:<8} shown {ids} chars {chars}" + (f" [{notice[:60]}]" if notice else ""), flush=True)
    finally:
        if db:
            drop(db)
    return rows


_heads = {}


def heads_of(db):
    if db not in _heads:
        _heads[db] = heads(db)
    return _heads[db]


# --- report -----------------------------------------------------------------------------------------------------

def pct(a, b):
    return f"{a}/{b} ({100 * a / b:.0f}%)" if b else "-"


def rejudge(rows, labels):
    """Judgments from the labels file now, not as they were at run time: relabelling needs no rerun (Claude's review).
    Gold not active at as_of is still as the run found it: a new gold id needs a rerun for that check."""
    _, cases = load_labels(labels)
    by = {}
    for c in cases:
        for t, turn in enumerate(c["turns"] if c.get("kind") == "session" else [c]):
            by[c["id"] + (f"#{t + 1}" if c.get("kind") == "session" else "")] = turn
    for r in rows:
        turn = by.get(r["case"])
        if turn is None:
            continue
        gold = set(turn.get("gold", []))
        ok = set(turn.get("ok", [])) | gold
        judged = ok | set(turn.get("noise", []))
        r["gold"], r["gold_in_core"] = sorted(gold), sorted(gold & set(r.get("held", [])))
        r["relevant"] = [i for i in r["shown"] if i in ok]
        r["unjudged"] = [i for i in r["shown"] if i not in judged]
        live = gold - set(r["gold_in_core"]) - set(r["gold_absent"])
        r["hit"] = bool(live & set(r["shown"])) if live else None
    return rows


def report(paths, watch=(), labels=None):
    rows = [json.loads(l) for p in paths for l in open(p, encoding="utf-8") if l.strip()]
    if labels:
        rows = rejudge(rows, labels)
    arms = sorted({(r["path"], r["layer"]) for r in rows})
    out = []
    for path, layer in arms:
        everything = [r for r in rows if r["path"] == path and r["layer"] == layer]
        counts = sorted({r["count"] for r in everything})
        out.append(f"## {path} arm, {layer} layer\n")
        # The settings in effect, first: a default left in place can hide a finding (Claude's review).
        for st in sorted({json.dumps(r.get("settings"), sort_keys=True) for r in everything}):
            st = json.loads(st)
            if not st:
                out.append("Settings: not recorded (results from before 2026-11-12's revision).\n")
                continue
            knobs = ", ".join(f"{k.replace('PERSISTENCE_', '')}={v} ({src})" for k, (v, src) in sorted(st["knobs"].items()))
            out.append(f"Settings: exe {st['exe']} (sha256 {st['exe_sha256']}…, path account: "
                       f"{'yes' if st['diagnostics'] else 'NO, every path unknown'}), labels sha256 {st['labels_sha256']}…; "
                       f"{knobs}.\n")
        snaps = sorted({r.get("snapshot_sha256", "?")[:16] for r in everything})
        out.append(f"Snapshot sha256: {', '.join(s_ + '…' for s_ in snaps)}" + ("" if not labels else f"; judged by {labels}") + "\n")
        out.append("| metric | " + " | ".join(f"@{c}" for c in counts) + " |")
        out.append("|---|" + "---|" * len(counts))

        def line(name, f, X):
            out.append(f"| {name} | " + " | ".join(f([r for r in X if r["count"] == c]) for c in counts) + " |")

        # The path each turn actually took, by the store's own account (results without one: unknown).
        actual = lambda r: r.get("actual", "unknown")
        ran = lambda X: [r for r in X if actual(r) != "none"]
        for p_ in ("semantic", "words-setting", "words-fallback", "unknown"):
            line(f"actual path {p_} / turns recall ran", lambda X, p_=p_: pct(sum(actual(r) == p_ for r in ran(X)), len(ran(X))), everything)
        line("actual path none (the hook made no recall)", lambda X: str(sum(actual(r) == "none" for r in X)), everything)
        if path == "semantic":
            line("FALL-BACK RATE: words-fallback / turns recall ran", lambda X: pct(sum(actual(r) == "words-fallback" for r in ran(X)), len(ran(X))), everything)
            line("  of the fall-backs, prompts over 1,200 chars", lambda X: pct(
                sum(actual(r) == "words-fallback" and r.get("prompt_chars", 0) > 1200 for r in ran(X)),
                sum(actual(r) == "words-fallback" for r in ran(X))), everything)
        line("turns where the peer was told recall ran on words alone", lambda X: str(sum(bool(r.get("words_notice")) for r in X)), everything)
        # Every number below is over the turns that took this arm's path (or made no recall); the rest are left
        # out, never counted as this arm's, so a floor is never tuned on another path's results.
        want = EXPECTED.get(path, path)
        R = [r for r in everything if actual(r) in (want, "none")]
        line(f"turns left out of what follows (actual path not {want})", lambda X: str(len(X)), [r for r in everything if r not in R])

        for label, pick in (("", lambda X: X), (" (real prompts)", lambda X: [r for r in X if not r.get("synthetic")]),
                            (" (synthetic prompts)", lambda X: [r for r in X if r.get("synthetic")])):
            gold = lambda X, pick=pick: [r for r in pick(X) if r["kind"] == "gold"]
            if not any(gold(R)) and label:
                continue
            scored = lambda X, gold=gold: [r for r in gold(X) if r["hit"] is not None]
            line(f"gold cases{label}: gold reached", lambda X, scored=scored: pct(sum(r["hit"] for r in scored(X)), len(scored(X))), R)
            line(f"gold cases{label}: precision (judged relevant / shown, a floor while any are unjudged)",
                 lambda X, gold=gold: pct(sum(len(r["relevant"]) for r in gold(X)), sum(len(r["shown"]) for r in gold(X))), R)
        gold = lambda X: [r for r in X if r["kind"] == "gold"]
        line("gold cases: not scored (gold already in core)", lambda X: str(sum(1 for r in gold(X) if r["hit"] is None and r["gold_in_core"])), R)
        line("gold cases: LABEL ERROR (gold not active at as_of)", lambda X: str(sum(1 for r in gold(X) if r["gold_absent"])), R)
        line("gold cases: shown but not yet judged", lambda X: str(sum(len(r["unjudged"]) for r in gold(X))), R)
        line("gold cases: chars shown", lambda X: str(sum(r["chars"] for r in gold(X))), R)
        none = lambda X: [r for r in X if r["kind"] == "none"]
        line("nothing-relevant cases: showed anything", lambda X: pct(sum(bool(r["shown"]) for r in none(X)), len(none(X))), R)
        line("nothing-relevant cases: of them synthetic prompts", lambda X: f"{sum(bool(r.get('synthetic')) for r in none(X))}/{len(none(X))}", R)
        line("nothing-relevant cases: items shown", lambda X: str(sum(len(r["shown"]) for r in none(X))), R)
        line("nothing-relevant cases: chars shown", lambda X: str(sum(r["chars"] for r in none(X))), R)
        auto = lambda X: [r for r in X if r["kind"] == "automated"]
        line("automated turns: recall fired (a regression test: should stay 0)", lambda X: pct(sum(bool(r["shown"]) for r in auto(X)), len(auto(X))), R)
        line("automated turns: chars shown", lambda X: str(sum(r["chars"] for r in auto(X))), R)
        for mode, title in (("evolving", "session, evolving store"), ("fixed", "session, fixed store (repeat test)"),
                            (None, "session (store mode not recorded)")):
            sess = lambda X, mode=mode: [r for r in X if r["kind"] == "session" and r.get("session_store") == mode]
            if not sess(R):
                continue
            # Repeats split at the compaction: within one window the hook's own exclusion should hold them at 0;
            # the dedup fix is aimed at the window after a compaction (Ember, Claude, 2026-11-12).
            plain = lambda X, sess=sess: [r for r in sess(X) if "compacted" not in r["case"]]
            comp = lambda X, sess=sess: [r for r in sess(X) if "compacted" in r["case"]]
            line(f"{title}: turns", lambda X, sess=sess: str(len(sess(X))), R)
            line(f"{title}: repeats / shown, one window (no compaction)",
                 lambda X, plain=plain: f"{sum(len(r['repeats']) for r in plain(X))}/{sum(len(r['shown']) for r in plain(X))}", R)
            line(f"{title}: repeats / shown, the case with a compaction (the dedup target)",
                 lambda X, comp=comp: f"{sum(len(r['repeats']) for r in comp(X))}/{sum(len(r['shown']) for r in comp(X))}", R)
            line(f"{title}: relevant / shown (judged turns only)",
                 lambda X, sess=sess: pct(sum(len(r["relevant"]) for r in sess(X) if r["shown"] and not r["unjudged"]),
                                          sum(len(r["shown"]) for r in sess(X) if r["shown"] and not r["unjudged"]))
                 if any(r["shown"] and not r["unjudged"] for r in sess(X)) else "unlabelled", R)
            line(f"{title}: chars shown", lambda X, sess=sess: str(sum(r["chars"] for r in sess(X))), R)
        single = lambda X: [r for r in X if r["kind"] in ("gold", "none")]
        line("head share: group heads / items shown (gold+none)",
             lambda X: pct(sum(len(r["heads"]) for r in single(X)), sum(len(r["shown"]) for r in single(X))), R)
        if watch:
            line(f"head share: watched {', '.join('#' + str(w) for w in watch)} / items shown",
                 lambda X: pct(sum(1 for r in single(X) for i in r["shown"] if i in watch), sum(len(r["shown"]) for r in single(X))), R)
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
    g.add_argument("--watch", default="", help="ids whose share of what is shown to report (none by default)")
    g.add_argument("--labels", help="judge with this labels file now, not as at run time")
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
        print(report(a.results, [int(w) for w in a.watch.split(",") if w], a.labels))


if __name__ == "__main__":
    main(sys.argv[1:])
