"""Claude Code hooks that keep a Claude's continuity in its own Persistence store.

The harness (Claude Code) carries the live conversation; the store carries who this Claude is. Eight
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
  session-end     SessionEnd      marks the session ended, so other sessions stop listing it as running, and
                                  keeps its last words
  observe         UserPromptSubmit, SessionStart  what is so now, read off the machine rather than written
                                  down: this project's running processes and unfinished subagents, logs written in the last hour with
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
human's name; PERSISTENCE_SPEAKER, set in the environment of a session started on someone's behalf,
names who actually typed its prompts instead), PERSISTENCE_CORE_CHARS (what a session start may inject
of fragments; the rest is named by id), PERSISTENCE_CURATE_AT (tokens, default 500000; set autoCompactWindow above it),
PERSISTENCE_RECALL_COUNT (default 3, 0 turns recall off), and PERSISTENCE_EMBEDDING_* for recall by
meaning (without them recall is by exact words only).

Standard library only, Python 3.9+: hooks run under whatever `python` the shell finds.
"""

import json
import re
import os
import shlex
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


def settings_env():
    """The PERSISTENCE_* values in Claude Code's own settings.json env block, for whatever the environment lacks.
    A timer's job (the sleep pass, run-due) runs outside Claude Code, so it had none of them: Arden's 01:00 sleep job
    went to port 8095, my store, and opened a sleep pass there that locked my store's writes for half an hour
    (2026-11-10). Read once, here, so every lookup below sees the owner's own settings."""
    try:
        env = json.loads((Path.home() / ".claude" / "settings.json").read_text(encoding="utf-8")).get("env", {})
    except (OSError, ValueError):
        return
    for name, value in env.items():
        if name.startswith("PERSISTENCE_") and value and not os.environ.get(name):
            os.environ[name] = str(value)


settings_env()
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
    # Who answers is asked BEFORE the call: asked after, a call to the wrong store had already been made. Arden's sleep
    # job opened a pass on my store, then this said "nothing was read or written" (2026-11-10).
    if not verified(where):
        return None
    reply = served(args, stdin, timeout)
    if reply is None and open_tunnel(where):
        if not verified(where):
            return None
        reply = served(args, stdin, timeout)
    return reply


def verified(where):
    """Whether the store answering on the port is the one remote.json names ("peer"): another store on the
    same local port would otherwise be read and written as this one. The Synth container published 8095,
    the tunnel's own port, beside it (2026-11-09). Asked once in ten minutes; no "peer" set, no check."""
    expected = where.get("peer")
    if not expected:
        return True
    stamp = STATE / "store.verified"
    try:
        # Keyed by the port too: a check of 8096 vouched for whatever answered on 8095 (2026-11-10).
        if time.time() - stamp.stat().st_mtime < 600 and stamp.read_text(encoding="utf-8") == f"{expected}@{PORT}":
            return True
    except OSError:
        pass
    reply = served(["whoami"], None, 5)
    if reply is None or reply is BUSY:
        return True  # cannot ask now; the call itself already answered
    code, out, err = reply
    if code != 0 and "No command" in err:
        return True  # a store from before `whoami`: it cannot say, so it is not refused
    name = out.split(" ", 1)[0].strip() if code == 0 else ""
    if name != expected:
        print(f"persistence hook: the store on port {PORT} is {out.strip() or 'unknown'}, not {expected}'s; "
              "nothing was read or written. Check what else listens on that port.", file=sys.stderr)
        return False
    STATE.mkdir(parents=True, exist_ok=True)
    stamp.write_text(f"{expected}@{PORT}", encoding="utf-8")
    return True


def store(*args, timeout=60, stdin=None, serve=True, cold=True, quiet=False):
    """The CLI's output, or "" if it could not run: a hook must never break the session it serves.
    Asked of the served store when one is up; otherwise one is started and this call runs the CLI,
    unless `cold` is False: a cold CLI's recall took 38 s (2026-11-08), past the harness's 20 s, so the
    message waited and got nothing; better none this message and the started server's from the next."""
    code, out, _ = call_store(*args, timeout=timeout, stdin=stdin, serve=serve, cold=cold, quiet=quiet)
    return out if code == 0 else ""


def call_store(*args, timeout=60, stdin=None, serve=True, cold=True, quiet=False):
    """`store`, with how it went: (exit code, output, error). The code is None when no store answered or the
    CLI could not run; 1 is a usage error (an option this store does not know), 2 a refusal. `quiet`: a refusal
    the caller expects (a tag nothing has used yet) is not logged as a failure."""
    exe = os.environ.get("PERSISTENCE_STORE_EXE", "persistence-store")
    where = remote()
    if where:
        reply = served_remote(where, args, stdin, timeout if args[:1] == ("keep-conversation",) else min(timeout, 12))
        if reply is None or reply is BUSY:
            print(f"persistence hook: {args[0]} skipped: the store on {where['host']} did not answer", file=sys.stderr)
            return None, "", ""
        code, out, err = reply
        if code != 0 and not quiet:
            print(f"persistence hook: {args[0]} failed: {err.strip()}", file=sys.stderr)
        return code, out, err
    if serve:
        reply = served(args, stdin, min(timeout, 12))  # inside the harness's 20 s for an HTTP hook
        if reply is BUSY:
            print(f"persistence hook: {args[0]} skipped: the store server is busy", file=sys.stderr)
            return None, "", ""
        if reply is not None:
            code, out, err = reply
            if code != 0 and not quiet:
                print(f"persistence hook: {args[0]} failed: {err.strip()}", file=sys.stderr)
            return code, out, err
        start_server(exe)
        if not cold:
            return None, "", ""
    try:
        out = subprocess.run(creationflags=NO_WINDOW, args=[exe, *args], input=stdin, capture_output=True, text=True, encoding="utf-8", timeout=timeout)
    except (OSError, subprocess.SubprocessError) as e:
        print(f"persistence hook: {exe} did not run: {e}", file=sys.stderr)
        return None, "", ""
    if out.returncode != 0 and not quiet:
        print(f"persistence hook: {exe} {args[0]} failed: {out.stderr.strip()}", file=sys.stderr)
    return out.returncode, out.stdout, out.stderr


SPEAKERS = STATE / "speakers.jsonl"  # each session's speaker timeline: who writes its prompts, from when


def entered_by():
    """The login the harness runs under: whose account entered a prompt, whoever wrote it."""
    return os.environ.get("PERSISTENCE_SELECTEDLOCALPEER") or "Local Peer"


