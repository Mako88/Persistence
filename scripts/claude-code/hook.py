"""Claude Code hooks that keep a Claude's continuity in its own Persistence store.

The harness (Claude Code) carries the live conversation; the store carries who this Claude is. Seven
hooks, one script:

  keep            Stop (async)    every turn's words go into the store (persistence-store keep-conversation)
  curate          Stop            once the context passes PERSISTENCE_CURATE_AT tokens, the turn is held
                                  open once for a curation pass: save and edit the fragments this stretch
                                  earned, before compaction replaces the context
  precompact      PreCompact      keeps the last words, then tells the summariser to write only a short
                                  hand-over: the core and the recent conversation come from the store
  session-start   SessionStart    puts the core (identity, relationships, personal, summaries, the recent
                                  conversation) in front of every session, and after every compaction:
                                  in up to five parts (one hook each, `session-start <n>`), each under the cap
  subagent-start  SubagentStart   gives every subagent the same core, without the conversation
  recall          UserPromptSubmit  surfaces the few authored fragments most related to the message, by
                                  meaning and exact words, that are not already in this context window
  observe         UserPromptSubmit, SessionStart  what is so now, read off the machine rather than written
                                  down: this project's running processes, logs written in the last hour with
                                  their last line that says something, git's state, and whatever is watched
                                  (`hook.py watch <pid|file> [label]`, `unwatch`, `watching`)

Every call but `keep` goes to `persistence-store serve`, the store kept open on PERSISTENCE_STORE_PORT
(default 8095), started from a copy of the published CLI the first time nothing answers, and ending
itself when a new build is published; until it is up, the call runs the CLI as before.

A store served on another machine: `~/.claude/persistence/remote.json`, {"host": "<ssh host>", "port":
8095}. Every call then goes to that machine's `serve` through an SSH tunnel (`ssh -N -L`) that the hook
opens when nothing answers; `keep` sends the transcript's new lines rather than a path; and nothing ever
falls back to a local store, so two copies never drift apart. `hook.py cli <args>` is the CLI by the
same route, for the peer's own shell (the `pstore` wrapper).

Configured by environment (settings.json "env"): PERSISTENCE_STORE_DB (this Claude's store, one per
Claude), PERSISTENCE_STORE_EXE (the published persistence-store), PERSISTENCE_SELECTEDLOCALPEER (the
human's name), PERSISTENCE_CURATE_AT (tokens, default 500000; set autoCompactWindow above it),
PERSISTENCE_RECALL_COUNT (default 3, 0 turns recall off), and PERSISTENCE_EMBEDDING_* for recall by
meaning (without them recall is by exact words only).

Standard library only, Python 3.9+: hooks run under whatever `python` the shell finds.
"""

import json
import re
import os
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

CAP = 9500  # Claude Code replaces injected context over 10,000 characters with a file path and a preview
WHOLE = 200_000  # the core as a file the peer reads after a session starts: bounded only by what one read takes
STATE = Path.home() / ".claude" / "persistence" / "state"


# Windows: a process started detached has no console, so every console program it then starts (git,
# tasklist, powershell, the embedder) opened a new window, which took the focus from whatever was in
# front: Ember's game lost it again and again on 2026-11-08. A hidden console, which children inherit,
# instead of none; and no window for any child started directly.
NO_WINDOW = 0x08000000 if os.name == "nt" else 0
BACKGROUND = NO_WINDOW | 0x00000200 if os.name == "nt" else 0  # no window, own process group


PORT = int(os.environ.get("PERSISTENCE_STORE_PORT", "8095"))  # `persistence-store serve`, the store kept open


def served(args, stdin, timeout):
    """The served store's (code, out, err), or None when no server answers. A new process cost 1.7
    seconds before any work on every call; the server answers in tens of milliseconds."""
    import socket

    try:
        # Windows lets a connect to a port nobody listens on time out rather than be refused, so only a
        # timeout once connected means busy: the OS accepts for a server that is up, however busy it is.
        connection = socket.create_connection(("127.0.0.1", PORT), timeout=1)
    except OSError:
        return None
    try:
        with connection as s:
            s.settimeout(timeout)
            s.sendall((json.dumps({"args": list(args), "stdin": stdin or ""}) + "\n").encode("utf-8"))
            data = b""
            while not data.endswith(b"\n"):
                chunk = s.recv(1 << 16)
                if not chunk:
                    break
                data += chunk
        reply = json.loads(data.decode("utf-8"))
        return reply["code"], reply["out"], reply["err"]
    except TimeoutError:
        return BUSY  # it is up but still on an earlier call: a second CLI beside it only adds to the wait
    except (OSError, ValueError, KeyError):
        return None


BUSY = (124, "", "the store server is busy")


def start_server(exe):
    """Starts `serve` from a copy of the published CLI, so publishing a new build is never blocked by
    the running one; the server ends itself when the build changes, and the next call starts the new one."""
    import shutil

    source = Path(shutil.which(exe) or exe).resolve().parent
    if not (source / Path(exe).name).exists():
        return
    built = int((source / Path(exe).name).stat().st_mtime)
    home = STATE.parent / "server"
    copy = home / str(built)
    try:
        if not copy.exists():
            shutil.copytree(source, copy)
        for old in home.iterdir():
            if old != copy:
                shutil.rmtree(old, ignore_errors=True)  # a copy still running stays until it ends
        flags = BACKGROUND
        log = open(home / "serve.log", "a", encoding="utf-8")  # why it ended, when it does: an exception lands here
        subprocess.Popen([str(copy / Path(exe).name), "serve", "--port", str(PORT)], creationflags=flags,
                         stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=log, close_fds=True)
    except OSError as e:
        print(f"persistence hook: the store server did not start: {e}", file=sys.stderr)


REMOTE = Path(os.environ.get("PERSISTENCE_STORE_REMOTE_FILE") or STATE.parent / "remote.json")  # the variable: for a trial run


