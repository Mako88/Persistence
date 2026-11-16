"""Concept-first recall v0: the live extractor (John's ask 2026-11-15, room 1571675418992582787; plan 1571675638690091049).

Off unless PERSISTENCE_CONCEPTS is on. When on, the Stop hook starts `hook.py concepts catch-up` detached after a turn,
and it reads the curated fragments (Identity, Relational, Personal) saved since the floor, oldest first, a few at a
time, archived ones too unless a later fragment replaced them (#63). For each one it asks a cheap model (Haiku 5.5, through `claude -p` with no user settings, so none of the self's
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
    hook.py concepts split <name> [--runs 3]        propose moving each fact of a merged concept to its referent (#60)

One name can be two concepts (Ember the peer self; John's daughter Ember: #60). state/concepts/ambiguous.json lists such
names, each with its referents and the cues that tell them apart; absent, nothing changes but the empty AMBIGUOUS NAMES
section. Listed, the model is shown both referents whenever the fragment uses the name or an alias of the old merged
concept, a fact it still files under the bare name or such an alias (the store resolves aliases, so 'Emma' would land on
the merged 'Ember': Arden, review 5915601823) is logged and not written, and `split <name>` asks the model, per fact of
the old merged concept and --runs times over, which referent it means; only a unanimous answer is proposed, under that
referent, superseding the old line on accept. Anything else stays put, listed as unsure or disagreed with the votes.
A fact the extractor files under one of those referents, from a fragment that uses the name, is asked again (CHECK_PROMPT),
blind to the pick (the bare name in its place) and with the fragment as evidence; it is written only if every
answer names the same referent (#14625 on Arden's #68 run: the daughter's relay filed under the peer). The hook passes
the same file to `concept touched --ambiguous`, so a message saying "Ember" reaches both referents.
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
TEMPLATE_VERSION = 3   # 2: the AMBIGUOUS NAMES section (#60). 3: claim, lasting, the cap, supersede and as_of rules (#62 judged)
MAX_FACTS = 5          # review load: median <= 5 facts per fragment (plan §4.4); the store's self reviews every one
KINDS = ("thing", "person", "self", "value")
# A proposal's text must say it is one; any of these will do. Without one it reads as done, and is not written.
PROPOSAL = re.compile(r"\b(propos\w*|suggest\w*|ask(s|ed|ing)?|offer\w*|plan(s|ned|ning)?|would|will|may|might|could|should|"
                      r"wants?|wanted|intends?|hopes?|to be|not yet|pending|request\w*|idea|recommend\w*|consider\w*)\b", re.I)
PRONOUNS = {"i", "me", "my", "you", "he", "she", "they", "them", "we", "us", "it", "this store", "the self", "the store",
            "this fragment", "the fragment", "the user"}

PROMPT = """You read one fragment of a self's memory store and write down the concepts it mentions and the facts it states about them. The facts are shown later whenever a message names the concept, so a reader can see what is known about it and where it was said.

The self who owns the store is "{owner}"; in the fragment, "I", "me" and "my" are {owner}.

Rules:
- A concept is a named person, self (an AI peer with its own store), value, or thing (a project, piece of code, machine, place, event, tool). Only concepts with a proper name or a stable label someone would type again; not every noun. Reuse a name from KNOWN CONCEPTS exactly when the fragment means the same one; never mint a variant ("John" and "John Doe"). One name can be two concepts: AMBIGUOUS NAMES lists them. Never use such a name bare as a subject or object; use the full name of the referent the fragment means, by the cues, and if you cannot tell which one it means, leave that fact out.
- kind is one of: thing, person, self, value.
- aliases: other proper names the fragment uses for the same concept, including a known mishearing (note it). Never a pronoun or a description; an empty list is normal.
- A fact is ONE plain sentence the fragment states, about a subject concept, optionally linking an object concept. Only what the fragment says, never an inference, and no stronger than the fragment says it: keep "may", "planned", "proposed", "I think".
- claim says what kind of statement the fragment makes:
  "fact": something the fragment says is so, or happened.
  "proposal": a proposal, suggestion, offer, request or plan that the fragment does not say was carried out. Its text must say so ("Arden proposed...", "John asked for...", "Claude plans to..."). Never state it as done, built, decided or as a rule: "my additions" are proposed additions, not things added; an issue someone asks to have a correction added to does not "have" that correction.
  "instruction": a one-off order or step for one session or one run ("merge #48 if the head is still 7db543b", "post the numbers when done", "restart the service"). Leave them out; one listed anyway is dropped.
