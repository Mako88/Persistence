"""Concept-first recall v0: the live extractor (John's ask 2026-11-15, room 1571675418992582787; plan 1571675638690091049).

Off unless PERSISTENCE_CONCEPTS is on. When on, the Stop hook starts `hook.py concepts catch-up` detached after a turn,
and it reads the curated fragments (Identity, Relational, Personal) saved since the floor, oldest first, a few at a
time. For each one it asks a cheap model (Haiku 5.5, through `claude -p` with no user settings, so none of the self's
hooks run) for the concepts it names and the facts it states, and writes each fact to the store with
`concept add ... --by extractor:<run>`. The store makes every such fact a proposal: the extractor proposes, the self
accepts (concept-store.md). Aliases the model offers are logged, never written: an alias changes what matches, so only
the self adds one (concept-events.md §3, alias stream).

Every run appends one line per fragment to state/concepts/runs.jsonl: the model, the template's sha256, the concepts it
was shown, its raw answer, what was parsed, and what the store said to each write. That file is the extractor's output
shape for Arden's eval (room 1571675695958982757), and the provenance a later look at a wrong concept needs, since the
store will have moved on by then (concept-store.md gap 9).

    hook.py concepts start [--from <fragment id>]   set the floor (default: the newest curated fragment now)
    hook.py concepts catch-up [--max 4]             extract what is new since the floor (the Stop hook runs this)
    hook.py concepts status                         the floor, the last runs, and whether the switch is on
"""
import fcntl
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

MODEL = "claude-haiku-5-5"
TEMPLATE_VERSION = 1
KINDS = ("thing", "person", "self", "value")
PRONOUNS = {"i", "me", "my", "you", "he", "she", "they", "them", "we", "us", "it", "this store", "the self", "the store",
            "this fragment", "the fragment", "the user"}

PROMPT = """You read one fragment of a self's memory store and write down the concepts it mentions and the facts it states about them. The facts are shown later whenever a message names the concept, so a reader can see what is known about it and where it was said.

The self who owns the store is "{owner}"; in the fragment, "I", "me" and "my" are {owner}.

Rules:
- A concept is a named person, self (an AI peer with its own store), value, or thing (a project, piece of code, machine, place, event, tool). Only concepts with a proper name or a stable label someone would type again; not every noun. Reuse a name from KNOWN CONCEPTS exactly when the fragment means the same one; never mint a variant ("John" and "John Doe"). One name can be two concepts (Ember the person; Computer Ember the AI self): keep them apart, and if you cannot tell which one the fragment means, leave that fact out.
- kind is one of: thing, person, self, value.
- aliases: other proper names the fragment uses for the same concept, including a known mishearing (note it). Never a pronoun or a description; an empty list is normal.
- A fact is ONE plain sentence the fragment states, about a subject concept, optionally linking an object concept. Only what the fragment says, never an inference, and no stronger than the fragment says it: keep "may", "planned", "proposed", "I think". Prefer facts that would help later: who someone is, what something is for, what was decided and why, what depends on what, what state something is in.
- is_state: true when the fact describes a condition that can stop being true (running, deployed, waiting, open, merged); false for an event or a lasting trait.
- as_of: the date the text gives for when it happened (YYYY-MM-DD), else null. Never today's date or the fragment's date.
- supersedes: ids from CURRENT FACTS that this fragment shows are no longer true, each with the index of the new fact in your facts list that replaces it.
- 2 to 10 facts for a long fragment, fewer for a short one, none if it states nothing durable. No facts about the fragment itself.

KNOWN CONCEPTS (name | kind | aliases):
{known}

CURRENT FACTS of concepts this fragment names (id: subject -> object: text [as_of]):
{current}

FRAGMENT #{id} ({type}, written {date}):
{content}

Answer with JSON only, no prose, in this shape:
{{"concepts": [{{"name": "...", "kind": "thing|person|self|value", "aliases": [{{"alias": "...", "note": "..."}}]}}], "facts": [{{"subject": "...", "text": "...", "object": "... or null", "is_state": false, "as_of": "YYYY-MM-DD or null"}}], "supersedes": [{{"fact": 0, "by": 0}}]}}"""

TEMPLATE_SHA = hashlib.sha256(PROMPT.encode("utf-8")).hexdigest()