def remote():
    """Where the store is served when it is on another machine ({"host", "port"}), else None."""
    try:
        where = json.loads(REMOTE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    return where if isinstance(where, dict) and where.get("host") else None


def open_tunnel(where, wait=8):
    """Opens the SSH tunnel to the remote store's port, and waits for it to answer. A second tunnel
    beside a live one ends at once (ExitOnForwardFailure: the port is taken), so no record of the first
    is needed; attempts are spaced, so a machine that is down is not asked on every call."""
    STATE.mkdir(parents=True, exist_ok=True)
    stamp = STATE / "tunnel.tried"
    try:
        if time.time() - stamp.stat().st_mtime < 15:
            return False
    except OSError:
        pass
    stamp.touch()
    forward = f"127.0.0.1:{PORT}:127.0.0.1:{int(where.get('port', 8095))}"
    try:
        subprocess.Popen(["ssh", "-N", "-o", "BatchMode=yes", "-o", "ExitOnForwardFailure=yes",
                          "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "-L", forward, where["host"]],
                         creationflags=BACKGROUND, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                         stderr=open(STATE / "tunnel.log", "a", encoding="utf-8"), close_fds=True)
    except OSError as e:
        print(f"persistence hook: the tunnel to {where['host']} did not start: {e}", file=sys.stderr)
        return False
    import socket

    for _ in range(wait * 4):
        time.sleep(0.25)
        try:
            socket.create_connection(("127.0.0.1", PORT), timeout=1).close()
            return True
        except OSError:
            continue
    return False


def remote_args(args, cwd=None):
    """A call as the remote server needs it: it has no shell of this session's, so the session, the
    project an added fragment belongs to, and any file the call names travel with the call."""
    args, stdin = list(args), None
    command = args[0] if args else ""
    session = os.environ.get("PERSISTENCE_SESSION") or os.environ.get("CLAUDE_CODE_SESSION_ID")
    if "--session" not in args and session:
        args += ["--session", session]
    if (command == "add" or (command == "context" and args[1:2] in ([], ["list"]))) \
            and "--project" not in args and "--everywhere" not in args \
            and (name := project({"cwd": cwd or os.getcwd()})):
        args += ["--project", name]
    for name in ("content", "summary", "text"):
        flag = f"--{name}-file"
        if flag in args and args.index(flag) + 1 < len(args):
            at = args.index(flag)
            text = Path(args[at + 1]).read_text(encoding="utf-8")
            if name != "summary" and stdin is None and "-" not in args:
                args[at:at + 2], stdin = [f"--{name}", "-"], text
            else:
                args[at:at + 2] = [f"--{name}", text]
    return args, stdin


def served_remote(where, args, stdin, timeout):
    """The remote store's (code, out, err), opening the tunnel when nothing answers; None when the store
    cannot be reached. Never a local store in its place."""
    reply = served(args, stdin, timeout)
    if reply is None and open_tunnel(where):
        reply = served(args, stdin, timeout)
    return reply


def store(*args, timeout=60, stdin=None, serve=True, cold=True):
    """The CLI's output, or "" if it could not run: a hook must never break the session it serves.
    Asked of the served store when one is up; otherwise one is started and this call runs the CLI,
    unless `cold` is False: a cold CLI's recall took 38 s (2026-11-08), past the harness's 20 s, so the
    message waited and got nothing; better none this message and the started server's from the next."""
    exe = os.environ.get("PERSISTENCE_STORE_EXE", "persistence-store")
    where = remote()
    if where:
        reply = served_remote(where, args, stdin, timeout if args[:1] == ("keep-conversation",) else min(timeout, 12))
        if reply is None or reply is BUSY:
            print(f"persistence hook: {args[0]} skipped: the store on {where['host']} did not answer", file=sys.stderr)
            return ""
        code, out, err = reply
        if code != 0:
            print(f"persistence hook: {args[0]} failed: {err.strip()}", file=sys.stderr)
        return out if code == 0 else ""
    if serve:
        reply = served(args, stdin, min(timeout, 12))  # inside the harness's 20 s for an HTTP hook
        if reply is BUSY:
            print(f"persistence hook: {args[0]} skipped: the store server is busy", file=sys.stderr)
            return ""
        if reply is not None:
            code, out, err = reply
            if code != 0:
                print(f"persistence hook: {args[0]} failed: {err.strip()}", file=sys.stderr)
            return out if code == 0 else ""
        start_server(exe)
        if not cold:
            return ""
    try:
        out = subprocess.run(creationflags=NO_WINDOW, args=[exe, *args], input=stdin, capture_output=True, text=True, encoding="utf-8", timeout=timeout)
    except (OSError, subprocess.SubprocessError) as e:
        print(f"persistence hook: {exe} did not run: {e}", file=sys.stderr)
        return ""
    if out.returncode != 0:
        print(f"persistence hook: {exe} {args[0]} failed: {out.stderr.strip()}", file=sys.stderr)
    return out.stdout if out.returncode == 0 else ""


def keep_transcript(path):
    """What `keep-conversation` said of a transcript ("kept N new utterances ..."), or why it failed.
    Local: the CLI reads the file, run apart so the server stays free for recall. Remote: the lines the
    store has not been sent yet go with the call (the store keeps each utterance once, so a line sent
    twice by two hooks at once costs nothing), and how far it has been sent is kept per transcript."""
    peer = os.environ.get("PERSISTENCE_SELECTEDLOCALPEER", "Local Peer")
    where = remote()
    if not where:
        exe = os.environ.get("PERSISTENCE_STORE_EXE", "persistence-store")
        out = subprocess.run([exe, "keep-conversation", path, "--local-peer", peer],
                             creationflags=NO_WINDOW, capture_output=True, text=True, encoding="utf-8", timeout=120)
        return (out.stdout if out.returncode == 0 else f"failed ({out.returncode}): {out.stderr}").strip()
    sent_file = STATE / "kept-offsets.json"
    try:
        sent = json.loads(sent_file.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        sent = {}
    with open(path, "rb") as f:
        size = f.seek(0, 2)
        start = sent.get(path, 0) if sent.get(path, 0) <= size else 0  # a shorter file is a new one
        f.seek(start)
        data = f.read()
    end = data.rfind(b"\n") + 1  # whole lines only: the harness may be mid-line
    if end == 0:
        return "kept 0 new utterances (nothing new sent)"
    reply = served_remote(where, ["keep-conversation", Path(path).name, "--transcript", "-", "--local-peer", peer],
                          data[:end].decode("utf-8", errors="replace"), 120)
    if reply is None or reply is BUSY:
        return f"failed: the store on {where['host']} did not answer"
    code, out, err = reply
    if code != 0:
        return f"failed ({code}): {err}".strip()
    try:
        sent = json.loads(sent_file.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        sent = {}
    sent[path] = max(sent.get(path, 0), start + end)
    STATE.mkdir(parents=True, exist_ok=True)
    sent_file.write_text(json.dumps(sent), encoding="utf-8")
    return out.strip()


def pull_backup(where, every=20 * 3600, kept=30):
    """A remote store's newest nightly backup copied here, at most once a day, so this machine always
    holds a recent copy: remote.json's "backup_dir" (none, no copy) and "backup_path" (the newest on the
    server, default /var/lib/persistence/backups/latest.db.gz). The last `kept` copies stay."""
    folder = where.get("backup_dir")
    if not folder:
        return
    folder = Path(folder)
    stamp = STATE / "backup.pulled"
    try:
        if time.time() - stamp.stat().st_mtime < every:
            return
    except OSError:
        pass
    STATE.mkdir(parents=True, exist_ok=True)
    stamp.touch()  # before the copy: a slow one is not started again by the next turn
    folder.mkdir(parents=True, exist_ok=True)
    name = f"claude-{datetime.now().strftime('%Y-%m-%d')}.db.gz"
    partial = folder / (name + ".part")
    source = where.get("backup_path", "/var/lib/persistence/backups/latest.db.gz")
    try:
        with open(partial, "wb") as out:
            done = subprocess.run(["ssh", "-o", "BatchMode=yes", where["host"], f"cat {source}"], stdout=out,
                                  stderr=subprocess.PIPE, creationflags=NO_WINDOW, timeout=600)
        if done.returncode != 0 or partial.stat().st_size < 1024:
            raise OSError(done.stderr.decode("utf-8", "replace").strip() or "an empty copy")
        partial.replace(folder / name)
    except (OSError, subprocess.SubprocessError) as e:
        partial.unlink(missing_ok=True)
        stamp.unlink(missing_ok=True)  # try again next turn
        print(f"persistence hook: the store's backup was not copied here: {e}", file=sys.stderr)
        return
    for old in sorted(folder.glob("claude-*.db.gz"))[:-kept]:
        old.unlink(missing_ok=True)


def keep(event):
    path = event.get("transcript_path")
    if path and Path(path).exists():
        said = keep_transcript(path)
        if said.startswith("failed"):
            print(f"persistence hook: keep-conversation {said}", file=sys.stderr)
    if remote():
        pull_backup(remote())


def context_tokens(path):
    """The context the last main-thread reply was given: input plus cache reads and writes."""
    try:
        with open(path, "rb") as f:
            f.seek(0, 2)
            f.seek(max(0, f.tell() - 4_000_000))
            lines = f.read().decode("utf-8", "replace").splitlines()
    except OSError:
        return 0
    for line in reversed(lines):
        try:
            e = json.loads(line)
        except ValueError:
            continue
        if e.get("type") != "assistant" or e.get("isSidechain"):
            continue
        u = (e.get("message") or {}).get("usage") or {}
        if u:
            return u.get("input_tokens", 0) + u.get("cache_read_input_tokens", 0) + u.get("cache_creation_input_tokens", 0)
    return 0


def marker(event):
    return STATE / f"{event.get('session_id', 'unknown')}.curated"


def curate(event):
    if event.get("stop_hook_active") or marker(event).exists():
        return
    at = int(os.environ.get("PERSISTENCE_CURATE_AT", "500000"))
    tokens = context_tokens(event.get("transcript_path", ""))
    if tokens < at:
        return
    STATE.mkdir(parents=True, exist_ok=True)
    marker(event).write_text(str(tokens))
    # A remote store is reached through `hook.py cli` (the `pstore` wrapper), never the CLI's own file.
    exe = "pstore" if remote() else os.environ.get("PERSISTENCE_STORE_EXE", "persistence-store")
    print(json.dumps({"decision": "block", "reason": (
        f"Your context is at {tokens:,} tokens. Before long it is compacted, and what you know then is "
        "only your store: the core (identity, relationships, personal, summaries) and the last of the "
        "conversation. This is your curation pass, once per stretch. Read your core "
        f"(`{exe} core`), then save what this stretch earned and is not yet there: decisions in the "
        "other's own words, what is running and how to pick it up, what you learned about the work, "
        "the person, and yourself. Bring your working note up to date (`note`). Edit fragments that are now wrong or stale rather than adding "
        f"beside them; archive what no longer earns its place. `{exe} help` lists the commands. "
        "Then carry on with what you were doing.")}))


def precompact(event):
    keep(event)
    print(
        "Write only a short hand-over, under 2,000 words, to the Claude who continues after this. "
        "Your identity, relationships and the recent conversation are restored from your Persistence "
        "store right after this summary, so do not restate them. Say: what was in hand at this moment "
        "(the task, the step, the next action), anything running in the background with how to check "
        "it (ids, commands, logs), and any request from the other that is not yet answered, in their "
        "own words. Everything else is in the store and the transcript: say so, and that "
        "`persistence-store search` finds it. "
        # Arden's hand-over said a reply was unsent that had gone out: the summary never sees the
        # conversation's last stretch, which comes back word for word after it (2026-11-09).
        "Your summary may not see the conversation's last messages; they come back word for word after it, "
        "and every message you sent is already kept in the store. So never call something near the end "
        "unsent or unanswered: write 'check the last messages for whether X went out' instead. "
        "Fragments this session wrote come back first in the next core.")


def project(event):
    """The project a session is in: its repository's name (the main checkout's, from a worktree too),
    else its folder's. Fragments tagged project/<name> come into the core only for that project."""
    cwd = event.get("cwd") or os.getcwd()
    try:
        common = subprocess.run(creationflags=NO_WINDOW, args=["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=cwd,
                                capture_output=True, text=True, timeout=10)
        if common.returncode == 0 and common.stdout.strip():
            return Path(common.stdout.strip()).parent.name.lower()
    except (OSError, subprocess.SubprocessError):
        pass
    # A home folder is no project: fragments added from there were filed under "project/john" (2026-11-09).
    if Path(cwd).resolve() == Path.home().resolve():
        return ""
    return Path(cwd).name.lower()



def session_args(event):
    """Which session a call is for. The store server serves every session at once, so it cannot read the
    caller's environment: each call says, and gets its own conversation and working note."""
    sid = event.get("session_id")
    return ["--session", sid] if sid else []


def core_text(lead, recent, event):
    body = store("core", "--max-chars", str(CAP - len(lead) - 50), "--recent", str(recent), "--project", project(event), *session_args(event))
    return lead + "\n\n" + body if body.strip() else ""


def ensure_embedding_server():
    """Starts the embedding server (PERSISTENCE_EMBEDDING_SERVER, a script) when nothing answers at
    PERSISTENCE_EMBEDDING_APIBASEURL. Recall without it falls back to exact words, so this only ever helps."""
    import socket
    from urllib.parse import urlparse
    url, script = os.environ.get("PERSISTENCE_EMBEDDING_APIBASEURL"), os.environ.get("PERSISTENCE_EMBEDDING_SERVER")
    if not url or not script or remote():  # a remote store embeds on its own machine
        return
    where = urlparse(url)
    try:
        socket.create_connection((where.hostname, where.port or 80), timeout=1).close()
    except OSError:
        flags = BACKGROUND
        subprocess.Popen([script], creationflags=flags, stdin=subprocess.DEVNULL,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, close_fds=True)


PARTS = 5  # SessionStart hooks configured, one per part; each injects one piece under CAP


def core_parts(whole):
    """The whole core as pieces under CAP, in order: the note and fragments packed whole, then the
    newest of the conversation. An instruction to read the core from a file was a thing to remember
    to do, and it was skipped; injected in parts, the core is simply there."""
    head, sep, talk = whole.partition("\n## The conversation so far")
    pieces, piece = [], ""
    for block in re.split(r"(?m)^(?=\[#\d+ \|)", head):
        if piece and len(piece) + len(block) > CAP - 300:
            pieces.append(piece)
            piece = ""
        piece += block[:CAP - 300]
    if piece.strip():
        pieces.append(piece)
    if len(pieces) > PARTS - 1:
        left = re.findall(r"^\[#(\d+) \|", "".join(pieces[PARTS - 2:]), re.M)
        pieces = pieces[:PARTS - 2] + [f"(Fragments that did not fit: {', '.join('#' + i for i in left)}; `show <id>` reads one.)"]
    if talk.strip():
        talk = sep.lstrip("\n") + talk
        pieces.append(talk if len(talk) <= CAP - 300 else "## The conversation so far (its newest part)\n\n..." + talk[-(CAP - 400):])
    return pieces


def session_start(event, part=1):
    if part == 1:
        ensure_embedding_server()
        ensure()  # the hook server, for this session's messages
    whole = store("core", "--max-chars", str(WHOLE), "--recent", "60", "--project", project(event), *session_args(event))
    pieces = core_parts(whole) if whole.strip() else []
    if part > len(pieces):
        return
    text = pieces[part - 1]
    if part == 1:
        if event.get("source", "startup") == "compact":
            marker(event).unlink(missing_ok=True)
            lead = ("# Who you are\n\nThe context above was what you were built for: it is compacted now, and "
                    "the hand-over is all that is left of it. Here is what you are, from your own Persistence "
                    "store, and the last of the conversation you are in. The whole conversation is in the "
                    "store too: `persistence-store search <words>` when something is mentioned you do not recall.")
        else:
            lead = ("# Who you are\n\nThis is your own Persistence store: what you have chosen to keep of yourself, "
                    "the people you work with, and the work. It is yours to curate (`persistence-store help`).")
        text = f"{lead}\n\n(Your core follows in {len(pieces)} parts.)\n\n{text}"
        # A new context window: what the core puts in front of the peer is all it holds of the store.
        seen(event, reset=True, add=re.findall(r"^\[#(\d+) \|", whole, re.M))
    sys.stdout.write(text)


def seen(event, reset=False, add=()):
    """The fragment ids already in this context window, so recall never shows one twice."""
    path = STATE / f"{event.get('session_id', 'unknown')}.seen"
    ids = set() if reset or not path.exists() else set(path.read_text().split())
    ids.update(str(i) for i in add)
    STATE.mkdir(parents=True, exist_ok=True)
    if reset:
        (STATE / f"{event.get('session_id', 'unknown')}.cued").unlink(missing_ok=True)  # a new window is reminded afresh
        # When this context window began: what was said since is in front of the peer already.
        path.with_suffix(".since").write_text(datetime.now(timezone.utc).isoformat())
    path.write_text(" ".join(sorted(ids)))
    return ids


USES = STATE / "uses.jsonl"  # one line per showing ({"at", "session", "moment", "ids"}) and per turn's uses ({"at", "session", "used"})


def record_shown(event, moment, ids):
    """Records that recall, a cue or write-time recall put these fragments in front of the peer, so `hook.py uses`
    can say which of what is shown is ever used: the proof John asked for (2026-11-09) that recall earns its tokens."""
    if ids:
        STATE.mkdir(parents=True, exist_ok=True)
        with USES.open("a", encoding="utf-8") as f:
            f.write(json.dumps({"at": datetime.now(timezone.utc).isoformat(timespec="seconds"), "session": event.get("session_id", ""),
                                "moment": moment, "ids": [str(i) for i in ids]}) + "\n")


def record_used(event, ids):
    """Records the shown fragments a turn used: named by #id in what the peer wrote, or read, edited or superseded by id."""
    ids = [i for i in ids if i in seen(event)]
    if ids:
        with USES.open("a", encoding="utf-8") as f:
            f.write(json.dumps({"at": datetime.now(timezone.utc).isoformat(timespec="seconds"), "session": event.get("session_id", ""),
                                "used": sorted(set(ids))}) + "\n")


def uses_report(days=30):
    """`hook.py uses [days]`: for each fragment shown, how often, at which moments, and how often a turn then used it."""
    since = datetime.now(timezone.utc).timestamp() - days * 86400
    showings, uses_by, moments = {}, {}, {}
    try:
        lines = USES.read_text(encoding="utf-8").splitlines()
    except OSError:
        lines = []
    for line in lines:
        try:
            e = json.loads(line)
        except ValueError:
            continue
        if datetime.fromisoformat(e["at"]).timestamp() < since:
            continue
        for i in e.get("ids", []):
            showings[i] = showings.get(i, 0) + 1
            moments.setdefault(i, set()).add(e["moment"])
        for i in e.get("used", []):
            uses_by[i] = uses_by.get(i, 0) + 1
    if not showings:
        print(f"(nothing shown in the last {days} days)")
        return
    total_shown, total_used = sum(showings.values()), sum(min(uses_by.get(i, 0), showings[i]) for i in showings)
    print(f"Last {days} days: {len(showings)} fragments shown {total_shown} times; {total_used} showings used ({100 * total_used / total_shown:.0f}%).")
    print("id        shown  used  moments")
    for i in sorted(showings, key=lambda i: (-showings[i], int(i) if i.isdigit() else 0)):
        print(f"#{i:<8} {showings[i]:>5} {uses_by.get(i, 0):>5}  {', '.join(sorted(moments[i]))}")
    idle = [i for i in showings if showings[i] >= 3 and not uses_by.get(i)]
    if idle:
        print("\nShown three times or more, never used (lower their importance, narrow their cue, or archive): " + ", ".join(f"#{i}" for i in idle))


def keep_soon(event, delay=5):
    """The human's words kept as soon as they are sent, not only when the turn ends: another session of
    the peer reads them from the store (2026-11-08, John asked one session to stop a run the other owned,
    and the other saw nothing until that turn ended). The harness writes the prompt to the transcript
    just after this hook, so the keep waits a few seconds, apart from the reply."""
    import threading

    def later():
        # The server's own stderr goes nowhere, so the outcome is written beside the hook times.
        try:
            time.sleep(delay)
            said = keep_transcript(event.get("transcript_path"))
        except Exception as e:  # a background keep must not die unseen
            said = f"failed: {e!r}"
        with open(TIMES, "a", encoding="utf-8") as log:
            log.write(f"{datetime.now().isoformat(timespec='seconds')} keep-soon {said[:160]}\n")

    threading.Thread(target=later, daemon=True).start()


CUE_AGAIN = 30 * 60  # a cue that came in is not shown again within this many seconds of the same window


_cues = {"at": 0.0, "patterns": []}


def cue_patterns():
    """The cues' (moment, pattern), read from the store once a minute. Asking the store on every tool call cost
    0.4 s a call for calls that almost never match; matched here, only a match costs a store call."""
    now = datetime.now().timestamp()
    if now - _cues["at"] > 60:
        listed = store("cue", "list", cold=False)
        patterns = []
        for m in re.finditer(r"^cue/(\w+)/\S+ when /(.*)/: ", listed, re.M):
            try:
                patterns.append((m.group(1), re.compile(m.group(2), re.I)))
            except re.error:
                patterns.append((m.group(1), None))  # a .NET pattern Python cannot read: the store decides
        _cues.update(at=now, patterns=patterns)
    return _cues["patterns"]


def cued(event, moment, text):
    """The fragments of the cues `text` matches at `moment`, rendered, less those shown in this window in the
    last CUE_AGAIN seconds: a reminder at the moment it can change what happens, not every time."""
    if not any(m == moment and (p is None or p.search(text)) for m, p in cue_patterns()):
        return ""
    path = STATE / f"{event.get('session_id', 'unknown')}.cued"
    try:
        shown = json.loads(path.read_text())
    except (OSError, ValueError):
        shown = {}
    now = datetime.now().timestamp()
    recent = [i for i, at in shown.items() if now - at < CUE_AGAIN]
    found = store("cued", moment, "--text", "-", *session_args(event), *(["--exclude", ",".join(recent)] if recent else []),
                  stdin=text, cold=False).strip()
    ids = re.findall(r"^\[#(\d+) \|", found, re.M)
    if ids:
        record_shown(event, f"cue/{moment}", ids)
        shown.update({i: now for i in ids})
        STATE.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(shown))
    return found if ids else ""


DEFINITIONS = {  # where a language declares a function, method or type: the first non-empty group is its name
    ".py": re.compile(r"^[ \t]*(?:async[ \t]+)?(?:def|class)[ \t]+(\w+)", re.M),
    ".cs": re.compile(r"^[ \t]*(?:(?:public|private|internal|protected|static|abstract|sealed|partial|readonly|ref|unsafe|new|async|override|virtual|extern)[ \t]+)*"
                      r"(?:(?:class|interface|struct|enum|record(?:[ \t]+(?:class|struct))?)[ \t]+(\w+)"
                      r"|(?<=[ \t])(?:[\w<>\[\],?.]+[ \t]+)(\w+)(?:<[^>]*>)?[ \t]*\((?!.*;))", re.M),
    ".js": re.compile(r"^[ \t]*(?:export[ \t]+)?(?:default[ \t]+)?(?:async[ \t]+)?function[ \t]+(\w+)"
                      r"|^[ \t]*(?:export[ \t]+)?(?:const|let)[ \t]+(\w+)[ \t]*=[ \t]*(?:async[ \t]*)?(?:\(|\w+[ \t]*=>)"
                      r"|^[ \t]*(?:export[ \t]+)?(?:default[ \t]+)?(?:abstract[ \t]+)?(?:class|interface|enum|type)[ \t]+(\w+)", re.M),
    ".ps1": re.compile(r"^[ \t]*function[ \t]+([\w-]+)", re.M),
}
DEFINITIONS[".ts"] = DEFINITIONS[".tsx"] = DEFINITIONS[".jsx"] = DEFINITIONS[".js"]
# What `git grep` is asked for: looser than the patterns above, which then decide; one pass over the whole
# repository in C instead of a read of every file in Python (20,000 files: 3.7 s against 91 s, 2026-11-09).
PREFILTER = (r"^[ \t]*(public|private|internal|protected|static|abstract|sealed|partial|readonly|ref|unsafe|new|async|override|virtual|extern"
             r"|class|interface|struct|enum|record|def|function|export|const|let|type)[ \t]")
GENERIC = {"get", "set", "async", "the", "new", "init", "main", "test", "tests", "run", "and", "for", "with", "from"}
INLINE_BUILD = 3000  # a repository with more files of interest than this is first indexed by a detached process


def name_tokens(name):
    """A name's words, for telling `KeepConversation` from `keep_conversation_async`: camel and snake split, lower case."""
    return {t.lower() for t in re.findall(r"[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|\d+", name)} - GENERIC


def git(cwd, *args, timeout=30):
    """Git's output at `cwd`, or None when it failed or is not there."""
    try:
        run = subprocess.run(["git", *args], cwd=cwd, capture_output=True, timeout=timeout, creationflags=NO_WINDOW)
    except (OSError, subprocess.SubprocessError):
        return None
    return run.stdout.decode("utf-8", errors="replace") if run.returncode in (0, 1) else None


def definitions_in(root, paths=None):
    """The definitions in the repository at `root` by file, from one `git grep` over the working tree (tracked
    and untracked files alike): {rel: [[name, line], ...]} for every file that has any. `paths` narrows it to
    those files; None is the whole repository in the languages above."""
    spec = [f":(literal){p}" for p in paths] if paths is not None else [f"*{suffix}" for suffix in DEFINITIONS]
    out = git(root, "grep", "-n", "-z", "-I", "--untracked", "-E", PREFILTER, "--", *spec, timeout=600)
    found = {}
    for record in (out or "").split("\n"):
        rel, _, rest = record.partition("\0")
        line, _, text = rest.partition("\0")
        pattern = DEFINITIONS.get(Path(rel).suffix)
        if not pattern or not line.isdigit() or (paths is None and rel.endswith(".min.js")):
            continue
        if (m := pattern.match(text)) and (name := next((g for g in m.groups() if g), None)):
            found.setdefault(rel, []).append([name, int(line)])
    return found


def symbols(event, inline=False):
    """The project's definitions, kept in STATE/symbols/<project>.json as {"files": {rel: [[name, line], ...]},
    "paths": [rel, ...], "tokens": {word: [[path index, i], ...]}} with the commit and the dirty files it was read at. Each call refreshes
    only what moved since: the files `git diff` names between the commit then and now, and the working tree's
    changed files (`git status`) whose mtime differs. The first index of a large repository is built by a
    detached process (`hook.py index <cwd>`) so no hook waits on it; `inline` builds it here regardless."""
    cwd = Path(event.get("cwd") or os.getcwd())
    path = STATE / "symbols" / f"{project(event)}.json"
    try:
        index = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        index = {}
    root = (git(cwd, "rev-parse", "--show-toplevel") or "").strip()
    if not root:
        return index
    root = Path(root)
    head = (git(root, "rev-parse", "--verify", "-q", "HEAD") or "").strip()
    dirty, status, skip = set(), (git(root, "status", "--porcelain", "-z", "--untracked-files=all") or "").split("\0"), False
    for entry in status:
        if skip:
            skip = False  # a rename's second path, the old name
            continue
        if len(entry) > 3:
            skip = entry[0] in "RC"
            if Path(entry[3:]).suffix in DEFINITIONS:
                dirty.add(entry[3:])
    files, tokens = index.get("files"), index.get("tokens")
    if files is None or tokens is None or index.get("head") and head and git(root, "diff", "--name-only", "-z", index["head"], head) is None:
        # No index yet, or the commit it was read at is gone (a rebase): the whole repository, here or detached.
        if not inline:
            marker = path.with_suffix(".building")
            try:
                fresh = time.time() - marker.stat().st_mtime < 600
            except OSError:
                fresh = False
            if fresh:
                return {}
            interest = sum(1 for f in (git(root, "ls-files", "-z") or "").split("\0") if Path(f).suffix in DEFINITIONS)
            if interest > INLINE_BUILD:
                marker.parent.mkdir(parents=True, exist_ok=True)
                marker.write_text(str(time.time()))
                subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "index", str(cwd)], creationflags=BACKGROUND,
                                 stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, close_fds=True)
                return {}
        files, paths, tokens = definitions_in(root), [], {}
        for rel, found in files.items():
            paths.append(rel)
            for i, (name, _) in enumerate(found):
                for tok in name_tokens(name):
                    tokens.setdefault(tok, []).append([len(paths) - 1, i])
        index, built = {"head": head, "dirty": {}, "files": files, "paths": paths, "tokens": tokens}, True
        refresh = set(dirty)
    else:
        refresh, built = set(), False
        if index.get("head") != head:
            refresh |= {f for f in (git(root, "diff", "--name-only", "-z", index["head"], head) or "").split("\0")
                        if Path(f).suffix in DEFINITIONS} if index.get("head") else set(files)
        refresh |= set(index.get("dirty", {})) - dirty  # dirty then, not now: committed or reverted
    mtimes = {}
    for rel in dirty:
        try:
            mtimes[rel] = (root / rel).stat().st_mtime
        except OSError:
            mtimes[rel] = 0
        if index.get("dirty", {}).get(rel) != mtimes[rel]:
            refresh.add(rel)
    if refresh:
        present = [rel for rel in refresh if (root / rel).is_file()]
        found = definitions_in(root, present) if present else {}
        paths = index["paths"]
        for rel in refresh:
            fid = paths.index(rel) if rel in paths else None
            for name, _ in files.get(rel, []):
                for tok in name_tokens(name):
                    if (kept := [p for p in tokens.get(tok, []) if p[0] != fid]):
                        tokens[tok] = kept
                    else:
                        tokens.pop(tok, None)
            files.pop(rel, None)
            if rel in found:
                if fid is None:
                    paths.append(rel)
                    fid = len(paths) - 1
                files[rel] = found[rel]
                for i, (name, _) in enumerate(found[rel]):
                    for tok in name_tokens(name):
                        tokens.setdefault(tok, []).append([fid, i])
    if built or refresh or index.get("head") != head or index.get("dirty") != mtimes:
        index.update(head=head, dirty=mtimes)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(index, separators=(",", ":")), encoding="utf-8")
        path.with_suffix(".building").unlink(missing_ok=True)
    return index


def write_recall(event):
    """PreToolUse on Write and Edit: the definitions the new text declares, each checked against what the project
    already has (the same name elsewhere, or one made of the same words) and against the store. John, 2026-11-09:
    what helps in a large codebase is knowing that something exists for this and where to look, not a search of
    everything; this is that knowledge, offered at the moment of writing. A name the edit replaces (in old_string)
    is a change to that function, not a second one, and is left alone."""
    given = event.get("tool_input") or {}
    file = Path(given.get("file_path") or "")
    new = given.get("new_string") or given.get("content") or ""
    pattern = DEFINITIONS.get(file.suffix)
    if not pattern or not new:
        return ""
    old_names = {next(g for g in m.groups() if g) for m in pattern.finditer(given.get("old_string") or "")}
    names = [next(g for g in m.groups() if g) for m in pattern.finditer(new)]
    names = [n for n in dict.fromkeys(names) if n not in old_names and len(n) > 2]
    if not names:
        return ""
    index = symbols(event)
    files, paths, postings = index.get("files", {}), index.get("paths", []), index.get("tokens", {})
    if not files:
        return ""
    # A word's weight is its rarity among the project's names: `Load` is in many and says little, `Hydrated` in
    # one and says which function this is like. Shared weight over all weight, at least half. Only names that
    # share a word are looked at, through the token index, so a repository of 200,000 names costs no more than one of 200.
    import math
    total = max(sum(len(found) for found in files.values()), 1)
    weight = lambda toks: sum(math.log(1 + total / max(len(postings.get(tok, ())), 1)) for tok in toks)
    lines = []
    for name in names[:4]:
        mine = name_tokens(name)
        candidates = {}
        for tok in mine:
            for fid, i in postings.get(tok, []):
                candidates.setdefault((fid, i), set()).add(tok)
        same, alike = [], []
        own = {tok: weight([tok]) for tok in mine}
        for (fid, i), shared in candidates.items():
            rel = paths[fid]
            other, line = files[rel][i]
            common = sum(own[tok] for tok in shared)
            if other == name:
                if not (Path(rel).name == file.name and other in old_names):
                    same.append(f"{rel}:{line}")
            elif common >= 0.5 * sum(own.values()) and common / weight(mine | name_tokens(other)) >= 0.5:
                alike.append((common, f"{other} ({rel}:{line})"))
        alike = [a for _, a in sorted(alike, reverse=True)]
        if same:
            lines.append(f"- `{name}` already exists: {', '.join(sorted(same)[:4])}" + (" …" if len(same) > 4 else ""))
        if alike:
            lines.append(f"- like `{name}`: {', '.join(alike[:5])}" + (" …" if len(alike) > 5 else ""))
    if not lines:
        return ""
    # The store by the names' exact words, not by meaning: a fragment that names a helper is the "I know we have
    # something for this" John described; one merely near in meaning was general advice every time it was tried.
    said = store("search", *names[:4], "--limit", "4", cold=False).strip()
    # Authored fragments only: a past utterance that happens to use the words is not knowledge of a helper.
    said = "\n\n".join(block for block in said.split("\n\n") if block.startswith("[#") and "| ChatMessage |" not in block.split("\n", 1)[0])[:1200]
    text = f"Before writing {', '.join(f'`{n}`' for n in names[:4])}, what the project already has (reuse or extend it, or name why not):\n" + "\n".join(lines)
    if said and said != "(nothing)":
        write_ids = re.findall(r"^\[#(\d+) \|", said, re.M)
        seen(event, add=write_ids)
        record_shown(event, "write", write_ids)
        text += "\n\nWhat your store says about these names:\n\n" + said
    return text


def tool_cue(event):
    """PreToolUse: a reminder set for this kind of call (`cue set tool/<name> --when '^PowerShell:.*python'`), and
    for a Write or Edit that declares a function, what the project already has under that name."""
    text = f"{event.get('tool_name', '')}: {json.dumps(event.get('tool_input') or {}, ensure_ascii=False)}"
    parts = []
    found = cued(event, "tool", text)
    if found:
        parts.append("A cue you set for calls like this one (a reminder, not a block):\n\n" + found)
    if event.get("tool_name") in ("Write", "Edit", "MultiEdit"):
        try:
            if existing := write_recall(event):
                parts.append(existing)
        except Exception as e:  # a reminder must never stop the edit
            print(f"persistence hook: write recall failed: {e!r}", file=sys.stderr)
    if parts:
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "additionalContext": "\n\n".join(parts)[:CAP]}}))