- is_state: true when the fact describes a condition that can stop being true (running, deployed, waiting, open, awaiting review); false for an event or a lasting trait.
- lasting: true for a relation or trait that holds for life or has no start worth dating (who someone is, whose child someone is, what something is). Its as_of is null.
- as_of: the date it became true or happened (YYYY-MM-DD), when the fragment gives it, in its text or because it reports the thing as happening when it was written. NOT the date someone said or wrote down something already true: "Ember is John's daughter", told on 2026-11-09, has as_of null. A state keeps the date the fragment gives for it (the day it was seen waiting, open, running): never drop it. Never today's date. Null when unsure.
- supersedes: ids from CURRENT FACTS that this fragment shows are no longer true, each with the index of the new fact in your facts list that replaces it. Check every state in CURRENT FACTS: if the fragment shows it ended (a PR awaiting review was approved or merged, something open was closed, a plan was done or dropped), write the new fact and supersede the old one. This is the most important kind of fact to get right.
- Terms as used in this store: a "red test" is a test written to fail until the fix lands (test-first), not a test failing on a PR; "green" is passing.
- At most {max_facts} facts, the most useful first: who someone is, what was decided and why, what depends on what, what state something is in, and any fact that supersedes a CURRENT FACT. Fewer for a short fragment, none if it states nothing durable. Leave out passing detail, one-off steps, and facts about the fragment itself.

AMBIGUOUS NAMES (each names more than one concept; name | kind | cues):
{ambiguous}

KNOWN CONCEPTS (name | kind | aliases):
{known}

CURRENT FACTS of concepts this fragment names (id: subject -> object: text [as_of]):
{current}

FRAGMENT #{id} ({type}, written {date}):
{content}

Answer with JSON only, no prose, in this shape:
{{"concepts": [{{"name": "...", "kind": "thing|person|self|value", "aliases": [{{"alias": "...", "note": "..."}}]}}], "facts": [{{"subject": "...", "text": "...", "object": "... or null", "claim": "fact|proposal|instruction", "is_state": false, "lasting": false, "as_of": "YYYY-MM-DD or null"}}], "supersedes": [{{"fact": 0, "by": 0}}]}}"""

TEMPLATE_SHA = hashlib.sha256(PROMPT.encode("utf-8")).hexdigest()

SPLIT_PROMPT = """A self's memory store filed facts about different concepts under one name, "{label}". Read one fact and the fragments it cites, and say which concept "{label}" means in that fact. The fact's own words come first: when they say which (a description beside the name), that settles it, even if a fragment also names the other. Use the fragments for what the fact leaves open. If neither settles it, or the fact is about both, answer unsure: the self decides those, and a wrong pick files a fact under the wrong one.

CANDIDATES (name | kind | cues):
{candidates}

FACT #{id}: {subject} -> {object}: {text}

CITED FRAGMENTS:
{fragments}

Answer with JSON only, no prose: {{"referent": "<one candidate's exact name, or unsure>", "why": "<one short sentence from the fragments>"}}"""

# The extractor's referent check (#60): unlike split's question, the fact here is the extractor's fresh reading, the
# thing being checked, so the fragment decides; and "my" in a fragment is its owner, not whoever is named near it
# ("my store's message" read as the peer's store let #14625's daughter through on the first gate run).
CHECK_PROMPT = """A self's memory store uses the name "{label}" for more than one concept. An extractor read the fragment below and wrote a fact that names "{label}". Decide from the fragment which concept "{label}" means in the part of the fragment the fact comes from. The fragment decides: the fact is the extractor's reading and may be wrong. The store's owner is "{owner}": in the fragment, "I", "me", "my" (my store, my session) are {owner}, never {label}. If the fragment does not settle which one, or the fact mixes both, answer unsure.

CANDIDATES (name | kind | cues):
{candidates}

FRAGMENT #{fragment}:
{content}

THE EXTRACTOR'S FACT: {subject} -> {object}: {text}