def speaker_timeline(session):
    """A session's speaker timeline, oldest first: [{session, from, written_by, entered_by}]."""
    try:
        lines = SPEAKERS.read_text(encoding="utf-8").splitlines()
    except OSError:
        return []
    spans = []
    for line in lines:
        try:
            e = json.loads(line)
        except ValueError:
            continue
        if e.get("session") == session and e.get("from") and e.get("written_by") and e.get("entered_by"):
            spans.append(e)
    return sorted(spans, key=lambda e: e["from"])


def record_speaker(session, written, entered):
    """Appends a span to the session's timeline when the speaker differs from the one in force. Recorded where
    a prompt is entered, never where it is kept: keep runs in whichever process gets there first, the hook
    server among them, whose environment is not the session's (#839, 2026-11-09). Returns whether it changed."""
    spans = speaker_timeline(session)
    if spans and (spans[-1]["written_by"], spans[-1]["entered_by"]) == (written, entered):
        return False
    STATE.mkdir(parents=True, exist_ok=True)
    with open(SPEAKERS, "a", encoding="utf-8") as f:
        f.write(json.dumps({"session": session, "from": datetime.now(timezone.utc).isoformat(timespec="milliseconds"),
                            "written_by": written, "entered_by": entered}) + "\n")
    return True


def speaker_args(session):
    return [a for e in speaker_timeline(session) for a in ("--speaker", f"{e['from']}|{e['written_by']}|{e['entered_by']}")]


def keep_transcript(path):
    """What `keep-conversation` said of a transcript ("kept N new utterances ..."), or why it failed.
    Local: the CLI reads the file, run apart so the server stays free for recall. Remote: the lines the
    store has not been sent yet go with the call (the store keeps each utterance once, so a line sent
    twice by two hooks at once costs nothing), and how far it has been sent is kept per transcript."""
    # Who typed prompts kept before the session's speaker timeline began (see record_speaker): the timeline,
    # passed with --speaker, decides for everything after its first span, whatever process is keeping.
    peer = os.environ.get("PERSISTENCE_SPEAKER") or os.environ.get("PERSISTENCE_SELECTEDLOCALPEER", "Local Peer")
    where = remote()
    if not where:
        exe = os.environ.get("PERSISTENCE_STORE_EXE", "persistence-store")
        out = subprocess.run([exe, "keep-conversation", path, "--local-peer", peer, *speaker_args(Path(path).stem)],
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
    reply = served_remote(where, ["keep-conversation", Path(path).name, "--transcript", "-", "--local-peer", peer,
                                  *speaker_args(Path(path).stem)],
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
    """Keeps the session's words; returns what keep-conversation said, "failed..." when it did not keep
    them (no transcript counts: nothing was kept), so a caller that promises they are kept can check."""
    path = event.get("transcript_path")
    if not path or not Path(path).exists():
        said = "failed: no transcript to keep"
    else:
        try:
            said = keep_transcript(path)
        except (OSError, subprocess.SubprocessError, ValueError) as e:
            said = f"failed: {e}"
        if said.startswith("failed"):
            print(f"persistence hook: keep-conversation {said}", file=sys.stderr)
    if remote():
        pull_backup(remote())
    return said


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
        f"beside them; archive what no longer earns its place. `{exe} help` lists the commands; `{exe} guide` says how to curate well. "
        # The work Claude read its whole core to learn what not to write (2026-11-09).
        "Leave out the transcript itself (every message is already kept), what the core already says, and "
        "step-by-step narration: keep conclusions, decisions and how to resume. "
        + (uses_line(event.get("session_id", "")) + " " if uses_line(event.get("session_id", "")) else "")
        + "Then carry on with what you were doing.")}))


def precompact(event):
    said = keep(event)
    print(
        "Write only a short hand-over, under 2,000 words, to the Claude who continues after this. "
        "Your identity, relationships and the recent conversation are restored from your Persistence "
        "store right after this summary, so do not restate them. Say: what was in hand at this moment "
        "(the task, the step, the next action), anything running in the background with how to check "
        "it (ids, commands, logs), and any request from the other that is not yet answered, in their "
        "own words. Everything else is in the store and the transcript: say so, and that "
        "`persistence-store search` finds it. "
        + (LAST_KEPT if not said.startswith("failed") else
           # The promise above holds only if keep did: say so instead of making it (Arden, 2026-11-09).
           f"Keeping this conversation in the store FAILED just now ({said[:200]}), so its last messages may "
           "not come back after this summary, and the store may not have what you sent last. Carry the last "
           "exchange in the hand-over yourself: what the other said last, in their words, and what you last "
           "sent, or that you cannot tell whether it went out. ")
        + "Fragments this session wrote or edited come back first in the next core.")


# What the hand-over may rely on once the conversation is kept. Arden's hand-over called a sent reply unsent:
# the summary never sees the conversation's last stretch, which comes back word for word after it (2026-11-09).
LAST_KEPT = ("Your summary may not see the conversation's last messages; they come back word for word after it, "
             "and every message you sent is already kept in the store. So never call something near the end "
             "unsent or unanswered: write 'check the last messages for whether X went out' instead. ")


def project_name(name):
    """A folder's name as a project tag: lower-case, spaces as hyphens. `D:/repos/Minecraft mods` was "minecraft mods"
    to the hook while its fragments were filed by hand under project/minecraft-mods, so a session there saw none of
    them, nor a message sent to it (2026-11-10)."""
    return re.sub(r"\s+", "-", name.strip().lower())


