"""Sources for the "Observed now" block (docs/design/observed-now-sources.md): what is so now, read at the wake and
never stored. The seam agreed with Ember (room 1570646253388177512):

- A source's read is `read(event, ctx) -> Observation`: a structured record (source, observed_at, status
  ok|unknown|error, freshness, bounded lines, optional event_range, truncated), never prose about itself.
  `render()` is the one place an Observation becomes text, so "couldn't read" is a status, not a line a source writes.
- A read writes nothing. Whatever a source would persist (a cursor, a cache) rides in `Observation.ack`, and
  `acknowledge()` commits it only after the block carrying that observation has been handed to the session, so a
  failed wake consumes nothing and a timed-out read that finishes late changes nothing.

Nothing runs unless the store's own settings name it in PERSISTENCE_OBSERVE_SOURCES. Ember's event-buffer projection
plugs in as one more entry in SOURCES (a read that projects "new since cursor" with event_range, an ack that advances
the cursor). Standard library only; nothing here imports hook.py (it hands what it needs in through ctx)."""
import json
import os
import re
import threading
import urllib.request
from dataclasses import dataclass, field, replace
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable, Mapping, Optional

INSTALLED = Path(__file__).resolve().parent  # the hook files sit together, as deploy-remote.sh installs them
HOOK_FILES = ("hook.py", "coordination.py", "claims.py", "cwd_exec.py", "run_line.py", "observed.py")
STATUSES = ("ok", "unknown", "error")
MAX_LINES = 6        # per observation, enforced by render() whatever a source returns
MAX_LINE_CHARS = 200
STALE = timedelta(seconds=60)  # data older than this is shown with its age


class Unreadable(Exception):
    """A source couldn't determine what it observes (status unknown); the reason is rendered."""


@dataclass(frozen=True)
class Observation:
    source: str
    observed_at: datetime                  # when the read ran
    status: str = "ok"                     # ok | unknown | error
    lines: tuple = ()                      # bounded facts, without "- "; render() adds it
    freshness: Optional[datetime] = None   # when the data was true at its source (a cache's time); None = observed_at
    reason: str = ""                       # why unknown/error, or what made ok data stale
    event_range: Optional[tuple] = None    # (oldest, newest) id or time of the events shown, for cursored sources
    truncated: int = 0                     # items left out; render() adds what it cuts
    ack: Any = None                        # what acknowledge() hands the source's ack after injection; never rendered


@dataclass(frozen=True)
class Source:
    read: Callable[[dict, "Context"], Observation]
    ack: Optional[Callable[[Observation, dict, "Context"], None]] = None


@dataclass
class Context:
    state: Path
    setting: Callable[[str, str], str]
    run: Callable[..., str]
    now: datetime
    env: Mapping[str, str] = field(default_factory=lambda: os.environ)


def age(delta):
    s = max(0, delta.total_seconds())
    return f"{s / 3600:.1f}h" if s >= 3600 else f"{s / 60:.0f}m" if s >= 60 else f"{s:.0f}s"


def hhmm(when):
    return when.astimezone(timezone.utc).strftime("%H:%MZ")


def parse_time(value):
    when = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
    return when if when.tzinfo else when.replace(tzinfo=timezone.utc)


def short(text, n):
    text = " ".join(str(text or "").split())
    return text if len(text) <= n else text[:n - 1] + "…"


def own_file(path):
    """A state file this process may trust: owned by us and writable by nobody else (cache trust, Ember's review)."""
    st = path.stat()
    return (not hasattr(os, "geteuid") or st.st_uid == os.geteuid()) and not st.st_mode & 0o022