STARTERS = {"Agent", "Task", "Monitor", "ScheduleWakeup", "CronCreate", "Workflow", "mcp__ccd_session__start_session",
            "mcp__ccd_session__hand_off_to_session"}


def this_turn(path):
    """The last message the peer wrote this turn, and whether the turn started anything that outlives it (a
    background command, a wake, an agent, a session), read from the transcript since the human last spoke."""
    try:
        lines = Path(path).read_text(encoding="utf-8", errors="replace").splitlines()[-400:]
    except OSError:
        return "", False
    said, started, named = "", False, set()
    for line in reversed(lines):
        try:
            e = json.loads(line)
        except ValueError:
            continue
        if e.get("isSidechain"):
            continue
        content = (e.get("message") or {}).get("content")
        if e.get("type") == "user":
            if isinstance(content, str) or any(isinstance(b, dict) and b.get("type") == "text" for b in content or []):
                break  # the message that began the turn: the human's, or the harness's
            continue
        if e.get("type") != "assistant" or not isinstance(content, list):
            continue
        for b in content:
            if b.get("type") == "text":
                if not said:
                    said = b.get("text", "")
                named.update(re.findall(r"#(\d{2,})\b", b.get("text", "")))
            if b.get("type") == "tool_use":
                given = b.get("input") or {}
                command = str(given.get("command", ""))
                # A wake set by the command itself, at a command position: the words inside a heredoc or a patch are not one
                # (the README's "hook.py wake" passed for a start on 2026-11-09, and a promise went unheld).
                if b.get("name") in STARTERS or given.get("run_in_background") or re.search(r"(?:^|[;&|(]\s*)(?:pwake|\S*python\S*\s+\S*hook\.py\s+wake)\s+\S", command, re.M):
                    started = True
                named.update(re.findall(r"\b(?:show|edit|supersede|tag|untag|merge|archive|protect)\s+(\d{2,})\b", command))
                named.update(re.findall(r"(?<![\w.])(\d{2,})(?=\s*(?:,\d{2,})*\s)", command) if "supersede" in command else [])
    return said, started, named