Answer with JSON only, no prose: {{"referent": "<one candidate's exact name, or unsure>", "why": "<one short sentence from the fragment>"}}"""

# Asked first, about the whole fragment: most fragments use the name for one referent only, and that is an easier
# question than one fact's, whose wording (the extractor's) can lead the answer (the second gate run, #14625).
READ_PROMPT = """A self's memory store uses the name "{label}" for more than one concept. Read the fragment and say which concept "{label}" means in it. The store's owner is "{owner}": in the fragment, "I", "me", "my" (my store, my session) are {owner}, never {label}. Answer one candidate's exact name only if every use of "{label}" in the fragment means that one; "both" if it is used for more than one of them; "unsure" if the fragment does not settle it.

CANDIDATES (name | kind | cues):
{candidates}

FRAGMENT #{fragment}:
{content}

Answer with JSON only, no prose: {{"referent": "<one candidate's exact name, both, or unsure>", "why": "<one short sentence from the fragment>"}}"""

CHECK_SHA = hashlib.sha256((CHECK_PROMPT + READ_PROMPT).encode("utf-8")).hexdigest()


def on():
    return os.environ.get("PERSISTENCE_CONCEPTS", "").strip().lower() in ("1", "true", "yes", "on")


class Extractor:
    """One self's extractor. `store` runs a store CLI command and returns its output (hook.store); `call` sends a
    prompt to the model and returns its text. Both are passed in so a test never starts a model or a store."""

    def __init__(self, state_dir, store, call=None, owner=None, ambiguous=None, split_runs=3, confirm_runs=1, read_runs=3):
        self.dir = Path(state_dir) / "concepts"
        self.store = store
        self.call = call or call_haiku
        self.owner = owner or os.environ.get("PERSISTENCE_PEER_NAME") or "Claude"
        self.ambiguous = load_ambiguous(self.dir / "ambiguous.json") if ambiguous is None else ambiguous
        self.split_runs = split_runs
        self.confirm_runs = confirm_runs
        self.read_runs = read_runs

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

    def above(self, floor, limit):
        """The first `limit` curated fragments above `floor`, oldest first, active and archived alike (#63). The store's
        `list` gives one status or the other, never both, so it asks for each and merges: the first `limit` of the
        union are always among the first `limit` of each. Forgotten fragments are never listed.
        Active is asked again after archived: a fragment restored between the first two calls is in neither of them,
        and a higher id in the same batch would move the floor past it for good (Arden on #65, review 5914122136).
        Keyed by id, the later answer winning: a fragment archived between calls (an inbox message shown at a session
        start while a detached catch-up runs) is read once, as its later self; one whose status changes at any one
        moment is in at least one answer."""
        both = {}
        for status in ([], ["--archived"], []):
            for f in json.loads(self.store("list", "--after", str(floor), "--limit", str(limit), *status, "--json") or "[]"):
                both[f["id"]] = f
        return sorted(both.values(), key=lambda f: f["id"])[:limit]

    def newest(self):
        return max((f["id"] for f in self.above(0, 1000000)), default=0)

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
            done = []
            for frag in self.above(floor, limit):
                if replaced(frag):
                    self.set_floor(frag["id"])
                    done.append(f"#{frag['id']}: replaced by a later fragment, not read")
                    continue
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
        bare = self.bare(known)
        named = self.names_in(content, bare)
        # A listed name's own concept (the merged one) is not offered for reuse, nor its facts: `split` moves those.
        # Its referents are offered, as named, whether or not the fragment spells their full names.
        known = [c for c in known if c["name"].lower() not in bare]
        touched = [t for t in touched if t["name"].lower() not in bare]
        for t in touched:
            t["facts"] = [f for f in t["facts"] if (f["subject"] or "").lower() not in bare and (f["object"] or "").lower() not in bare]
        referents = {r["name"].lower() for label in named for r in self.ambiguous[label]}
        shown = nearest(known, touched + [c for c in known if c["name"].lower() in referents])
        prompt = PROMPT.format(
            owner=self.owner, max_facts=MAX_FACTS,
            ambiguous="\n".join(f'"{label}" names more than one concept: ' + "; ".join(
                f"{r['name']} | {r.get('kind', '?')} | {r.get('cues', '')}" for r in self.ambiguous[label]) for label in named) or "(none)",
            known="\n".join(f"{c['name']} | {c['kind']}{'' if c.get('confirmed') else '?'} | " + ", ".join(c.get("aliases") or [])
                            for c in shown) or "(none yet)",
            current="\n".join(f"{f['id']}: {f['subject']} -> {f['object'] or '-'}: {f['text']}" + (f" [{f['as_of']}]" if f.get("as_of") else "")
                              for t in touched for f in t["facts"]) or "(none)",
            id=frag["id"], type=frag.get("type"), date=(frag.get("created") or "")[:10], content=content)
        rec = {"run": run, "at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "fragment": frag["id"],
               "model": MODEL, "template_version": TEMPLATE_VERSION, "template_sha256": TEMPLATE_SHA, "check_sha256": CHECK_SHA,
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
        rec["skipped"] = []
        # A fact filed under one referent of a name the fragment uses ambiguously is asked again, blind to that pick:
        # #14625 rep 2 on Arden's #68 run filed the daughter's relay under the peer (#60).
        referents = {r["name"].lower(): label for label in named for r in self.ambiguous[label]}
        confirm = None
        if referents:
            rec["referent_checks"], rec["referent_reading"] = [], {}
            confirm = lambda subject, obj, text: self.confirm_referent(subject, obj, text, referents, frag["id"], content,
                                                                       rec["referent_checks"], rec["referent_reading"])
        rec["written"] = self.write(frag["id"], rec["parsed"], run, {f["id"] for t in touched for f in t["facts"]}, rec["skipped"], bare, confirm)
        rec["seconds"] = round(time.time() - t0, 1)
        return rec

    def votes(self, prompt, refs, runs, extra=()):
        """`runs` answers to `prompt`, each a candidate's exact name, one of `extra`, 'unsure' or 'error', with the whys."""
        votes, whys = [], []
        for _ in range(max(1, int(runs))):
            try:
                got = parse(self.call(prompt))
                said = str(got.get("referent") or "").strip().lower()
                votes.append(next((r["name"] for r in refs if r["name"].lower() == said), said if said in extra else "unsure"))
                whys.append(one_line(str(got.get("why") or "")))
            except Exception as e:
                votes.append("error")
                whys.append(f"{type(e).__name__}: {e}")
        return votes, whys

    def reading(self, label, fragment, content, readings):
        """The one referent the fragment uses `label` for, when `read_runs` answers all name it; else None. Asked once
        per fragment and label, kept in `readings` (runs.jsonl's referent_reading)."""
        if label not in readings:
            refs = self.ambiguous[label]
            prompt = READ_PROMPT.format(label=label, owner=self.owner, fragment=fragment, content=content[:6000],
                                        candidates="\n".join(f"{r['name']} | {r.get('kind', '?')} | {r.get('cues', '')}" for r in refs))
            votes, whys = self.votes(prompt, refs, self.read_runs, extra=("both",))
            one = votes[0] if len(set(votes)) == 1 and any(r["name"] == votes[0] for r in refs) else None
            readings[label] = {"votes": votes, "whys": whys, "referent": one}
        return readings[label]["referent"]

    def confirm_referent(self, subject, obj, text, referents, fragment, content, checks, readings=None):
        """None when the fact names no referent of an ambiguous name the fragment uses, or when every such referent is
        confirmed; otherwise why it is not. Each referent in the fact is asked about `confirm_runs` times (CHECK_PROMPT),
        the fact shown with the bare name in that place so the extractor's pick can't lead the answer, and the
        fragment as its evidence. Only an answer naming the same referent every time confirms it: a different referent,
        unsure or an error leaves the fact unwritten and logged, since a fact under the wrong person is the severe miss.
        Before any of that, the fragment as a whole is asked which referent it means (READ_PROMPT): when every answer
        names one, a fact under it stands and a fact under another is dropped with no per-fact question."""
        readings = {} if readings is None else readings
        for name in dict.fromkeys(n for n in (subject, obj) if n and n.lower() in referents):
            label = referents[name.lower()]
            refs = self.ambiguous[label]
            whole = self.reading(label, fragment, content, readings)
            if whole is not None:
                if whole.lower() != name.lower():
                    return f"referent not confirmed: filed under {name!r}, but the fragment means {whole!r} by {label!r}"
                continue
            prompt = CHECK_PROMPT.format(
                label=label, owner=self.owner, candidates="\n".join(f"{r['name']} | {r.get('kind', '?')} | {r.get('cues', '')}" for r in refs),
                subject=label if subject == name else subject, object=label if obj == name else (obj or "-"), text=text,
                fragment=fragment, content=content[:6000])
            check = {"subject": subject, "object": obj, "text": text, "referent": name}
            check["votes"], check["whys"] = self.votes(prompt, refs, self.confirm_runs)
            check["confirmed"] = all(v.lower() == name.lower() for v in check["votes"])
            checks.append(check)
            if not check["confirmed"]:
                tally = ", ".join(f"{v} {check['votes'].count(v)}" for v in dict.fromkeys(check["votes"]))
                return f"referent not confirmed: filed under {name!r}, asked again: {tally}"
        return None

    def bare(self, known=None):
        """{lowercased name: its listed label} for every name that would file a fact on an ambiguous name's merged
        concept: the label itself and each alias the store holds for the concept of that name, since `concept add`
        resolves aliases too ('Emma' -> Ember; Arden, review 5915601823). `known` is `concept list`'s answer, asked
        for when not given."""
        bare = {label.lower(): label for label in self.ambiguous}
        if not bare:
            return bare
        if known is None:
            known = json.loads(self.store("concept", "list") or "[]")
        for c in known:
            label = bare.get((c.get("name") or "").lower())
            if label and c["name"].lower() == label.lower():
                for alias in c.get("aliases") or []:
                    bare.setdefault(alias.lower(), label)
        return bare

    def names_in(self, text, bare=None):
        """The listed ambiguous names the text uses, as whole words (Ember, Ember's; not Remember), or through an alias
        of the merged concept (Emma), in the order they are listed."""
        bare = bare if bare is not None else {label.lower(): label for label in self.ambiguous}
        hits = {label for name, label in bare.items() if re.search(rf"(?<!\w){re.escape(name)}(?!\w)", text or "", re.I)}
        return [label for label in self.ambiguous if label in hits]

    def kind_of(self, name):
        return next((r.get("kind") for rs in self.ambiguous.values() for r in rs if r["name"].lower() == name.lower()), None)

    def write(self, fragment, got, run, current_ids, skipped=None, bare=None, confirm=None):
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
            if old not in current_ids and by in current_ids and 0 <= old < len(facts):
                old, by = by, old   # the fields swapped: {"fact": <index>, "by": <shown id>} (the stale #48 miss on #62)
            if old in current_ids:
                replaces[by] = old
        if bare is None:
            bare = self.bare()
        log = (lambda subject, obj, text, why: skipped.append({"subject": subject, "object": obj, "text": text, "why": why})) \
            if skipped is not None else (lambda *_: None)
        keep = []
        for i, f in enumerate(facts):
            if not isinstance(f, dict):
                continue
            subject, text = clean(f.get("subject")), one_line(f.get("text") or "") if isinstance(f.get("text"), str) else ""
            if not subject or not text:
                continue
            obj = clean(f.get("object"))
            hit = next((n for n in (subject, obj) if n and n.lower() in bare), None)
            if hit:   # filed under the bare name would merge its referents again (#60): logged, not written
                label = bare[hit.lower()]
                log(subject, obj, text, f"ambiguous name {hit!r}: names more than one concept" if hit.lower() == label.lower() else
                    f"ambiguous name {hit!r}: an alias of {label!r}, which names more than one concept")
                continue
            claim = f.get("claim")
            if claim == "instruction":   # a one-off order to a session is not a fact about anything (#19196 on #62)
                log(subject, obj, text, "a one-off instruction, not a fact")
                continue
            if claim == "proposal" and not PROPOSAL.search(text):   # 'my additions' as 'Claude added' (#19324 on #62)
                log(subject, obj, text, "a proposal whose text states it as done")
                continue
            why = confirm(subject, obj, text) if confirm else None
            if why:
                log(subject, obj, text, why)
                continue
            keep.append((i, f, subject, obj, text))
        # The cap (plan §4.4, review load median <= 5): a fact that supersedes one shown goes first, since a stale fact
        # left standing is worse than a new one missed; the rest in the model's order, which the prompt asks be most useful first.
        ranked = sorted(keep, key=lambda k: (k[0] not in replaces, k[0]))
        for i, f, subject, obj, text in ranked[MAX_FACTS:]:
            log(subject, obj, text, f"over the cap of {MAX_FACTS} facts per fragment")
        written = []
        for i, f, subject, obj, text in sorted(ranked[:MAX_FACTS]):
            args = ["concept", "add", subject, "--fact", text, "--source", str(fragment), "--by", run]
            if obj and obj.lower() != subject.lower():
                args += ["--object", obj]
            if f.get("is_state") is True:
                args.append("--state")
            # A lasting relation or trait has no start worth dating: the day it was said is not when it began (#14625 on #62).
            # A state keeps its date even if also marked lasting (Arden, review 5916452365).
            as_of = f["as_of"] if isinstance(f.get("as_of"), str) and re.fullmatch(r"\d{4}-\d{2}-\d{2}", f["as_of"]) else None
            if f.get("lasting") is True and f.get("is_state") is not True:
                as_of = None
            if as_of:
                args += ["--as-of", as_of]
            if self.kind_of(subject):
                args += ["--kind", self.kind_of(subject)]
            elif subject.lower() in kinds:
                args += ["--kind", kinds[subject.lower()]]
            if i in replaces:
                args += ["--supersedes", str(replaces[i])]
            said = self.store(*args)
            # as_of and supersedes as passed, so a reader of runs.jsonl needn't re-derive the lasting rule or the swap (#62's adapter).
            written.append({"i": i, "subject": subject, "object": obj, "text": text, "as_of": as_of, "supersedes": replaces.get(i),
                            "said": said.strip()[:300]})
        return written

    @property
    def splits(self):
        return self.dir / "splits.jsonl"

    def split(self, label, runs=None):
        """Proposes moving each standing fact of the merged concept `label` to the referent it means (#60). The model is
        asked `runs` times per fact (default self.split_runs, 3); only when every answer names the same referent is the
        move proposed: `concept add` under the referent, with the same text, sources, state and date, `--by
        extractor:split-...` so the store makes it a proposal, and `--supersedes` the old fact, which accepting retires.
        A fact the runs disagree on, or are all unsure of, is left where it is and listed with its votes: a coin flip
        must not reach the review queue looking like a reading (Arden, review 5915601823). One line per fact in
        splits.jsonl; a fact already moved is not proposed again, and one left is asked again next time."""
        runs = max(1, int(runs or self.split_runs))
        key = next((k for k in self.ambiguous if k.lower() == label.lower()), None)
        if key is None:
            return [f"{label!r} is not listed in {self.dir / 'ambiguous.json'}: nothing to split into"]
        refs = self.ambiguous[key]
        names = {r["name"].lower(): r for r in refs}
        merged = next((t for t in json.loads(self.store("concept", "touched", "--json", "--text", "-", stdin=key) or "[]")
                       if t["name"].lower() == key.lower()), None)
        if merged is None:
            return [f"no concept is named {key!r}"]
        moved = set()
        if self.splits.exists():
            moved = {r["fact"] for r in map(json.loads, self.splits.read_text(encoding="utf-8").splitlines()) if r.get("proposed")}
        run = f"extractor:split-{uuid.uuid4().hex[:12]}"
        self.dir.mkdir(parents=True, exist_ok=True)
        done = []
        for f in merged["facts"]:
            if f["id"] in moved:
                done.append(f"#{f['id']}: already proposed under its referent")
                continue
            sources = f.get("sources") or []
            prompt = SPLIT_PROMPT.format(
                label=key, candidates="\n".join(f"{r['name']} | {r.get('kind', '?')} | {r.get('cues', '')}" for r in refs),
                id=f["id"], subject=f["subject"], object=f["object"] or "-", text=f["text"],
                fragments="\n\n".join(self.store("show", str(s)).strip()[:4000] for s in sources) or "(none)")
            rec = {"run": run, "at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "label": key, "fact": f["id"],
                   "model": MODEL, "runs": runs, "votes": [], "whys": [], "raws": [], "proposed": False}
            for _ in range(runs):
                try:
                    raw = self.call(prompt)
                    rec["raws"].append(raw)
                    got = parse(raw)
                    pick = names.get(str(got.get("referent") or "").strip().lower())
                    vote = pick["name"] if pick else "unsure"
                    rec["whys"].append(one_line(str(got.get("why") or "")))
                except Exception as e:
                    vote = "error"
                    rec["whys"].append(f"{type(e).__name__}: {e}")
                rec["votes"].append(vote)
            agreed = rec["votes"][0] if len(set(rec["votes"])) == 1 and rec["votes"][0].lower() in names else None
            if agreed is None:
                rec["outcome"] = "unsure" if all(v == "unsure" for v in rec["votes"]) else \
                    "error" if all(v == "error" for v in rec["votes"]) else "disagreed"
                tally = ", ".join(f"{v} {rec['votes'].count(v)}" for v in dict.fromkeys(rec["votes"]))
                why = next((w for w in rec["whys"] if w), "")
                done.append(f"#{f['id']}: left on {key}: {rec['outcome']} (votes: {tally})" + (f" ({why})" if rec["outcome"] != "disagreed" and why else ""))
            else:
                pick = names[agreed.lower()]
                subj = pick["name"] if f["subject"].lower() == key.lower() else f["subject"]
                obj = pick["name"] if (f["object"] or "").lower() == key.lower() else f["object"]
                args = ["concept", "add", subj, "--fact", f["text"], "--source", ",".join(map(str, sources)), "--by", run,
                        "--supersedes", str(f["id"])]
                if obj:
                    args += ["--object", obj]
                if f.get("is_state"):
                    args.append("--state")
                if f.get("as_of"):
                    args += ["--as-of", str(f["as_of"])[:10]]
                if subj == pick["name"] and pick.get("kind"):
                    args += ["--kind", pick["kind"]]
                rec["outcome"], rec["referent"], rec["said"] = "agreed", pick["name"], self.store(*args).strip()[:300]
                rec["proposed"] = bool(rec["said"])   # the CLI answers "" when the store refused or could not run
                done.append(f"#{f['id']}: " + (f"proposed under {pick['name']} ({runs}/{runs} agree): {rec['said']}" if rec["proposed"] else f"{pick['name']}, but the store refused"))
            with self.splits.open("a", encoding="utf-8") as out:
                out.write(json.dumps(rec) + "\n")
        return done

    def status(self):
        lines = [f"switch PERSISTENCE_CONCEPTS: {'on' if on() else 'off'}", f"floor: {self.floor()}"]
        if self.runs.exists():
            recent = self.runs.read_text(encoding="utf-8").splitlines()[-5:]
            for line in recent:
                r = json.loads(line)
                lines.append(f"  {r['at']} #{r['fragment']} {r.get('seconds')}s: " + (r.get("error") or f"{len(r['written'])} written"))
        return "\n".join(lines)


def load_ambiguous(path):
    """{name: [{name, kind, cues}, ...]} from ambiguous.json, or {} when there is none. Only a name with two or more
    referents counts: one referent is an alias, which only the self adds."""
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    return {k: v for k, v in data.items() if isinstance(v, list) and len(v) >= 2 and all(isinstance(r, dict) and r.get("name") for r in v)}


def replaced(frag):
    """Archived because a later fragment took its place (superseded, merged, or split). The replacement has a higher id,
    so catch-up reads it in turn; reading this one too would propose the same facts twice. Any other archived fragment,
    an inbox message once shown above all, is read: archived means no longer shown, not never true (#63)."""
    return frag.get("status") == "Archived" and frag.get("supersededBy") is not None   # a split sets it too


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
    if verb == "split" and len(argv) > 1:
        runs = None
        if "--runs" in argv:
            i = argv.index("--runs")
            if i + 1 >= len(argv) or not argv[i + 1].isdigit() or int(argv[i + 1]) < 1:
                print("--runs takes a whole number of at least 1", file=sys.stderr)
                return 2
            runs = int(argv[i + 1])
        print("\n".join(x.split(argv[1], runs)))
        return 0
    print(__doc__, file=sys.stderr)
    return 2
