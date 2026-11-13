"""Sources for the "Observed now" block (docs/design/observed-now-sources.md): what is so now, read at the wake and
never stored. A source is `source(event, ctx) -> list[str]`: a few "- " lines, [] when it has nothing to say, or it
raises (Unreadable with a reason, or anything else) and the runner says it couldn't read. Nothing runs unless the
store's own settings name it in PERSISTENCE_OBSERVE_SOURCES. Ember's event-buffer projection plugs in as one more
entry in SOURCES. Standard library only; nothing here imports hook.py (it hands what it needs in through ctx)."""
import json
import os
import re
import threading
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Callable, Mapping

INSTALLED = Path(__file__).resolve().parent  # the hook files sit together, as deploy-remote.sh installs them
HOOK_FILES = ("hook.py", "coordination.py", "claims.py", "cwd_exec.py", "run_line.py", "observed.py")


class Unreadable(Exception):
    """A source couldn't read what it observes; the reason goes in its line."""


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


# --- prs: open pull requests, each with its head and each reviewer's latest review ---

PRS_TTL = 120
PRS_SHOWN = 5


def github_get(url, token, timeout):
    request = urllib.request.Request(url, headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
                                                   "User-Agent": "persistence-observe"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def github_token(ctx):
    """The one `Token:` line of the token file; no other line is read into memory past the match."""
    path = Path(os.path.expanduser(ctx.setting("PERSISTENCE_OBSERVE_GITHUB_TOKEN_FILE", "~/secrets/accounts")))
    try:
        with path.open(encoding="utf-8") as f:
            for line in f:
                if line.startswith("Token:"):
                    return line[len("Token:"):].strip() or None
    except OSError:
        return None
    return None


def prs_lines(repo, pulls, reviews_of):
    if not pulls:
        return [f"- PRs: none open on {repo}"]
    out = []
    for pr in pulls[:PRS_SHOWN]:
        latest = {}
        for r in reviews_of.get(str(pr["number"]), []):
            if r.get("state") and r["state"] != "PENDING":
                latest[(r.get("user") or {}).get("login", "?")] = r["state"]
        said = ", ".join(f"{who} {state}" for who, state in latest.items()) or "no reviews"
        draft = " draft" if pr.get("draft") else ""
        out.append(f"- PR #{pr['number']}{draft} {short(pr.get('title'), 70)} (head {str(pr['head']['sha'])[:7]}): {said}")
    if len(pulls) > PRS_SHOWN:
        out.append(f"- PRs: {len(pulls) - PRS_SHOWN} more open")
    return out


def prs(event, ctx):
    repo = ctx.setting("PERSISTENCE_OBSERVE_GITHUB_REPO", "Mako88/Persistence")
    cache_path = ctx.state / "observed-prs.json"
    try:
        cache = json.loads(cache_path.read_text(encoding="utf-8"))
        cached_at = parse_time(cache["at"])
        if cache.get("repo") != repo:
            cache = None
    except (OSError, ValueError, KeyError, TypeError):
        cache = None
    if cache and ctx.now - cached_at < timedelta(seconds=PRS_TTL):
        return cache["lines"]
    token = github_token(ctx)
    if not token:
        raise Unreadable("no Token: line in the token file")
    try:
        base = f"https://api.github.com/repos/{repo}/pulls"
        pulls = github_get(f"{base}?state=open&per_page=30", token, 3)
        reviews = {str(p["number"]): github_get(f"{base}/{p['number']}/reviews?per_page=100", token, 3) for p in pulls[:PRS_SHOWN]}
        lines = prs_lines(repo, pulls, reviews)
    except Exception as e:
        if cache:
            return [f"- PRs: GitHub couldn't be read ({type(e).__name__}); cached {age(ctx.now - cached_at)} ago:"] + cache["lines"]
        raise Unreadable(f"GitHub: {type(e).__name__}, nothing cached")
    try:
        ctx.state.mkdir(parents=True, exist_ok=True)
        tmp = cache_path.with_suffix(".tmp")
        tmp.write_text(json.dumps({"at": ctx.now.isoformat(timespec="seconds"), "repo": repo, "lines": lines}), encoding="utf-8")
        tmp.replace(cache_path)
    except OSError:
        pass  # an unwritable cache costs a call next prompt, not the answer
    return lines


# --- deployed: installed hook files and the deployed store against main ---

def deployed(event, ctx):
    repo = os.path.expanduser(ctx.setting("PERSISTENCE_OBSERVE_REPO", "~/work/Persistence"))
    main = ctx.run(["git", "rev-parse", "--verify", "-q", "main"], repo).strip()
    if not main:
        raise Unreadable(f"no main branch in {repo}")
    out = []
    tree = {}
    for line in ctx.run(["git", "ls-tree", "main", "scripts/claude-code/"], repo).splitlines():
        meta, _, path = line.partition("\t")
        if meta.split()[1:2] == ["blob"]:
            tree[Path(path).name] = meta.split()[2]
    present = [f for f in HOOK_FILES if (INSTALLED / f).exists()]
    hashes = ctx.run(["git", "hash-object", *[str(INSTALLED / f) for f in present]], repo).split() if present else []
    if len(hashes) != len(present):
        out.append("- hooks: couldn't read (git hash-object failed)")
    else:
        differ = [f for f, h in zip(present, hashes) if tree.get(f) != h]
        missing = [f for f in HOOK_FILES if f not in present and f in tree]
        if not differ and not missing:
            out.append(f"- hooks: match main ({main[:7]})")
        else:
            out.append("- hooks: " + "; ".join(filter(None, [
                f"{', '.join(differ)} differ from main ({main[:7]})" if differ else "",
                f"missing: {', '.join(missing)}" if missing else ""])))
    marker = Path(ctx.setting("PERSISTENCE_OBSERVE_STORE_MARKER", "/opt/persistence/store/DEPLOYED"))
    try:
        sha = marker.read_text(encoding="utf-8").split()[0]
    except (OSError, IndexError):
        out.append(f"- store: couldn't read which commit (no {marker})")
        return out
    behind = ctx.run(["git", "rev-list", "--count", f"{sha}..main"], repo).strip()
    if not behind.isdigit():
        out.append(f"- store: deployed {sha[:7]}, not in {repo}'s history")
    elif behind == "0":
        out.append(f"- store: deployed {sha[:7]}, at main")
    else:
        out.append(f"- store: deployed {sha[:7]}, {behind} commit{'s' if behind != '1' else ''} behind main ({main[:7]})")
    return out


# --- switches: the recall and query switches that are set, on the hook's side and the store's ---

HOOK_SWITCHES = ("PERSISTENCE_RECALL_COUNT", "PERSISTENCE_RECALL_SAID", "PERSISTENCE_RECALL_WANDER")
STRIP = "PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE"
STORE_SWITCH = re.compile(r"\bPERSISTENCE_(?:RECALL_\w+|EMBEDDING_QUERY\w+|EMBEDDING_QUALITYWEIGHT)=\S*")


def switches(event, ctx):
    # Read the way recall reads them: from this process's environment (hook.py's recall uses os.environ).
    set_ = [f"{n[len('PERSISTENCE_'):]}={ctx.env[n]}" for n in HOOK_SWITCHES if ctx.env.get(n)]
    on = str(ctx.env.get(STRIP, "")).strip().lower() in ("1", "true", "yes", "on")
    out = ["- recall switches (hook): " + ", ".join(set_ + ["STRIP_ROOM_PREAMBLE " + ("on" if on else "off")])]
    unit = ctx.setting("PERSISTENCE_OBSERVE_STORE_UNIT", "")
    if unit:
        shown = ctx.run(["systemctl", "show", "-p", "Environment", unit], None, 5)
        found = STORE_SWITCH.findall(shown)
        out.append(f"- recall switches (store {unit}): " + (", ".join(s[len("PERSISTENCE_"):] for s in found) if found else
                                                              "none set" if shown.startswith("Environment=") else "couldn't read"))
    return out


# --- claims: work sessions that finished since this session last looked ---

CLAIMS_SHOWN = 4
FIRST_LOOK = timedelta(hours=6)


def claims_done(event, ctx):
    session = event.get("session_id") or "unknown"
    cursor = ctx.state / f"{session}.observed-claims"
    try:
        since = parse_time(cursor.read_text(encoding="utf-8").strip())
    except (OSError, ValueError):
        since = ctx.now - FIRST_LOOK
    found = []
    for path in (ctx.state / "tasks").glob("*.json"):
        try:
            record = json.loads(path.read_text(encoding="utf-8"))
            when = parse_time(record["updated"])
        except (OSError, ValueError, KeyError, TypeError):
            continue
        if record.get("status") in ("done", "abandoned") and record.get("owner") != session and since < when <= ctx.now:
            found.append((when, record))
    try:
        ctx.state.mkdir(parents=True, exist_ok=True)
        cursor.write_text(ctx.now.isoformat(timespec="seconds"), encoding="utf-8")
    except OSError:
        pass
    found.sort(key=lambda item: item[0], reverse=True)
    out = [f"- work {r['status']} {hhmm(w)}: claim {str(r.get('id', ''))[:8]} {short(r.get('task'), 70)}"
           + (f" — {short(r['outcome'], 60)}" if r.get("outcome") else "") for w, r in found[:CLAIMS_SHOWN]]
    if len(found) > CLAIMS_SHOWN:
        out.append(f"- work: {len(found) - CLAIMS_SHOWN} more finished since you last looked (`ptask show <id>`)")
    return out


SOURCES = {"prs": prs, "deployed": deployed, "switches": switches, "claims": claims_done}


def lines(event, ctx):
    """The opted-in sources' lines, in the order named. Each runs on its own daemon thread, all under one deadline, so a
    slow source costs its own line and not the block."""
    names = [n.strip() for n in str(ctx.setting("PERSISTENCE_OBSERVE_SOURCES", "") or "").split(",") if n.strip()]
    if not names:
        return []
    try:
        budget = float(ctx.setting("PERSISTENCE_OBSERVE_SOURCE_SECONDS", "4"))
    except ValueError:
        budget = 4.0
    results, threads = {}, {}

    def run(name, source):
        try:
            results[name] = list(source(event, ctx) or [])
        except Unreadable as e:
            results[name] = [f"- {name}: couldn't read ({e})"]
        except Exception as e:
            results[name] = [f"- {name}: couldn't read ({type(e).__name__})"]

    for name in dict.fromkeys(names):
        if name in SOURCES:
            threads[name] = threading.Thread(target=run, args=(name, SOURCES[name]), daemon=True)
            threads[name].start()
    deadline = datetime.now().timestamp() + budget
    for t in threads.values():
        t.join(max(0.0, deadline - datetime.now().timestamp()))
    out = []
    for name in dict.fromkeys(names):
        if name not in SOURCES:
            out.append(f"- observe: no source named {name!r}")
        else:
            out += results.get(name) or ([] if not threads[name].is_alive() else [f"- {name}: couldn't read (timed out)"])
    return out