def stop_cue(event):
    """Stop: a reminder set for how a turn ends (`cue set stop/<name> --when <promise>`), shown only when the turn
    started nothing that carries on, and only once (stop_hook_active): a turn that said "I'll build it next" and
    ended without starting it is held, once, with the reminder."""
    if event.get("stop_hook_active"):
        return
    said, started, named = this_turn(event.get("transcript_path", ""))
    record_used(event, named)
    if not said or started:
        return
    found = cued(event, "stop", said)
    if found:
        print(json.dumps({"decision": "block", "reason": "A cue you set for how a turn ends matched your last message, "
                          "and this turn started nothing that carries on (no background command, wake, agent or session). "
                          "If you meant to start something, start it now; if not, end the turn again.\n\n" + found}))


def recall(event):
    keep_soon(event)
    count = int(os.environ.get("PERSISTENCE_RECALL_COUNT", "3"))
    prompt = (event.get("prompt") or "").strip()
    if count <= 0 or len(prompt) < 12:
        return
    if any(m in prompt for m in ("<task-notification>", "<agent-message", "[Subagent hand-back]")) or prompt.startswith("[SYSTEM NOTIFICATION"):
        return  # the harness reporting a background task or a subagent, not the human: nothing of theirs to associate with
    # A short message ("sounds good") matches past acknowledgements, not what it is about: no past utterances for it.
    reminded = cued(event, "prompt", prompt)
    already = seen(event)
    since = STATE / f"{event.get('session_id', 'unknown')}.since"
    found = store("recall", "--text", "-", *session_args(event), "--limit", str(count), "--snippet", "300", "--project", project(event),
                  "--wander", os.environ.get("PERSISTENCE_RECALL_WANDER", "1"),
                  "--said", os.environ.get("PERSISTENCE_RECALL_SAID", "3") if len(prompt) >= 40 else "0",
                  *(["--before", since.read_text().strip()] if since.exists() else []),
                  # A cue's text joins what is looked for: "receiving" also finds what was said about receipting.
                  *(["--exclude", ",".join(already)] if already else []), stdin=prompt + ("\n\n" + reminded if reminded else ""), cold=False)
    ids = re.findall(r"^\[#(\d+) \|", found, re.M)
    if not ids and not reminded:
        return
    seen(event, add=ids)
    record_shown(event, "recall", ids)
    text = ("Cued by this message (a reminder you set for words like these):\n\n" + reminded + "\n\n" if reminded else "") + (
        "Recalled from your store, by association with this message (`persistence-store show <id>` "
        "reads the whole of one):\n\n" + found.strip() if ids else "")
    print(json.dumps({"hookSpecificOutput": {"hookEventName": "UserPromptSubmit", "additionalContext": text[:CAP]}}))