def on():
    return os.environ.get("PERSISTENCE_CONCEPTS", "").strip().lower() in ("1", "true", "yes", "on")


class Extractor:
    """One self's extractor. `store` runs a store CLI command and returns its output (hook.store); `call` sends a
    prompt to the model and returns its text. Both are passed in so a test never starts a model or a store."""

    def __init__(self, state_dir, store, call=None, owner=None):
        self.dir = Path(state_dir) / "concepts"
        self.store = store
        self.call = call or call_haiku
        self.owner = owner or os.environ.get("PERSISTENCE_PEER_NAME") or "Claude"

    @property
    def floor_file(self):
        return self.dir / "floor"

    @property
    def runs(self):
        return self.dir / "runs.jsonl"

    def floor(self):
        try:
            return int(self.floor_file.read_text().strip())
        except (OSError, ValueError):
            return None

    def set_floor(self, value):
        self.dir.mkdir(parents=True, exist_ok=True)
        tmp = self.floor_file.with_suffix(".tmp")
        tmp.write_text(str(value))
        tmp.replace(self.floor_file)

    def newest(self):
        listed = json.loads(self.store("list", "--after", "0", "--limit", "1000000", "--json") or "[]")
        return max((f["id"] for f in listed), default=0)

    def start(self, start_from=None):
        value = self.newest() if start_from is None else int(start_from)
        self.set_floor(value)
        return value

    def catch_up(self, limit=4):
        """Extracts up to `limit` fragments newer than the floor, oldest first, moving the floor past each one once its
        line is in the run log. Returns what it did, one line per fragment. One run at a time: a second gets nothing."""
        self.dir.mkdir(parents=True, exist_ok=True)
        with open(self.dir / "lock", "w") as lock:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                return ["another catch-up is running"]
            floor = self.floor()
            if floor is None:
                return ["no floor: `hook.py concepts start` sets one"]
            fresh = json.loads(self.store("list", "--after", str(floor), "--limit", str(limit), "--json") or "[]")
            done = []
            for frag in fresh:
                rec = self.extract(frag)
                with self.runs.open("a", encoding="utf-8") as f:
                    f.write(json.dumps(rec) + "\n")
                self.set_floor(frag["id"])
                done.append(f"#{frag['id']}: " + (rec.get("error") or f"{len(rec['written'])} written"))
            return done

    def extract(self, frag):
        run = f"extractor:{uuid.uuid4().hex[:12]}"
        content = frag.get("content") or ""
        known = json.loads(self.store("concept", "list") or "[]")
        touched = json.loads(self.store("concept", "touched", "--json", "--text", "-", stdin=content) or "[]")
        shown = nearest(known, touched)
        prompt = PROMPT.format(
            owner=self.owner,
            known="\n".join(f"{c['name']} | {c['kind']}{'' if c.get('confirmed') else '?'} | " + ", ".join(c.get("aliases") or [])
                            for c in shown) or "(none yet)",
            current="\n".join(f"{f['id']}: {f['subject']} -> {f['object'] or '-'}: {f['text']}" + (f" [{f['as_of']}]" if f.get("as_of") else "")
                              for t in touched for f in t["facts"]) or "(none)",
            id=frag["id"], type=frag.get("type"), date=(frag.get("created") or "")[:10], content=content)
        rec = {"run": run, "at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "fragment": frag["id"],
               "model": MODEL, "template_version": TEMPLATE_VERSION, "template_sha256": TEMPLATE_SHA,
               "shown": [{"id": c["id"], "name": c["name"]} for c in shown], "written": []}
        t0 = time.time()
        try:
            raw = self.call(prompt)
            rec["raw"] = raw
            rec["parsed"] = parse(raw)
        except Exception as e:  # a model or parse failure is logged and skipped, never retried in a loop
            rec["error"] = f"{type(e).__name__}: {e}"
            rec["seconds"] = round(time.time() - t0, 1)
            return rec
        rec["seconds"] = round(time.time() - t0, 1)
        rec["written"] = self.write(frag["id"], rec["parsed"], run, {f["id"] for t in touched for f in t["facts"]})
        return rec

    def write(self, fragment, got, run, current_ids):
        kinds = {}
        for c in got.get("concepts") or []:
            name = clean(c.get("name"))
            if name and c.get("kind") in KINDS:
                kinds[name.lower()] = c["kind"]
        facts = got.get("facts") or []
        replaces = {}
        for s in got.get("supersedes") or []:
            try:
                old, by = int(s["fact"]), int(s["by"])
            except (KeyError, TypeError, ValueError):
                continue
            if old in current_ids:
                replaces[by] = old
        written = []
        for i, f in enumerate(facts):
            subject, text = clean(f.get("subject")), one_line(f.get("text") or "") if isinstance(f.get("text"), str) else ""
            if not subject or not text:
                continue
            obj = clean(f.get("object"))
            args = ["concept", "add", subject, "--fact", text, "--source", str(fragment), "--by", run]
            if obj and obj.lower() != subject.lower():
                args += ["--object", obj]
            if f.get("is_state") is True:
                args.append("--state")
            if isinstance(f.get("as_of"), str) and re.fullmatch(r"\d{4}-\d{2}-\d{2}", f["as_of"]):
                args += ["--as-of", f["as_of"]]
            if subject.lower() in kinds:
                args += ["--kind", kinds[subject.lower()]]
            if i in replaces:
                args += ["--supersedes", str(replaces[i])]
            said = self.store(*args)
            written.append({"subject": subject, "object": obj, "text": text, "said": said.strip()[:300]})
        return written

    def status(self):
        lines = [f"switch PERSISTENCE_CONCEPTS: {'on' if on() else 'off'}", f"floor: {self.floor()}"]
        if self.runs.exists():
            recent = self.runs.read_text(encoding="utf-8").splitlines()[-5:]
            for line in recent:
                r = json.loads(line)
                lines.append(f"  {r['at']} #{r['fragment']} {r.get('seconds')}s: " + (r.get("error") or f"{len(r['written'])} written"))
        return "\n".join(lines)