def write_atomic(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + f".{os.getpid()}.{threading.get_ident()}.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(text)
        f.flush()
        os.fsync(f.fileno())
    os.chmod(tmp, 0o600)
    tmp.replace(path)


# --- prs: open pull requests, each with its head and each reviewer's latest review ---

PRS_TTL = 120
PRS_SHOWN = 5


def github_get(url, token, timeout):
    request = urllib.request.Request(url, headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
                                                   "User-Agent": "persistence-observe"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def github_token(ctx):
    """The one `Token:` line of the token file; no other line is kept past the match."""
    path = Path(os.path.expanduser(ctx.setting("PERSISTENCE_OBSERVE_GITHUB_TOKEN_FILE", "~/secrets/accounts")))
    try:
        with path.open(encoding="utf-8") as f:
            for line in f:
                if line.startswith("Token:"):
                    return line[len("Token:"):].strip() or None
    except OSError:
        return None
    return None


def prs_summary(pulls, reviews_of):
    """What is cached and rendered: numbers, titles, heads and review states only; no URLs, bodies or tokens."""
    out = []
    for pr in pulls[:PRS_SHOWN]:
        latest = {}
        for r in reviews_of.get(str(pr["number"]), []):
            if r.get("state") and r["state"] != "PENDING":
                latest[str((r.get("user") or {}).get("login", "?"))] = str(r["state"])
        out.append({"number": int(pr["number"]), "title": short(pr.get("title"), 70), "head": str(pr["head"]["sha"])[:7],
                    "draft": bool(pr.get("draft")), "reviews": latest})
    return {"open": len(pulls), "shown": out}


def prs_lines(repo, summary):
    if not summary["open"]:
        return (f"none open on {repo}",)
    out = [f"PR #{p['number']}{' draft' if p['draft'] else ''} {p['title']} (head {p['head']}): "
           + (", ".join(f"{who} {state}" for who, state in p["reviews"].items()) or "no reviews") for p in summary["shown"]]
    return tuple(out)


def prs_cache(ctx, repo):
    path = ctx.state / "observed-prs.json"
    try:
        if not own_file(path):
            return None
        cache = json.loads(path.read_text(encoding="utf-8"))
        if cache.get("repo") != repo:
            return None
        return parse_time(cache["at"]), cache["summary"]
    except (OSError, ValueError, KeyError, TypeError):
        return None


def prs(event, ctx):
    repo = ctx.setting("PERSISTENCE_OBSERVE_GITHUB_REPO", "Mako88/Persistence")
    cached = prs_cache(ctx, repo)
    obs = Observation("prs", ctx.now)
    if cached and timedelta(0) <= ctx.now - cached[0] < timedelta(seconds=PRS_TTL):
        at, summary = cached
        return replace(obs, lines=prs_lines(repo, summary), freshness=at, reason="cached",
                       truncated=max(0, summary["open"] - len(summary["shown"])))
    token = github_token(ctx)
    try:
        if not token:
            raise Unreadable("no Token: line in the token file")
        base = f"https://api.github.com/repos/{repo}/pulls"
        pulls = github_get(f"{base}?state=open&per_page=30", token, 3)
        reviews = {str(p["number"]): github_get(f"{base}/{p['number']}/reviews?per_page=100", token, 3) for p in pulls[:PRS_SHOWN]}
        summary = prs_summary(pulls, reviews)
    except Exception as e:
        why = str(e) if isinstance(e, Unreadable) else f"GitHub: {type(e).__name__}"  # never the exception text: it can carry a URL
        if cached:
            at, summary = cached
            return replace(obs, lines=prs_lines(repo, summary), freshness=at, reason=f"{why}; cached",
                           truncated=max(0, summary["open"] - len(summary["shown"])))
        return replace(obs, status="unknown", reason=f"{why}, nothing cached")
    return replace(obs, lines=prs_lines(repo, summary), truncated=max(0, summary["open"] - len(summary["shown"])),
                   ack={"at": ctx.now.isoformat(timespec="seconds"), "repo": repo, "summary": summary})


def prs_ack(obs, event, ctx):
    write_atomic(ctx.state / "observed-prs.json", json.dumps(obs.ack))


# --- deployed: installed hook files and the deployed store against main ---

def deployed(event, ctx):
    repo = os.path.expanduser(ctx.setting("PERSISTENCE_OBSERVE_REPO", "~/work/Persistence"))
    obs = Observation("deployed", ctx.now)
    main = ctx.run(["git", "rev-parse", "--verify", "-q", "main"], repo).strip()
    if not main:
        return replace(obs, status="unknown", reason=f"no main branch in {repo}")
    out, unknown = [], []
    tree = {}
    for line in ctx.run(["git", "ls-tree", "main", "scripts/claude-code/"], repo).splitlines():
        meta, _, path = line.partition("\t")
        if meta.split()[1:2] == ["blob"]:
            tree[Path(path).name] = meta.split()[2]
    present = [f for f in HOOK_FILES if (INSTALLED / f).exists()]
    hashes = ctx.run(["git", "hash-object", *[str(INSTALLED / f) for f in present]], repo).split() if present else []
    if len(hashes) != len(present):
        unknown.append("hooks: git hash-object failed")
    else:
        differ = [f for f, h in zip(present, hashes) if tree.get(f) != h]
        missing = [f for f in HOOK_FILES if f not in present and f in tree]
        if not differ and not missing:
            out.append(f"hooks: match main ({main[:7]})")
        else:
            out.append("hooks: " + "; ".join(filter(None, [
                f"{', '.join(differ)} differ from main ({main[:7]})" if differ else "",
                f"missing: {', '.join(missing)}" if missing else ""])))
    marker = Path(ctx.setting("PERSISTENCE_OBSERVE_STORE_MARKER", "/opt/persistence/store/DEPLOYED"))
    try:
        sha = marker.read_text(encoding="utf-8").split()[0]
    except (OSError, IndexError):
        unknown.append(f"store: which commit is deployed (no {marker})")
        sha = None
    if sha:
        behind = ctx.run(["git", "rev-list", "--count", f"{sha}..main"], repo).strip()
        if not behind.isdigit():
            out.append(f"store: deployed {sha[:7]}, not in {repo}'s history")
        elif behind == "0":
            out.append(f"store: deployed {sha[:7]}, at main")
        else:
            out.append(f"store: deployed {sha[:7]}, {behind} commit{'s' if behind != '1' else ''} behind main ({main[:7]})")
    return replace(obs, lines=tuple(out), status="unknown" if unknown else "ok", reason="; ".join(unknown))


# --- switches: the recall and query switches that are set, on the hook's side and the store's ---

HOOK_SWITCHES = ("PERSISTENCE_RECALL_COUNT", "PERSISTENCE_RECALL_SAID", "PERSISTENCE_RECALL_WANDER")
STRIP = "PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE"
STORE_SWITCH = re.compile(r"\bPERSISTENCE_(?:RECALL_\w+|EMBEDDING_QUERY\w+|EMBEDDING_QUALITYWEIGHT)=\S*")


def switches(event, ctx):
    # Read the way recall reads them: from this process's environment (hook.py's recall uses os.environ).
    set_ = [f"{n[len('PERSISTENCE_'):]}={ctx.env[n]}" for n in HOOK_SWITCHES if ctx.env.get(n)]
    on = str(ctx.env.get(STRIP, "")).strip().lower() in ("1", "true", "yes", "on")
    out = ["recall switches (hook): " + ", ".join(set_ + ["STRIP_ROOM_PREAMBLE " + ("on" if on else "off")])]
    obs = Observation("switches", ctx.now)
    unit = ctx.setting("PERSISTENCE_OBSERVE_STORE_UNIT", "")
    if unit:
        shown = ctx.run(["systemctl", "show", "-p", "Environment", unit], None, 5)
        if not shown.startswith("Environment="):
            return replace(obs, lines=tuple(out), status="unknown", reason=f"store unit {unit}: systemctl show gave nothing")
        found = STORE_SWITCH.findall(shown)
        out.append(f"recall switches (store {unit}): " + (", ".join(s[len("PERSISTENCE_"):] for s in found) or "none set"))
    return replace(obs, lines=tuple(out))


# --- claims: work sessions that finished since this session last acknowledged them ---

CLAIMS_SHOWN = 4
FIRST_LOOK = timedelta(hours=6)


def claims_cursor(event, ctx):
    return ctx.state / f"{event.get('session_id') or 'unknown'}.observed-claims"


CLAIMS_SETTLE = timedelta(seconds=30)  # how far the cursor stays behind a read's now (Arden on 5d07630)


def read_claims_cursor(event, ctx):
    """(since, {claim id: its updated time} already shown after since). The cursor file is JSON {"at", "seen"}; a bare
    time (as 5d07630 wrote it) reads as that time with nothing seen. Missing or unreadable: the first look's window."""
    try:
        text = claims_cursor(event, ctx).read_text(encoding="utf-8").strip()
    except OSError:
        return ctx.now - FIRST_LOOK, {}
    try:
        if text.startswith("{"):
            data = json.loads(text)
            return parse_time(data["at"]), {str(k): parse_time(v) for k, v in data.get("seen", {}).items()}
        return parse_time(text), {}
    except (ValueError, KeyError, TypeError, AttributeError):
        return ctx.now - FIRST_LOOK, {}


def claims_done(event, ctx):
    """Claims done or abandoned in (cursor, now], not this session's own and not already shown. Reads the cursor, never
    moves it. The ack moves it to CLAIMS_SETTLE before this read's now, not to now: claims.py stamps `updated` before it
    writes the file, so a claim stamped just before the read may not be on disk yet (Arden on 5d07630). The ids shown
    inside that margin ride along as `seen` with the finish they were shown for, so the next read skips that finish but
    not a later one (a claim reopened and finished again inside the margin; Arden on 2b6e140): at-least-once, without
    repeats."""
    session = event.get("session_id") or "unknown"
    since, seen = read_claims_cursor(event, ctx)
    found = []
    for path in sorted((ctx.state / "tasks").glob("*.json")):
        try:
            record = json.loads(path.read_text(encoding="utf-8"))
            when = parse_time(record["updated"]).replace(microsecond=0)  # as `seen` stores it
        except (OSError, ValueError, KeyError, TypeError, AttributeError):
            continue
        if record.get("status") in ("done", "abandoned") and record.get("owner") != session and since < when <= ctx.now \
                and seen.get(str(record.get("id"))) != when:
            found.append((when, record))
    found.sort(key=lambda item: (item[0], str(item[1].get("id"))), reverse=True)
    out = tuple(f"work {r['status']} {hhmm(w)}: claim {str(r.get('id', ''))[:8]} {short(r.get('task'), 70)}"
                + (f" — {short(r['outcome'], 60)}" if r.get("outcome") else "") for w, r in found[:CLAIMS_SHOWN])
    at = max(since, (ctx.now - CLAIMS_SETTLE).replace(microsecond=0))  # as written, so `seen` is judged against it
    still = {**seen, **{str(r.get("id")): w for w, r in found}}
    ack = {"at": at.isoformat(timespec="seconds"),
           "seen": {k: w.isoformat(timespec="seconds") for k, w in sorted(still.items()) if w > at}}
    return Observation("claims", ctx.now, lines=out, truncated=max(0, len(found) - CLAIMS_SHOWN),
                       event_range=(found[-1][0].isoformat(timespec="seconds"), found[0][0].isoformat(timespec="seconds")) if found else None,
                       ack=json.dumps(ack, sort_keys=True))


def claims_ack(obs, event, ctx):
    write_atomic(claims_cursor(event, ctx), obs.ack)


# --- heartbeat: the stalled-work reports block, the standing delivery fault, a suspended working mode ---
# (docs/design/heartbeat-stalled-work.md 3.1, 3.3 and 5.) The read writes nothing; the ack moves exactly the entries
# still `queued` under the delivering wake to `delivered`, under heartbeat.lock (compare and set).

HB_ACK_WAIT = 5


def fault_showing(fault):
    return isinstance(fault, dict) and isinstance(fault.get("rev"), int) and fault["rev"] > (fault.get("acked_rev") or 0)


def fault_line(fault):
    c = fault.get("counts") or {}
    return (f"heartbeat delivery fault (rev {fault['rev']}) since {hhmm(parse_time(fault['since']))}: {c.get('cap', 0)} "
            f"cap, {c.get('hb_failed', 0)} failed heartbeat wakes, {c.get('undeliverable', 0)} undeliverable. "
            f"pheartbeat ack-fault {fault['rev']} acknowledges it")


def suspended_line(mode):
    since = parse_time(mode.get("since")) if mode.get("since") else None
    ids = " ".join(str(t)[:8] for t in mode.get("ticks") or [])
    return (f"working mode SUSPENDED since {hhmm(since) if since else '?'}: 3 failed ticks {ids}. pheartbeat detect or "
            f"pheartbeat working acknowledges it")


def _read_json(path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return None


def _enqueued_session(state, session):
    """Firing ids whose `enqueued` fact names this session: the ledger read plainly, a bad line skipped (read only)."""
    out = set()
    try:
        data = (state / "fired.jsonl").read_bytes()
    except OSError:
        return out
    for raw in data.split(b"\n"):
        try:
            f = json.loads(raw)
        except ValueError:
            continue
        if isinstance(f, dict) and f.get("fact") == "enqueued" and f.get("session") == session and f.get("hb"):
            out.add(f.get("firing"))
    return out


def heartbeat_read(event, ctx):
    import pwd

    try:
        data = _read_json(ctx.state / "heartbeat.json") or {}
        mode = _read_json(ctx.state / "heartbeat-mode.json") or {}
    except (OSError, ValueError) as e:
        raise Unreadable(f"heartbeat state can't be read ({type(e).__name__})")
    if not isinstance(data, dict) or not isinstance(mode, dict):
        raise Unreadable("heartbeat state isn't the heartbeat's")
    lines, ack = [], None
    session = event.get("session_id")
    quiet = data.get("quiet") if isinstance(data.get("quiet"), dict) else {}
    reports = {k: e for k, e in (data.get("reports_q") or {}).items() if isinstance(e, dict) and e.get("state") == "queued"}
    mine = _enqueued_session(ctx.state, session) if session else set()
    wake = next((w for w in mine if (quiet.get("state") == "queued" and quiet.get("wake") == w)
                 or any(e.get("wake") == w for e in reports.values())), None)
    if wake:
        keys = sorted(k for k, e in reports.items() if e.get("wake") == wake)
        is_quiet = quiet.get("state") == "queued" and quiet.get("wake") == wake
        user = pwd.getpwuid(os.geteuid()).pw_name
        if is_quiet:
            spell = parse_time(quiet.get("spell"))
            lines.append(f"heartbeat: this is quiet wake {wake[:8]}; the last work activity was at "
                         f"{hhmm(spell) if spell and spell.year > 1970 else 'never'}")
        if keys:
            kinds = {}
            for k in keys:
                kinds[k.split(':')[0]] = kinds.get(k.split(':')[0], 0) + 1
            lines.append(f"heartbeat: {len(keys)} reports for you (wake {wake[:8]}): "
                         + ", ".join(f"{n} {kind}" for kind, n in sorted(kinds.items()))
                         + f". journalctl -u persistence-heartbeat@{user} has the lines")
            claims = [k.split(':')[1] for k in keys if k.split(':')[0] in ("claim", "paused")]
            lines += [f"claim {c} (ptask show {c})" for c in dict.fromkeys(claims)]
            lines += [k for k in keys if k.split(':')[0] not in ("claim", "paused")]
        ack = json.dumps({"wake": wake, "keys": keys, "quiet": is_quiet}, sort_keys=True)
    if mode.get("mode") == "suspended":
        lines.insert(0, suspended_line(mode))
    if fault_showing(data.get("delivery_fault")):
        lines.insert(0, fault_line(data["delivery_fault"]))
    return Observation("heartbeat", ctx.now, lines=tuple(lines), ack=ack)


def heartbeat_ack(obs, event, ctx):
    import coordination

    got = json.loads(obs.ack)
    wake, at = got["wake"], ctx.now.isoformat(timespec="seconds")
    with coordination.lock(ctx.state, "heartbeat", wait=HB_ACK_WAIT):
        path = ctx.state / "heartbeat.json"
        data = json.loads(path.read_text(encoding="utf-8"))
        changed = False
        quiet = data.get("quiet")
        if got.get("quiet") and isinstance(quiet, dict) and quiet.get("state") == "queued" and quiet.get("wake") == wake:
            quiet.update(state="delivered", at=at)
            changed = True
        for key in got.get("keys", []):
            e = (data.get("reports_q") or {}).get(key)
            if isinstance(e, dict) and e.get("state") == "queued" and e.get("wake") == wake:
                e.update(state="delivered", at=at)
                changed = True
        if changed:
            write_atomic(path, json.dumps(data, indent=1, sort_keys=True))


SOURCES = {"prs": Source(prs, prs_ack), "deployed": Source(deployed), "switches": Source(switches),
           "claims": Source(claims_done, claims_ack), "heartbeat": Source(heartbeat_read, heartbeat_ack)}


# --- the runner, the one renderer, and the ack ---

def named(ctx):
    return list(dict.fromkeys(n.strip() for n in str(ctx.setting("PERSISTENCE_OBSERVE_SOURCES", "") or "").split(",") if n.strip()))


def observe(event, ctx):
    """The opted-in sources' Observations, in the order named. Each read runs on its own daemon thread, all under one
    deadline. A read past the deadline is reported unknown ("timed out") and its result, if it comes, is dropped: a
    thread can't be cancelled, but since reads write nothing, a late one changes nothing."""
    names = named(ctx)
    if not names:
        return []
    try:
        budget = float(ctx.setting("PERSISTENCE_OBSERVE_SOURCE_SECONDS", "4"))
    except ValueError:
        budget = 4.0
    results, threads, lock = {}, {}, threading.Lock()

    def run(name, source):
        try:
            got = source.read(event, ctx)
            if not isinstance(got, Observation) or got.status not in STATUSES:
                got = Observation(name, ctx.now, status="error", reason=f"returned {type(got).__name__}, not an Observation")
            elif got.source != name:
                got = replace(got, source=name)
        except Unreadable as e:
            got = Observation(name, ctx.now, status="unknown", reason=str(e))
        except Exception as e:
            got = Observation(name, ctx.now, status="error", reason=type(e).__name__)
        with lock:
            results.setdefault(name, got)

    for name in names:
        if name in SOURCES:
            threads[name] = threading.Thread(target=run, args=(name, SOURCES[name]), daemon=True)
            threads[name].start()
    deadline = datetime.now().timestamp() + budget
    for t in threads.values():
        t.join(max(0.0, deadline - datetime.now().timestamp()))
    out = []
    with lock:  # a late read finds its slot taken and is dropped
        for name in names:
            if name not in SOURCES:
                results[name] = Observation("observe", ctx.now, status="error", reason=f"no source named {name!r}")
            else:
                results.setdefault(name, Observation(name, ctx.now, status="unknown", reason="timed out"))
            out.append(results[name])
    return out


def bounded(obs):
    lines = [short(line, MAX_LINE_CHARS) for line in obs.lines]
    cut = max(0, len(lines) - MAX_LINES)
    return lines[:MAX_LINES], obs.truncated + cut


def render(observations, now=None):
    """The one place an Observation becomes text: "- " lines for the block. Returns [(observation, its lines)], so the
    caller can ack exactly the observations whose lines reached the session."""
    rendered = []
    for obs in observations:
        lines, more = bounded(obs)
        when = obs.freshness or obs.observed_at
        stale = (now or obs.observed_at) - when >= STALE
        head = []
        if obs.status != "ok":
            head.append(f"- {obs.source}: {obs.status} ({obs.reason or 'no reason given'})")
        elif stale:
            head.append(f"- {obs.source}: as of {age((now or obs.observed_at) - when)} ago" + (f" ({obs.reason})" if obs.reason else ""))
        body = [f"- {line}" for line in lines]
        if more:
            body.append(f"- {obs.source}: {more} more not shown")
        rendered.append((obs, head + body))
    return rendered


def acknowledge(observations, event, ctx):
    """After injection: run each source's ack for the observations handed over. An ack that fails is swallowed; the
    worst case is the same observation again next wake (at-least-once), never a lost one."""
    for obs in observations:
        source = SOURCES.get(obs.source)
        if source and source.ack and obs.ack is not None and obs.status == "ok":
            try:
                source.ack(obs, event, ctx)
            except Exception:
                pass