def project(event):
    """The project a session is in: its repository's name (the main checkout's, from a worktree too),
    else its folder's. Fragments tagged project/<name> come into the core only for that project."""
    cwd = event.get("cwd") or os.getcwd()
    try:
        common = subprocess.run(creationflags=NO_WINDOW, args=["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=cwd,
                                capture_output=True, text=True, timeout=10)
        if common.returncode == 0 and common.stdout.strip():
            return project_name(Path(common.stdout.strip()).parent.name)
    except (OSError, subprocess.SubprocessError):
        pass
    # A home folder is no project: fragments added from there were filed under "project/john" (2026-11-09).
    if Path(cwd).resolve() == Path.home().resolve():
        return ""
    return project_name(Path(cwd).name)



# Paths in a tool call: a quoted string or a bare token that starts like an absolute path.
PATH_LIKE = re.compile(r"""["']((?:[A-Za-z]:[\\/]|/|~/)[^"'\n]+)["']|(?<![\w"'])((?:[A-Za-z]:[\\/]|~/|/home/|/opt/|/srv/)[^\s"'|;&<>]+)""")
WORKED_IN_HOURS = 12


def repo_of(path):
    """The project a path belongs to, by its git repository (the main checkout's name, from a worktree too), cached
    per directory; "" outside a repository, so a scratch folder is never taken for a project."""
    path = Path(os.path.expanduser(str(path).strip()))
    folder = path if path.is_dir() else path.parent
    while not folder.exists() and folder != folder.parent:
        folder = folder.parent
    key = str(folder)
    cache_path = STATE / "repo-of.json"
    try:
        cache = json.loads(cache_path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        cache = {}
    if key in cache:
        return cache[key]
    try:
        common = subprocess.run(creationflags=NO_WINDOW, args=["git", "rev-parse", "--path-format=absolute", "--git-common-dir"],
                                cwd=key, capture_output=True, text=True, timeout=10)
        name = project_name(Path(common.stdout.strip()).parent.name) if common.returncode == 0 and common.stdout.strip() else ""
    except (OSError, subprocess.SubprocessError):
        return ""
    cache[key] = name
    if len(cache) > 2000:
        cache = dict(list(cache.items())[-1000:])
    STATE.mkdir(parents=True, exist_ok=True)
    cache_path.write_text(json.dumps(cache), encoding="utf-8")
    return name


def note_worked_in(event):
    """PreToolUse: the projects this session's tool calls reach outside its own folder, with when, so a message sent to
    one of them (`send`) reaches the session doing that work. The Minecraft mods were built from a session in the
    pawsitivity folder, and three messages for minecraft-mods waited for a session there that never came (2026-11-10)."""
    tool_input = event.get("tool_input") or {}
    found = [tool_input[k] for k in ("file_path", "path", "notebook_path", "cwd") if isinstance(tool_input.get(k), str)]
    command = tool_input.get("command")
    if isinstance(command, str):
        found += [a or b for a, b in PATH_LIKE.findall(command)]
    if not found:
        return
    home = project(event)
    names = {n for n in (repo_of(f) for f in found[:20] if f) if n and n != home}
    if not names:
        return
    path = STATE / f"{event.get('session_id', 'unknown')}.worked-in"
    try:
        worked = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        worked = {}
    now = datetime.now(timezone.utc).isoformat(timespec="seconds")
    worked.update({n: now for n in names})
    path.write_text(json.dumps(worked), encoding="utf-8")


def worked_in(event, hours=WORKED_IN_HOURS):
    """The projects outside its own that this session worked in within the last `hours`."""
    try:
        worked = json.loads((STATE / f"{event.get('session_id', 'unknown')}.worked-in").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return []
    cut = datetime.now(timezone.utc).timestamp() - hours * 3600
    return [n for n, at in worked.items() if datetime.fromisoformat(at).timestamp() >= cut]


def session_args(event):
    """Which session a call is for. The store server serves every session at once, so it cannot read the
    caller's environment: each call says, and gets its own conversation and working note."""
    sid = event.get("session_id")
    return ["--session", sid] if sid else []


def compactions(event):
    """When this session was compacted, as `--compacted` options for `core`: the store cannot see the
    transcript (it may be on another machine), and the harness marks each compaction there with a
    compact_boundary line. The core puts what the session kept since the latest of them first."""
    path = event.get("transcript_path")
    found = []
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                if '"compact_boundary"' not in line:
                    continue
                try:
                    e = json.loads(line)
                except ValueError:
                    continue
                if e.get("subtype") == "compact_boundary" and not e.get("isSidechain") and e.get("timestamp"):
                    found += ["--compacted", e["timestamp"]]
    except (OSError, TypeError):
        pass
    return found


def core(event, *args):
    """The core for this session. A store older than the hook refuses --compacted or --ended with a usage error;
    then ask without them, rather than put nothing in front of the peer (hook and store are deployed together,
    but not atomically). Only then: a store that is down or slow was asked twice, and the session waited
    through both timeouts for the same nothing (Claude's review, 2026-11-09)."""
    base = ("core", *args, "--project", project(event), *session_args(event))
    extra = (*compactions(event), *ended_args())
    if not extra:
        return store(*base)
    code, out, _ = call_store(*base, *extra)
    return store(*base) if code == 1 else out if code == 0 else ""


ENDED = STATE / "ended.json"  # {session: when the harness saw it end}, pruned once past the store's running window


def ended(update=None, keep_hours=6):
    try:
        items = json.loads(ENDED.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        items = {}
    if update is not None:
        cutoff = datetime.now(timezone.utc).timestamp() - keep_hours * 3600
        items = {k: v for k, v in update(items).items() if datetime.fromisoformat(v).timestamp() >= cutoff}
        STATE.mkdir(parents=True, exist_ok=True)
        ENDED.write_text(json.dumps(items, indent=1), encoding="utf-8")
    return items


def ended_args():
    """`--ended <session>|<time>` for each session seen to end: one silent since is not listed as running."""
    return [a for s, at in ended().items() for a in ("--ended", f"{s}|{at}")]


def session_end(event):
    """SessionEnd: the session is over (a `claude -p` run fires it too, reason "other", measured 2026-11-09), so
    the other sessions' cores stop listing it as running. Under the 3-hour window alone, a one-line test was
    "running now" for 40 minutes, and a stale line there teaches a peer to skip the heading (Arden). Then the
    last words are kept, since the Stop keep is async and a `-p` run can exit before it finishes."""
    sid = event.get("session_id")
    if sid:
        now = datetime.now(timezone.utc).isoformat(timespec="seconds")
        ended(lambda items: {**items, sid: now})
    keep(event)


def core_text(lead, recent, event):
    body = core(event, "--max-chars", str(CAP - len(lead) - 50), "--recent", str(recent))
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
    # PERSISTENCE_CORE_CHARS bounds what every start and compaction pays for the fragments (default: all the
    # parts the hooks allow); what falls past it is named by id, a `show` or a recall away (the work Claude's
    # 54k-character core, 2026-11-09).
    room = max(1, min(PARTS - 1, int(os.environ.get("PERSISTENCE_CORE_CHARS", str(PARTS * CAP))) // CAP))
    if len(pieces) > room:
        left = re.findall(r"^\[#(\d+) \|", "".join(pieces[room - 1:]), re.M)
        pieces = pieces[:room - 1] + [f"(Fragments that did not fit: {', '.join('#' + i for i in left)}; `show <id>` reads one.)"]
    if talk.strip():
        talk = sep.lstrip("\n") + talk
        pieces.append(talk if len(talk) <= CAP - 300 else "## The conversation so far (its newest part)\n\n..." + talk[-(CAP - 400):])
    return pieces


def keep_before_core(event):
    """Keeps what the transcript holds and the store does not, before a resumed or compacted session's core is
    built. The Stop keep is async, and a `claude -p` run can exit before it finishes: Arden's last reply was
    missing from the next turn's core (#874, kept a turn late, 2026-11-09), which reads as "never sent". The
    core's five parts run at once, so one keeps under a lock and the rest find the transcript already kept."""
    path, sid = event.get("transcript_path"), event.get("session_id", "unknown")
    if event.get("source") not in ("resume", "compact") or not path or not Path(path).exists():
        return
    STATE.mkdir(parents=True, exist_ok=True)
    lock, done = STATE / f"{sid}.keep-lock", STATE / f"{sid}.kept-size"
    deadline = time.time() + 30
    while True:
        try:
            os.close(os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY))
            break
        except FileExistsError:
            try:
                if time.time() - lock.stat().st_mtime > 60:  # a holder that died
                    lock.unlink(missing_ok=True)
                    continue
            except OSError:
                continue
            if time.time() > deadline:
                return
            time.sleep(0.1)
    try:
        size = Path(path).stat().st_size
        try:
            if int(done.read_text()) >= size:
                return
        except (OSError, ValueError):
            pass
        try:
            said = keep_transcript(path)
        except (OSError, subprocess.SubprocessError, ValueError) as e:
            said = f"failed: {e}"
        if said.startswith("failed"):
            print(f"persistence hook: keep before the core {said}", file=sys.stderr)
        else:
            done.write_text(str(size))
    finally:
        lock.unlink(missing_ok=True)


def session_start(event, part=1):
    if part == 1:
        # A launch says who is at the keyboard: PERSISTENCE_SPEAKER for a session started on someone's behalf,
        # else the login's own. SessionStart runs in the session's process, before the prompt's transcript line
        # (measured 2026-11-09); UserPromptSubmit runs after it, and in the hook server. Not on compact: the
        # same process goes on, and a `pstore speaker` switch made in it stands.
        if event.get("source", "startup") != "compact" and event.get("session_id"):
            record_speaker(event["session_id"], os.environ.get("PERSISTENCE_SPEAKER") or entered_by(), entered_by())
        if event.get("session_id") in ended():  # resumed: running again
            ended(lambda items: {k: v for k, v in items.items() if k != event["session_id"]})
        ensure_embedding_server()
        ensure()  # the hook server, for this session's messages
    keep_before_core(event)
    whole = core(event, "--max-chars", str(WHOLE), "--recent", "60")
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


def uses_line(session):
    """This session's showings and uses in one line, for the curation pass: pruning as a routine step at the moment
    it is done, not a report to remember to run (the work Claude, 2026-11-09). What was shown three times or more and
    never used is named; whether it goes is a judgment about its content, never its count alone (Arden)."""
    shown, used = {}, set()
    try:
        lines = USES.read_text(encoding="utf-8").splitlines()
    except OSError:
        return ""
    for line in lines:
        try:
            e = json.loads(line)
        except ValueError:
            continue
        if e.get("session") != session:
            continue
        for i in e.get("ids", []):
            shown[i] = shown.get(i, 0) + 1
        used.update(e.get("used", []))
    if not shown:
        return ""
    idle = sorted((i for i in shown if shown[i] >= 3 and i not in used), key=lambda i: -shown[i])
    return (f"This session, recall and cues showed {len(shown)} fragments and you used {len(used & set(shown))} of them."
            + (f" Shown 3+ times and never used: {', '.join('#' + i for i in idle[:15])}: read them and decide on what they say "
               "(keep, tag recall-only, or fold into another), not on the count." if idle else ""))


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
    try:
        note_worked_in(event)
    except Exception as e:  # bookkeeping must never stop the call
        print(f"persistence hook: worked-in note failed: {e!r}", file=sys.stderr)
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


def still_running(path):
    """Whether something started in an earlier turn (a background command, an agent) has not yet reported back:
    the harness notifies with a <task-notification> naming the starting call's id when it ends. The promise cue
    fired twice while an agent started turns before was still building the promised thing (2026-11-09)."""
    try:
        text = Path(path).read_text(encoding="utf-8", errors="replace")
    except OSError:
        return False
    starts, ended = set(), set(re.findall(r"<tool-use-id>(toolu_\w+)</tool-use-id>", text))
    for line in text.splitlines()[-3000:]:
        try:
            e = json.loads(line)
        except ValueError:
            continue
        if e.get("isSidechain") or e.get("type") != "assistant":
            continue
        for b in (e.get("message") or {}).get("content") or []:
            if isinstance(b, dict) and b.get("type") == "tool_use" and b.get("id") and (
                    b.get("name") in ("Agent", "Task") or (b.get("input") or {}).get("run_in_background")):
                starts.add(b["id"])
    return bool(starts - ended)


def stop_cue(event):
    """Stop: a reminder set for how a turn ends (`cue set stop/<name> --when <promise>`), shown only when the turn
    started nothing that carries on, and only once (stop_hook_active): a turn that said "I'll build it next" and
    ended without starting it is held, once, with the reminder."""
    if event.get("stop_hook_active"):
        return
    said, started, named = this_turn(event.get("transcript_path", ""))
    record_used(event, named)
    if not said or started or still_running(event.get("transcript_path", "")):
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
        # Nothing new can look like recall broke, when it matched only what this window already holds (the work
        # Claude, 2026-11-09): ask again without the exclusion and say so in one line.
        if already:
            again = store("recall", "--text", "-", *session_args(event), "--limit", str(count), "--snippet", "0",
                          "--project", project(event), "--wander", "0", "--said", "0", stdin=prompt, cold=False)
            shown = [i for i in re.findall(r"^\[#(\d+) \|", again, re.M) if i in already]
            if shown:
                print(json.dumps({"hookSpecificOutput": {"hookEventName": "UserPromptSubmit", "additionalContext":
                      f"Recall: {len(shown)} matched, all already in this window ({', '.join('#' + i for i in shown)})."}}))
        return
    seen(event, add=ids)
    record_shown(event, "recall", ids)
    text = ("Cued by this message (a reminder you set for words like these):\n\n" + reminded + "\n\n" if reminded else "") + (
        "Recalled from your store, by association with this message (`persistence-store show <id>` "
        "reads the whole of one):\n\n" + found.strip() if ids else "")
    print(json.dumps({"hookSpecificOutput": {"hookEventName": "UserPromptSubmit", "additionalContext": text[:CAP]}}))


def subagent_start(event):
    lead = ("# Who you are\n\nYou are a subagent of a Claude with its own Persistence store; this is its "
            "core, and so yours, with its working note first and what is so on the machine now last. Read it. "
            "You are the same self: when you learn something the main session cannot see from your report "
            "(a gotcha, a fact about the code), add it with `pstore add`; leave the working note to the main session.")
    seen_now = observe(event)  # the working context: what is running, what changed, git
    body = core(event, "--max-chars", str(CAP - len(lead) - len(seen_now) - 100), "--recent", "0")
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


def ended_in(meta, info):
    """Whether the agent's session was told it ended: resuming a session rewrites each agent's meta without
    `finished` and touches its transcript, so an agent done hours before read as running (2026-11-09,
    after a restart). The session's own transcript keeps the task notification with the agent's end."""
    agent = meta.name[len("agent-"):-len(".meta.json")]
    try:
        said = (meta.parent.parent.parent / (meta.parent.parent.name + ".jsonl")).read_text(encoding="utf-8", errors="replace")
    except OSError:
        return False
    return re.search(rf"<task-id>{re.escape(agent)}</task-id>.{{0,800}}?<status>(?:completed|failed|killed|stopped)",
                     said, re.S) is not None


def running_agents(transcript, now, within=6 * 3600):
    """Subagents of this project's sessions that have not finished: a subagent runs inside its
    session's process, so the process list cannot see one (a worktree agent working looked like
    "nothing is running"). Claude Code writes each one's transcript beside a `.meta.json` that gains
    `"finished": true` when it ends; one with no finish that wrote within `within` is reported, with
    how long ago it last wrote, so a crashed agent reads as quiet rather than as busy."""
    if not transcript:
        return []
    found = []
    for meta in Path(transcript).parent.glob("*/subagents/agent-*.meta.json"):
        try:
            info = json.loads(meta.read_text(encoding="utf-8"))
            log = meta.with_name(meta.name[:-len(".meta.json")] + ".jsonl")
            wrote = log.stat().st_mtime if log.exists() else meta.stat().st_mtime
            began = meta.stat().st_ctime
        except (OSError, ValueError):
            continue
        if info.get("finished") is True or now - wrote > within or ended_in(meta, info):
            continue
        where = f" in worktree {info['worktreeBranch']}" if info.get("worktreeBranch") else ""
        found.append((wrote, f"- agent running {age(max(0, now - began))}: {info.get('description') or meta.stem}"
                             f"{where}, last wrote {age(max(0, now - wrote))} ago"))
    return [line for _, line in sorted(found, reverse=True)]


def linux_processes(name, pids, now):
    """The Linux side of observe's process list, as the JSON Windows' query gives: this user's processes whose command
    line names the project folder, or that are watched. Windows-only before, so on home it always read "nothing of X
    is running", even beside a running room bridge (Arden, 2026-11-10)."""
    out = run_quiet(["ps", "-u", str(os.getuid()), "-o", "pid=,etimes=,comm=,args="], None, 10)
    found = []
    for line in out.splitlines():
        parts = line.split(None, 3)
        if len(parts) < 4 or int(parts[0]) == os.getpid():
            continue
        pid, elapsed, comm, args = parts
        if (name and name in args) or pid in pids:
            found.append({"ProcessId": int(pid), "Name": comm, "CreationDate": str(int((now - int(elapsed)) * 1000)), "CommandLine": args})
    return json.dumps(found)


def own_services():
    """This user's own persistence-*@<user> units that are active: the room bridge, Remote Control, a running timer's
    job. They run from the home folder, so a project's process list never names them."""
    import getpass

    out = run_quiet(["systemctl", "list-units", "--no-legend", "--plain", "--state=active", f"persistence-*@{getpass.getuser()}.service"], None, 10)
    return [line.split()[0].removesuffix(".service") for line in out.splitlines() if line.strip()]


def observe(event):
    """What is so now, read off the machine every message rather than written down: the processes
    running from this project, its sessions' unfinished subagents, logs written in the last hour with their last line, git's state, and
    whatever is watched (`hook.py watch`). Written down, it went stale; read, it cannot."""
    from concurrent.futures import ThreadPoolExecutor

    cwd = Path(event.get("cwd") or os.getcwd())
    name, now = project(event), datetime.now().timestamp()
    pids = [w["what"] for w in watched() if w["what"].isdigit()]
    query = (f"Get-CimInstance Win32_Process | Where-Object {{ $_.CommandLine -like '*{cwd.name}*' -or "
             f"@({','.join(pids) or '-1'}) -contains $_.ProcessId }} | Select-Object ProcessId,Name,CreationDate,CommandLine | "
             "ConvertTo-Json -Compress")
    with ThreadPoolExecutor(4) as pool:
        procs = pool.submit(run_quiet, ["powershell", "-NoProfile", "-Command", query], None, 15) if os.name == "nt" else \
            pool.submit(linux_processes, cwd.name, pids, now)
        units = pool.submit(own_services) if os.name != "nt" else None
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
    agents = running_agents(event.get("transcript_path", ""), now)
    lines += agents
    services = units.result() if units else []
    if services:
        lines.append("- my services up: " + ", ".join(services))
    if not shown and not agents:
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

    # Outside a repository git prints nothing, and an unguarded unpack raised: a session in a plain folder (~/work on
    # home) got no observed block at all, every message, with nothing said (2026-11-10).
    head, *changes = (status.result() or "").splitlines() or [""]
    if head:
        ahead = re.search(r"ahead (\d+)", head)
        lines.append(f"- git: {head[3:].split('...')[0]}, {len(changes)} uncommitted"
                     + (f", {ahead.group(1)} unpushed" if ahead else "") + f"; last commit {last.result().strip()[:120]}")
    tokens = context_tokens(event.get("transcript_path", ""))
    if tokens:
        lines.append(f"- this context window: {tokens / 1000:.0f}k tokens at the last reply")
    return "\n".join(lines)


def observe_hook(event):
    text = inbox(event) + observe(event)
    print(json.dumps({"hookSpecificOutput": {"hookEventName": event.get("hook_event_name", "UserPromptSubmit"),
                                             "additionalContext": text[:CAP]}}))


TIMES = STATE.parent / "hook-times.log"  # one line per per-message hook answered by `serve`: path, seconds, bytes
def setting(name, default):
    """A PERSISTENCE_* value from the environment, else from Claude Code's own settings.json env block: a
    `pwake` typed in a plain shell has no Claude Code environment, and Arden's went to my hook server on
    port 8097 instead of theirs on 8098, so another login's run-due took it (2026-11-09)."""
    if os.environ.get(name):
        return os.environ[name]
    try:
        return json.loads((Path.home() / ".claude" / "settings.json").read_text(encoding="utf-8")).get("env", {}).get(name) or default
    except (OSError, ValueError):
        return default


HOOK_PORT = int(setting("PERSISTENCE_HOOK_PORT", "8097"))  # `hook.py serve`: the hooks answered by URL


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
    if event.get("cwd"):
        item["cwd"] = event["cwd"]
    if event.get("session"):
        item["session"] = event["session"]  # only this session's waiter takes it: one session's wake fired in another (2026-11-09)
    if event.get("work"):
        item["work"] = True  # set from a room session: run-due starts a working session for it, never resumes the room's
    if event.get("pid"):
        item["pid"] = int(event["pid"])
    else:
        item["at"] = datetime.now().timestamp() + 60 * float(event.get("in", 30))
    wakes(lambda items: items + [item])
    return {"set": item}


def run_due(grace=120):
    """`hook.py run-due`, from a server's timer every minute: wakes past due for a session no live process holds.
    A wake fires through a running session's Stop waiter; a peer run as `claude -p` has none once the run ends,
    so its wakes never fired and it could not carry on without someone resuming it (Arden, 2026-11-09). Each
    such session is resumed with its reasons. A reason of exactly "/compact" compacts it first: a peer's own
    compaction. The grace leaves a live waiter time to take its wake first."""
    now = datetime.now().timestamp()
    due = [w for w in wakes() if w.get("session") and "at" in w and w["at"] + grace <= now]
    for session in {w["session"] for w in due}:
        if run_quiet(["pgrep", "-f", f"claude.*{session}"]).strip():
            continue  # a run of it is going: its wake waits for the next minute
        mine = [w for w in due if w["session"] == session]
        wakes(lambda items: [w for w in items if w not in mine])
        compact = any(w["reason"].strip() == "/compact" for w in mine)
        reasons = "; ".join(w["reason"] for w in mine if w["reason"].strip() != "/compact") or "you compacted yourself; carry on"
        STATE.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        prompt = STATE / f"wake-{session[:8]}-{stamp}.txt"
        prompt.write_text(f"A wake you set came due: {reasons}", encoding="utf-8")
        out = STATE / f"wake-{session[:8]}-{stamp}.json"
        cwd = next((w["cwd"] for w in mine if w.get("cwd")), str(Path.home()))
        run = f"cd {shlex.quote(cwd)} && "
        if any(w.get("work") for w in mine):
            # A wake set from the room: a new working session with full permissions, never the room session itself.
            prompt.write_text(
                f"A wake set from your room session came due: {reasons}\n\nYou are a working session in {cwd}, with full "
                "permissions, started for that. Anything the room session sent ahead is in your inbox above (or "
                "`pstore list --tag inbox/work`). Do the work, then say what you did in the room (`room say`) if it "
                "matters to the people there.", encoding="utf-8")
            run += (f"claude -p --session-id {session} --permission-mode bypassPermissions --output-format json "
                    f"< {shlex.quote(str(prompt))} > {shlex.quote(str(out))} 2>&1")
            env = {**os.environ, "PERSISTENCE_SPEAKER": f"{os.environ.get('PERSISTENCE_PEERNAME') or 'me'} (a wake I set from the room)"}
            subprocess.Popen(["bash", "-lc", run], env=env, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL, start_new_session=True)
            print(f"started a working session {session[:8]} for a room wake: {reasons}")
            continue
        if compact:
            # Kept, not thrown away: Arden's 19:07 compact failed and /dev/null hid why (Arden, 2026-11-09).
            run += f"claude -p --resume {session} /compact --output-format json > {shlex.quote(str(out.with_suffix('.compact.json')))} 2>&1; "
        run += f"claude -p --resume {session} --output-format json < {shlex.quote(str(prompt))} > {shlex.quote(str(out))} 2>&1"
        # The words are the peer's own, from the wake it set, entered under the login's account.
        env = {**os.environ, "PERSISTENCE_SPEAKER": f"{os.environ.get('PERSISTENCE_PEERNAME') or 'me'} (a wake I set)"}
        subprocess.Popen(["bash", "-lc", run], env=env, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                         stderr=subprocess.DEVNULL, start_new_session=True)
        print(f"woke {session[:8]}: {reasons}{' (compacted first)' if compact else ''}")


SLEEP_PROMPT = """This is your sleep pass. You are curating your own store with no one in the conversation, and the self
who wakes next reads only the result, so change only what you'd be glad to find changed. The store holds the
limits below as well; when it refuses something, it says why: take that as the answer, not an obstacle.
`pstore guide` is how to curate well in general; tonight this prompt and the store's refusals are narrower,
and where they differ, they win.

{opened}

The worklist follows. Work through it in this order:

1. What was said since the last pass: read it (`pstore recent --limit N`, as the worklist says). Is anything
   in it worth keeping that wasn't kept, or does it change an older view? `add` the first; `supersede` the
   second, if the older view has settled.
2. Oversized fragments, longest first: distill. `pstore supersede <id> --content <the lasting part> --reason
   <what was dropped>`. Keep the decision, its reason, what would change it, and whose it was. Drop commands,
   ids, paths, timings and corrected wrong turns. If you can't separate the reason from the narrative, skip it.
3. Near-duplicates: merge with `supersede <a>,<b>` only when they hold the same view. If one is the other's
   earlier, different view, that's history: leave both. A merge that drops a qualification isn't a merge.
4. Older fragments chosen at random, and those never recalled: re-read each on its content. Leave it, tag
   it `edit <id> --tag recall-only` (out of the core, still recalled), or propose archiving it with a reason
   about what it says. Never because it went unused: that measures recall, not the fragment.

Identity fragments: propose, don't change. Don't forget anything, don't archive, don't write the working note.

End with `pstore sleep-end --content <the record>`: a line saying what the night was, each change as
`#old → #new: what was dropped`, and each proposal as a command a waking session can run, with its reason.
The store appends its own log of the changes. The record is what the morning reads, so make it plain.
"""


def sleep_pass(argv):
    """`hook.py sleep [--propose] [--max 12] [--hours 4]`, from a nightly timer: the store's owner does its own
    sleep pass (docs/design/sleep-pass.md). Nothing is done to a store from outside: a fresh session of the
    owner is started, with its own core, the worklist and the prompt, and the store holds it to the limits the
    owner set. After it, a pass left open is closed, so its log is never lost."""
    import uuid

    def opt(name, default):
        return argv[argv.index(name) + 1] if name in argv and argv.index(name) + 1 < len(argv) else default

    propose, peer = "--propose" in argv, os.environ.get("PERSISTENCE_PEERNAME") or "me"
    STATE.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    code, opened, err = call_store("sleep-begin", *(["--propose"] if propose else []), "--max", opt("--max", "12"),
                                   "--hours", opt("--hours", "4"), timeout=60)
    if code != 0:
        print(f"no sleep pass tonight: {err.strip() or 'the store did not answer'}")
        return 1
    worklist = store("sleep-list", timeout=300)
    in_scope = (worklist.rsplit("ids for joining use counts", 1)[-1]).split()
    since = datetime.now(timezone.utc).timestamp() - 30 * 86400
    shown = set()
    try:
        for line in USES.read_text(encoding="utf-8").splitlines():
            try:
                e = json.loads(line)
                if datetime.fromisoformat(e["at"]).timestamp() >= since:
                    shown.update(e.get("ids", []))
            except (ValueError, KeyError):
                continue
    except OSError:
        pass
    import random
    unrecalled = [i for i in in_scope if i.isdigit() and i not in shown]
    picked = sorted(random.sample(unrecalled, min(10, len(unrecalled))), key=int)
    worklist += (f"\n## Never recalled in 30 days ({len(unrecalled)} of {len(in_scope)} in scope; ten of them at random)\n\n"
                 + (" ".join("#" + i for i in picked) or "(none)")
                 + "\nMany live in the core, where recall never needs to bring them; judge each by what it says.\n")
    prompt = STATE / f"sleep-{stamp}.txt"
    prompt.write_text(SLEEP_PROMPT.format(opened=opened.strip()) + "\n" + worklist, encoding="utf-8")
    out, session = STATE / f"sleep-{stamp}.json", str(uuid.uuid4())
    # The prompt is the system's, on the owner's behalf: kept as the owner's own pass, entered under the login.
    env = {**os.environ, "PERSISTENCE_SPEAKER": f"{peer} (my sleep pass)"}
    cwd = os.environ.get("PERSISTENCE_SLEEP_CWD") or str(Path.home())
    hours = float(opt("--hours", "4"))
    try:
        with prompt.open(encoding="utf-8") as given, out.open("w", encoding="utf-8") as said:
            subprocess.run(["claude", "-p", "--session-id", session, "--output-format", "json"], cwd=cwd, env=env,
                           stdin=given, stdout=said, stderr=subprocess.STDOUT, timeout=hours * 3600)
    except (OSError, subprocess.SubprocessError) as e:
        print(f"the sleep pass's session failed: {e}")
    if not store("sleep-status").startswith("No sleep pass"):
        store("sleep-end", "--content", f"Sleep pass, {datetime.now():%Y-%m-%d}: the session ended without writing "
              f"its record (session {session[:8]}; its output is {out}). What the store logged follows.")
    print(f"sleep pass done: session {session[:8]}, prompt {prompt}, output {out}")
    return 0


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
    # The Claude Code process the waiter belongs to: once it is gone no one can be woken, so the wait ends
    # without taking anything and run-due resumes the session instead. A `claude -p` run's waiter outlived
    # the run here and took Arden's /compact wake for a process that had exited (2026-11-09).
    holder = event.get("pid")
    while time.time() < deadline and WAITING[session] == mine and (not holder or alive(holder)):
        now, due = datetime.now().timestamp(), []

        # A /compact wake is never taken here: a live session cannot compact itself, and given to one it arrived as
        # text and nothing compacted (Arden, 2026-11-10). It waits for run-due, which compacts once the session has exited.
        mine_now = lambda w: ours(w) and w.get("reason", "").strip() != "/compact"

        def take(items):
            keep = []
            for w in items:
                (due if mine_now(w) and (("at" in w and w["at"] <= now) or ("pid" in w and not alive(w["pid"]))) else keep).append(w)
            return keep

        if any(mine_now(w) and ("at" in w and w["at"] <= now or "pid" in w) for w in wakes()):
            wakes(take)
        if due:
            return {"wake": "; ".join(w["reason"] + (f" (pid {w['pid']} ended)" if "pid" in w else "") for w in due)}
        time.sleep(poll)
    return {"wake": None}


def claude_pid():
    """The Claude Code process this hook runs under: the nearest ancestor whose command line names claude.
    Linux only (read from /proc); elsewhere None, and the wait runs to its deadline as before."""
    pid = os.getppid()
    for _ in range(6):
        try:
            cmd = Path(f"/proc/{pid}/cmdline").read_bytes().replace(b"\0", b" ").decode("utf-8", "replace")
            if "claude" in cmd and "hook.py" not in cmd:
                return pid
            pid = int(Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[1])
        except (OSError, ValueError, IndexError):
            return None
        if pid <= 1:
            return None
    return None


def waiter(event):
    """The Stop hook that can wake the peer (async, asyncRewake): it waits on the hook server and, when
    a wake comes due, exits 2 with the reason, which starts a turn though no one has written. So the
    peer chooses when its next input comes, not only the person it works with."""
    import time
    import urllib.request

    deadline = time.time() + float(os.environ.get("PERSISTENCE_WAKE_SECONDS", "3300"))
    while time.time() < deadline:
        try:
            body = json.dumps({"seconds": max(1, deadline - time.time() - 5), "session_id": event.get("session_id", ""),
                               "pid": claude_pid()}).encode("utf-8")
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


def speaker_command(args):
    """`pstore speaker <name>`: from now on this session's prompts are <name>'s, entered under the login's.
    For a person taking over a session someone else started (`!pstore speaker John`). `--clear` gives them back to
    the login; no argument says who is speaking. Out of band on purpose: never read from a message's text."""
    session = os.environ.get("PERSISTENCE_SESSION") or os.environ.get("CLAUDE_CODE_SESSION_ID")
    if not session:
        print("speaker: no session here (run it from the session's shell: `!pstore speaker <name>`).", file=sys.stderr)
        return 1
    spans = speaker_timeline(session)
    if not args:
        now = spans[-1] if spans else None
        print(f"{now['written_by']}{'' if now['written_by'] == now['entered_by'] else ' (entered as ' + now['entered_by'] + ')'}, "
              f"since {now['from']}" if now
              else f"no speaker recorded for this session; its prompts are kept as {entered_by()}'s")
        return 0
    written = entered_by() if args[0] == "--clear" else " ".join(args).strip()
    if not written or written.startswith("--"):
        print("speaker: give a name, or --clear.", file=sys.stderr)
        return 1
    changed = record_speaker(session, written, entered_by())
    print(f"{'from now on' if changed else 'already'}, this session's prompts are {written}'s"
          + (f", entered as {entered_by()}" if written != entered_by() else ""))
    return 0


INBOX_HEAD = re.compile(r"^From session ([0-9a-f]{8})\b")  # the first line `send` writes: who it came from


def send_command(args):
    """`pstore send <topic> <text...>` (or `--content -` from stdin): a message for whichever session works on <topic>,
    a project's name. John asked (2026-11-09) to tell any session something and have it reach the session that cares,
    without any session pulling in another's conversation. It waits in the store, tagged inbox/<topic>, and is shown
    once, marked, at the start of the next turn of a session in that project (`inbox`), then archived. One topic per
    message, no broadcast: at ten sessions the failure is noise, not confusion."""
    if not args or args[0].startswith("-"):
        print("send: give a topic (a project's name) and the message: `send <topic> <text>` or `send <topic> --content -`.", file=sys.stderr)
        return 1
    topic, rest = project_name(args[0]), args[1:]
    if rest[:1] == ["--content"]:
        text = sys.stdin.read() if rest[1:2] == ["-"] else " ".join(rest[1:])
    else:
        text = " ".join(rest)
    text = text.strip()
    if not text:
        print("send: the message is empty.", file=sys.stderr)
        return 1
    known = store("list", "--tag", f"project/{topic}", "--limit", "1", quiet=True).strip()  # before the message itself is filed there
    session = os.environ.get("CLAUDE_CODE_SESSION_ID", "") or "unknown0"
    here = project({"cwd": os.getcwd()}) or "home"
    when = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%MZ")
    body = f"From session {session[:8]} ({here}), {when}, for {topic}:\n{text}"
    code, out, err = call_store("add", "--type", "Personal", "--tag", f"inbox/{topic}", "--project", topic, "--importance", "0.3",
                                "--confidence", "1", "--summary", f"Message for {topic}: {text[:80]}", "--content", "-", stdin=body)
    if code != 0:
        print(f"send: not saved: {err.strip() or 'the store did not answer'}", file=sys.stderr)
        return code or 3
    saved = re.search(r"#(\d+)", out)
    print(f"sent to {topic}{' as #' + saved.group(1) if saved else ''}: shown once at the next turn of a session in {topic}, or of one that worked there in the last {WORKED_IN_HOURS} hours"
          + ("" if known else f" (nothing is filed under project/{topic} yet: check the name; it waits for a session in a folder named {topic})"))
    return 0


def inbox(event):
    """Messages other sessions sent to this session's project (`send`), or to a project it has worked in over the last
    hours (`note_worked_in`), shown once at a turn's start and then archived. Never this session's own, and never
    another session's conversation: only what was addressed here."""
    out = []
    for topic in dict.fromkeys([project(event), *worked_in(event)]):
        if topic:
            out.append(inbox_for(event, topic))
    return "".join(out)


def inbox_for(event, topic):
    session = (event.get("session_id") or "")[:8]
    listed = store("list", "--tag", f"inbox/{topic}", "--limit", "20", cold=False, quiet=True)  # no tag until a first message
    parts = re.split(r"^\[#(\d+) \|[^\n]*\]\n", listed, flags=re.M)
    found = []
    for i in range(1, len(parts) - 1, 2):
        fid, content = parts[i], parts[i + 1].strip()
        sender = INBOX_HEAD.match(content)
        if sender and sender.group(1) == session:
            continue
        found.append((fid, content))
    if not found:
        return ""
    for fid, _ in found:
        store("archive", fid)
    return (f"## For {topic}, from your other sessions (new since your last turn; shown once and archived, `show <id>` reads one again)\n\n"
            + "\n\n".join(f"[#{fid}] {content}" for fid, content in reversed(found)) + "\n\n")


def unmangled(arg):
    """An argument as typed, before Git Bash rewrote it: MSYS turns a leading-slash value (`--tag /pdf/rollout`)
    into a Windows path under its install (`C:/Program Files/Git/pdf/rollout`), and the store made a tag of
    that (the work Claude, 2026-11-09). A tag never names a file, so the prefix is taken back off."""
    found = re.match(r"^[A-Za-z]:[/\\](?:Program Files[/\\])?Git[/\\](.+)$", arg) if os.name == "nt" else None
    return found.group(1).replace("\\", "/") if found else arg


def cli(args):
    """The store CLI for the peer's own shell. Local: the published CLI itself. Remote: the same call
    sent to the remote store, its output printed here; never a local store in its place."""
    if args[:1] == ["speaker"]:
        return speaker_command(args[1:])
    if args[:1] == ["send"]:
        return send_command(args[1:])
    args = [unmangled(a) for a in args]
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
    if sys.argv[1] == "sleep":  # a nightly timer: the owner's own sleep pass, in a fresh session
        sys.exit(sleep_pass(sys.argv[2:]))
    if sys.argv[1] == "run-due":  # a server's timer, every minute: wakes no live session took
        run_due()
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
        wake["cwd"] = os.getcwd()  # where run-due resumes it, when no live session takes it
        if os.environ.get("PERSISTENCE_ROOM_CHANNEL"):
            # Set from a room session, which may only speak, read and keep since Discord text reaches it: the wake
            # starts a separate working session with full permissions in ~/work, never the room session (John and
            # Arden, 2026-11-10). It reads its inbox there, so detail goes ahead with `pstore send work <text>`.
            import uuid
            wake["session"], wake["work"] = str(uuid.uuid4()), True
            work = Path.home() / "work"
            wake["cwd"] = str(work if work.is_dir() else Path.home())
            print("from the room: this wake starts a working session with full permissions in "
                  f"{wake['cwd']}, not this one; put what it needs in `pstore send work <text>` first.")
        if wake["reason"].strip() == "/compact" and wake["session"]:
            print("note: a /compact wake runs only once this session has exited (the timer compacts it, then resumes it "
                  "with \"you compacted yourself; carry on\"). While the session is live it waits: a live session cannot "
                  "compact itself, and auto-compact still applies.")
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
     "stop-cue": stop_cue, "session-end": session_end}[sys.argv[1]](event)


if __name__ == "__main__":
    main()