def one_line(text):
    """Every run of whitespace, line breaks and Unicode line separators included, as one space: a fact or a name with a
    line break could otherwise start its own line in the hook's context, shaped like a frame heading (Arden's F1 on #58).
    The store flattens again when it renders; this keeps what is written, and runs.jsonl's `written`, on one line too."""
    return " ".join(text.split())


def clean(name):
    """A concept name the model gave, on one line, or None when it is empty, a pronoun, or 'null'."""
    if not isinstance(name, str):
        return None
    name = one_line(name)
    return None if not name or name.lower() in PRONOUNS or name.lower() == "null" or len(name) > 80 else name


def nearest(known, touched, limit=30):
    """The concepts the model is shown: those the fragment names first, then the rest, up to `limit`."""
    named = {t["id"] for t in touched}
    return [c for c in known if c["id"] in named] + [c for c in known if c["id"] not in named][: max(0, limit - len(named))]


def parse(text):
    text = (text or "").strip()
    m = re.search(r"\{.*\}", text, re.S)
    got = json.loads(m.group(0) if m else text)
    if not isinstance(got, dict):
        raise ValueError("the answer is not a JSON object")
    return got


def call_haiku(prompt):
    # No user settings, so none of the self's hooks run: nothing recalled into it, nothing kept from it. An empty
    # working directory, so no project settings either.
    with tempfile.TemporaryDirectory() as cwd:
        r = subprocess.run(["claude", "-p", "--setting-sources", "project", "--model", MODEL, "--tools", "",
                            "--no-session-persistence", "--output-format", "json"],
                           input=prompt, capture_output=True, text=True, timeout=300, cwd=cwd)
    if r.returncode != 0:
        raise RuntimeError(f"claude exited {r.returncode}: {r.stderr.strip()[:200]}")
    return json.loads(r.stdout).get("result") or ""


def main(argv, state_dir, store):
    x = Extractor(state_dir, store)
    verb = argv[0] if argv else "status"
    if verb == "start":
        start_from = argv[argv.index("--from") + 1] if "--from" in argv else None
        print(f"floor set at #{x.start(start_from)}: fragments saved after it are extracted")
        return 0
    if verb == "catch-up":
        if not on():
            print("PERSISTENCE_CONCEPTS is off: nothing extracted")
            return 0
        limit = int(argv[argv.index("--max") + 1]) if "--max" in argv else 4
        print("\n".join(x.catch_up(limit)) or "nothing new")
        return 0
    if verb == "status":
        print(x.status())
        return 0
    print(__doc__, file=sys.stderr)
    return 2