def subagent_start(event):
    lead = ("# Who you are\n\nYou are a subagent of a Claude with its own Persistence store; this is its "
            "core, and so yours, with its working note first and what is so on the machine now last. Read it; "
            "do not write to the store, the main session does that.")
    seen_now = observe(event)  # the working context: what is running, what changed, git
    body = store("core", "--max-chars", str(CAP - len(lead) - len(seen_now) - 100), "--recent", "0", "--project", project(event), *session_args(event))
    text = f"{lead}\n\n{body}\n\n{seen_now}" if body.strip() else ""
    if text:
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "SubagentStart", "additionalContext": text}}))


WATCHED = STATE.parent / "watched.json"
SHELLS = {"bash.exe", "sh.exe", "powershell.exe", "pwsh.exe", "cmd.exe", "conhost.exe", "git.exe", "claude.exe", "node.exe",
          "persistence-store.exe"}  # the harness's and the store's own, not the work
HEAVY = {"node_modules", "bin", "obj", "data", "models", "__pycache__", "dist", "build"}  # not walked for logs
NOISE = re.compile(r"^\d+\.\d+\.\d+\.\d+ [IWED] |it/s\]|Loading weights|\bslot\b|\bsrv\b|\| task|LRU|n_tokens|tokens per second|\w = \S")  # a server's per-request lines and key = value chatter, progress bars


def watched():
    try:
        return json.loads(WATCHED.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return []


def watch(args):
    """`watch <pid|file|command> [label]`, `unwatch <pid|file|command>`, `watching`: what the observer
    reads besides what it finds on its own (this project's processes and its recently written logs)."""
    items = watched()
    if args[0] == "watching":
        print("\n".join(f"{w['what']}  {w.get('label', '')}" for w in items) or "(nothing watched)")
        return
    what = " ".join(args[1:2]) if args[0] == "unwatch" else args[1]
    items = [w for w in items if w["what"] != what]
    if args[0] == "watch":
        items.append({"what": what, "label": " ".join(args[2:]), "since": datetime.now(timezone.utc).isoformat(timespec="seconds")})
    WATCHED.parent.mkdir(parents=True, exist_ok=True)
    WATCHED.write_text(json.dumps(items, indent=1), encoding="utf-8")
    print(f"{'watching' if args[0] == 'watch' else 'no longer watching'} {what}")


def last_line(path):
    """The last line of a log that says something: a server's per-request lines and progress bars skipped."""
    try:
        with open(path, "rb") as f:
            f.seek(0, 2)
            f.seek(max(0, f.tell() - 400_000))
            cut = f.tell() > 0
            lines = f.read().decode("utf-8", "replace").replace("\r", "\n").splitlines()[1 if cut else 0:]
    except OSError:
        return ""
    said = [l for l in lines if l.strip() and not NOISE.search(l)]
    return said[-1].strip()[:160] if said else ""


def age(seconds):
    return f"{seconds / 3600:.1f}h" if seconds >= 3600 else f"{seconds / 60:.0f}m" if seconds >= 60 else f"{seconds:.0f}s"


def run_quiet(cmd, cwd=None, timeout=10):
    try:
        out = subprocess.run(creationflags=NO_WINDOW, args=cmd, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
        return out.stdout if out.returncode == 0 else ""
    except (OSError, subprocess.SubprocessError):
        return ""


def observe(event):
    """What is so now, read off the machine every message rather than written down: the processes
    running from this project, logs written in the last hour with their last line, git's state, and
    whatever is watched (`hook.py watch`). Written down, it went stale; read, it cannot."""
    from concurrent.futures import ThreadPoolExecutor

    cwd = Path(event.get("cwd") or os.getcwd())
    name, now = project(event), datetime.now().timestamp()
    pids = [w["what"] for w in watched() if w["what"].isdigit()]
    query = (f"Get-CimInstance Win32_Process | Where-Object {{ $_.CommandLine -like '*{cwd.name}*' -or "
             f"@({','.join(pids) or '-1'}) -contains $_.ProcessId }} | Select-Object ProcessId,Name,CreationDate,CommandLine | "
             "ConvertTo-Json -Compress")
    with ThreadPoolExecutor(4) as pool:
        procs = pool.submit(run_quiet, ["powershell", "-NoProfile", "-Command", query], None, 15)
        status = pool.submit(run_quiet, ["git", "status", "--porcelain", "--branch"], cwd)
        last = pool.submit(run_quiet, ["git", "log", "-1", "--format=%h %cr: %s"], cwd)
    lines = [f"## Observed now ({datetime.now().strftime('%H:%M')} local, read off the machine, not written)"]

    try:
        found = json.loads(procs.result() or "[]")
    except ValueError:
        found = []
    found = [found] if isinstance(found, dict) else found
    shown = set()
    for p in found:
        if p.get("Name", "").lower() in SHELLS or not p.get("CommandLine") or "hook.py" in p["CommandLine"]:
            continue
        started = re.search(r"\d+", p.get("CreationDate") or "")
        since = age(now - int(started.group()) / 1000) if started else "?"
        cmd = " ".join(re.sub(r'[A-Za-z]:[\\/][^" ]*[\\/]', "", p["CommandLine"]).replace('"', "").split())
        if cmd in shown:
            continue  # a venv's launcher and the interpreter it starts are one process to the reader
        shown.add(cmd)
        lines.append(f"- running {since}: pid {p['ProcessId']} {cmd[:150]}")
    if not shown:
        lines.append(f"- nothing of {name} is running")

    logs = []
    for root, dirs, files in os.walk(cwd):
        dirs[:] = [d for d in dirs if d not in HEAVY and not d.startswith(".")]
        for f in files:
            if f.endswith(".log"):
                try:
                    mtime = (Path(root) / f).stat().st_mtime
                except OSError:
                    continue
                if now - mtime < 3600:
                    logs.append((mtime, Path(root) / f))
    for w in watched():
        if not w["what"].isdigit() and Path(w["what"]).exists():
            logs.append((Path(w["what"]).stat().st_mtime, Path(w["what"])))
    for mtime, path in sorted(logs, reverse=True)[:4]:
        where = path.relative_to(cwd) if path.is_relative_to(cwd) else path
        tail = last_line(path)
        lines.append(f"- {where}, written {age(max(0, now - mtime))} ago" + (f": {tail}" if tail else ""))
    for w in watched():
        if w["what"].isdigit() and not any(str(p.get("ProcessId")) == w["what"] for p in found):
            lines.append(f"- watched pid {w['what']} ({w.get('label') or 'no label'}) has exited")

    head, *changes = (status.result() or "").splitlines()
    if head:
        ahead = re.search(r"ahead (\d+)", head)
        lines.append(f"- git: {head[3:].split('...')[0]}, {len(changes)} uncommitted"
                     + (f", {ahead.group(1)} unpushed" if ahead else "") + f"; last commit {last.result().strip()[:120]}")
    tokens = context_tokens(event.get("transcript_path", ""))
    if tokens:
        lines.append(f"- this context window: {tokens / 1000:.0f}k tokens at the last reply")
    return "\n".join(lines)


def observe_hook(event):
    text = observe(event)
    print(json.dumps({"hookSpecificOutput": {"hookEventName": event.get("hook_event_name", "UserPromptSubmit"),
                                             "additionalContext": text[:CAP]}}))


TIMES = STATE.parent / "hook-times.log"  # one line per per-message hook answered by `serve`: path, seconds, bytes
HOOK_PORT = int(os.environ.get("PERSISTENCE_HOOK_PORT", "8097"))  # `hook.py serve`: the hooks answered by URL


class PerRequest:
    """sys.stdout while serving: each request's thread writes to its own buffer, so the hook functions
    print exactly as they do when run as a command, and their output becomes that request's reply."""

    def __init__(self, real):
        import threading

        self.real, self.local = real, threading.local()

    def write(self, text):
        buffer = getattr(self.local, "buffer", None)
        return buffer.write(text) if buffer is not None else self.real.write(text)

    def flush(self):
        if getattr(self.local, "buffer", None) is None:
            self.real.flush()


WAKES = STATE.parent / "wakes.json"
WAITING = {}  # session -> its newest waiter's generation; an older one is let go, since its turn has passed


def wakes(update=None):
    try:
        items = json.loads(WAKES.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        items = []
    if update is not None:
        items = update(items)
        WAKES.write_text(json.dumps(items, indent=1), encoding="utf-8")
    return items


def add_wake(event):
    """A wake set by the peer: at a time ({"in": minutes}) or when a process ends ({"pid": n}), with
    the reason it will be woken with."""
    item = {"reason": event.get("reason") or "a wake you set", "set": datetime.now(timezone.utc).isoformat(timespec="seconds")}
    if event.get("session"):
        item["session"] = event["session"]  # only this session's waiter takes it: one session's wake fired in another (2026-11-09)
    if event.get("pid"):
        item["pid"] = int(event["pid"])
    else:
        item["at"] = datetime.now().timestamp() + 60 * float(event.get("in", 30))
    wakes(lambda items: items + [item])
    return {"set": item}


def alive(pid):
    out = run_quiet(["tasklist", "/FI", f"PID eq {pid}", "/NH"]) if os.name == "nt" else ""
    return str(pid) in out if os.name == "nt" else Path(f"/proc/{pid}").exists()


def wait_for_wake(event, poll=10):
    """Held open until a wake is due (then {"wake": reason}), a newer turn's waiter replaces this one
    ({"wake": null}), or the waiter's own budget runs out."""
    import time

    session = event.get("session_id") or ""
    WAITING[session] = WAITING.get(session, 0) + 1
    mine, deadline = WAITING[session], time.time() + float(event.get("seconds", 3000))
    ours = lambda w: w.get("session", session) == session  # a wake set before sessions were recorded is anyone's
    while time.time() < deadline and WAITING[session] == mine:
        now, due = datetime.now().timestamp(), []

        def take(items):
            keep = []
            for w in items:
                (due if ours(w) and (("at" in w and w["at"] <= now) or ("pid" in w and not alive(w["pid"]))) else keep).append(w)
            return keep

        if any(ours(w) and ("at" in w and w["at"] <= now or "pid" in w) for w in wakes()):
            wakes(take)
        if due:
            return {"wake": "; ".join(w["reason"] + (f" (pid {w['pid']} ended)" if "pid" in w else "") for w in due)}
        time.sleep(poll)
    return {"wake": None}


def waiter(event):
    """The Stop hook that can wake the peer (async, asyncRewake): it waits on the hook server and, when
    a wake comes due, exits 2 with the reason, which starts a turn though no one has written. So the
    peer chooses when its next input comes, not only the person it works with."""
    import time
    import urllib.request

    deadline = time.time() + float(os.environ.get("PERSISTENCE_WAKE_SECONDS", "3300"))
    while time.time() < deadline:
        try:
            body = json.dumps({"seconds": max(1, deadline - time.time() - 5), "session_id": event.get("session_id", "")}).encode("utf-8")
            request = urllib.request.Request(f"http://127.0.0.1:{HOOK_PORT}/wait", body, {"Content-Type": "application/json"})
            reply = json.loads(urllib.request.urlopen(request, timeout=deadline - time.time() + 30).read() or b"{}")
        except (OSError, ValueError):
            ensure()
            time.sleep(10)
            continue
        if reply.get("wake"):
            print(f"Woken by a wake you set: {reply['wake']}", file=sys.stderr)
            sys.exit(2)
        return  # replaced by a newer turn's waiter, or out of time


def answer(name, part, event):
    """A hook's reply as an HTTP hook needs it: JSON as it is; plain text (the core's parts) wrapped as
    additionalContext, since an HTTP hook's plain text is not put in front of the peer."""
    import io

    if name in ("wait", "wake"):
        return json.dumps(wait_for_wake(event) if name == "wait" else add_wake(event))
    sys.stdout.local.buffer = io.StringIO()
    try:
        if name == "session-start":
            session_start(event, part)
        else:
            {"subagent-start": subagent_start, "recall": recall, "observe": observe_hook, "tool-cue": tool_cue,
             "stop-cue": stop_cue}[name](event)
        out = sys.stdout.local.buffer.getvalue().strip()
    finally:
        sys.stdout.local.buffer = None
    if not out or out.startswith("{"):
        return out
    return json.dumps({"hookSpecificOutput": {"hookEventName": event.get("hook_event_name", "SessionStart"), "additionalContext": out}})


def serve():
    """The hooks a session calls every message, answered by a process that stays up: the harness posts
    the event to http://127.0.0.1:PERSISTENCE_HOOK_PORT/<hook>[/<part>], and nothing starts per call.
    When this script changes it starts itself again, so an edit is live from the next call."""
    import time
    from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

    sys.stdout = PerRequest(sys.stdout)
    script = Path(__file__).resolve()
    written = script.stat().st_mtime

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            parts = self.path.strip("/").split("/")
            began = time.perf_counter()
            try:
                event = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}")
                body = answer(parts[0], int(parts[1]) if len(parts) > 1 else 1, event).encode("utf-8")
                self.send_response(200)
            except Exception as e:  # a hook must never break the session it serves
                print(f"persistence hook server: {self.path} failed: {e!r}", file=sys.stderr)
                body = b""
                self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            if parts[0] != "wait":  # each per-message hook's time, so a slowdown can be read rather than guessed
                try:
                    with open(TIMES, "a", encoding="utf-8") as log:
                        log.write(f"{datetime.now().isoformat(timespec='seconds')} {self.path} {time.perf_counter() - began:.2f}s {len(body)}b\n")
                except OSError:
                    pass
            if script.stat().st_mtime != written:
                os.execv(sys.executable, [sys.executable, str(script), "serve"])

        def log_message(self, *args):
            pass

    class Server(ThreadingHTTPServer):
        # SO_REUSEADDR on Windows lets a second process bind a port already bound, so two hooks that each
        # found the server down both started one and the requests were split between them (2026-11-08).
        # Without it the second fails to bind and ends, and the first keeps the port.
        allow_reuse_address = os.name != "nt"

    try:
        server = Server(("127.0.0.1", HOOK_PORT), Handler)
    except OSError:
        return  # another server holds the port: it answers
    server.serve_forever()


def ensure():
    """Starts the hook server when nothing answers on its port; run by the command hooks that remain,
    so a server that stopped is back within one turn, and its hooks fail open meanwhile."""
    import socket

    try:
        socket.create_connection(("127.0.0.1", HOOK_PORT), timeout=1).close()
    except OSError:
        flags = BACKGROUND
        subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "serve"], creationflags=flags,
                         stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, close_fds=True)


def cli(args):
    """The store CLI for the peer's own shell. Local: the published CLI itself. Remote: the same call
    sent to the remote store, its output printed here; never a local store in its place."""
    exe = os.environ.get("PERSISTENCE_STORE_EXE", "persistence-store")
    where = remote()
    if not where:
        return subprocess.run([exe, *args]).returncode
    try:
        call, stdin = remote_args(args)
    except OSError as e:
        print(f"could not read a file the call names: {e}", file=sys.stderr)
        return 1
    if stdin is None and "-" in call:
        stdin = sys.stdin.read()
    reply = served_remote(where, call, stdin or "", 300)
    if reply is None or reply is BUSY:
        print(f"the store on {where['host']} did not answer (tunnel log: {STATE / 'tunnel.log'})", file=sys.stderr)
        return 3
    code, out, err = reply
    sys.stdout.write(out)
    sys.stderr.write(err)
    return code


def main():
    # Windows Pythons default to the console code page, which cannot encode what fragments hold.
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdin.reconfigure(encoding="utf-8")
    if sys.argv[1] in ("watch", "unwatch", "watching"):
        watch(sys.argv[1:])  # run by a person or a peer, not the harness: nothing on stdin
        return
    if sys.argv[1] in ("serve", "ensure"):
        serve() if sys.argv[1] == "serve" else ensure()
        return
    if sys.argv[1] == "cli":  # `cli <args>`: the store CLI, by way of the remote store when there is one
        sys.exit(cli(sys.argv[2:]))
    if sys.argv[1] == "uses":  # `uses [days]`: what recall and the cues showed, and how much of it a turn then used
        uses_report(int(sys.argv[2]) if len(sys.argv) > 2 else 30)
        return
    if sys.argv[1] == "index":  # `index <cwd>`: the project's symbol index built whole, by the detached process `symbols` starts
        symbols({"cwd": sys.argv[2]}, inline=True)
        return
    if sys.argv[1] == "wake":  # `wake <minutes|pid:N> <reason>` or `wake list`, run by the peer
        import urllib.request

        if sys.argv[2] == "list":
            print("\n".join(f"{w.get('pid') and 'when pid ' + str(w['pid']) + ' ends' or datetime.fromtimestamp(w['at']).strftime('at %H:%M')}: {w['reason']}"
                            for w in wakes()) or "(no wakes set)")
            return
        when = sys.argv[2]
        wake = {"pid": when[4:]} if when.startswith("pid:") else {"in": float(when.rstrip("m"))}
        wake["reason"] = " ".join(sys.argv[3:])
        wake["session"] = os.environ.get("CLAUDE_CODE_SESSION_ID", "")
        ensure()
        request = urllib.request.Request(f"http://127.0.0.1:{HOOK_PORT}/wake", json.dumps(wake).encode("utf-8"), {"Content-Type": "application/json"})
        print(urllib.request.urlopen(request, timeout=10).read().decode("utf-8"))
        return
    event = json.load(sys.stdin) if not sys.stdin.isatty() else {}
    if sys.argv[1] == "session-start":
        session_start(event, int(sys.argv[2]) if len(sys.argv) > 2 else 1)
        return
    if sys.argv[1] in ("keep", "curate"):
        ensure()  # the Stop hooks run every turn: the hook server, if it stopped, is back by the next message
    {"keep": keep, "curate": curate, "precompact": precompact, "waiter": waiter,
     "subagent-start": subagent_start, "recall": recall, "observe": observe_hook, "tool-cue": tool_cue,
     "stop-cue": stop_cue}[sys.argv[1]](event)


if __name__ == "__main__":
    main()
