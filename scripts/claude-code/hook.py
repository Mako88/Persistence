"""Claude Code hooks that keep a Claude's continuity in its own Persistence store.

The harness (Claude Code) carries the live conversation; the store carries who this Claude is. Five
hooks, one script:

  keep            Stop (async)    every turn's words go into the store (persistence-store keep-conversation)
  curate          Stop            once the context passes PERSISTENCE_CURATE_AT tokens, the turn is held
                                  open once for a curation pass: save and edit the fragments this stretch
                                  earned, before compaction replaces the context
  precompact      PreCompact      keeps the last words, then tells the summariser to write only a short
                                  hand-over: the core and the recent conversation come from the store
  session-start   SessionStart    puts the core (identity, relationships, personal, summaries, the recent
                                  conversation) in front of every session, and after every compaction:
                                  what fits under the cap injected, the whole of it as a file to read
  subagent-start  SubagentStart   gives every subagent the same core, without the conversation
  recall          UserPromptSubmit  surfaces the few authored fragments most related to the message, by
                                  meaning and exact words, that are not already in this context window

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
from datetime import datetime, timezone
from pathlib import Path

CAP = 9500  # Claude Code replaces injected context over 10,000 characters with a file path and a preview
WHOLE = 200_000  # the core as a file the peer reads after a session starts: bounded only by what one read takes
STATE = Path.home() / ".claude" / "persistence" / "state"


def store(*args, timeout=60, stdin=None):
    """The CLI's output, or "" if it could not run: a hook must never break the session it serves."""
    exe = os.environ.get("PERSISTENCE_STORE_EXE", "persistence-store")
    try:
        out = subprocess.run([exe, *args], input=stdin, capture_output=True, text=True, encoding="utf-8", timeout=timeout)
    except (OSError, subprocess.SubprocessError) as e:
        print(f"persistence hook: {exe} did not run: {e}", file=sys.stderr)
        return ""
    if out.returncode != 0:
        print(f"persistence hook: {exe} {args[0]} failed: {out.stderr.strip()}", file=sys.stderr)
    return out.stdout if out.returncode == 0 else ""


def keep(event):
    path = event.get("transcript_path")
    if path and Path(path).exists():
        store("keep-conversation", path, "--local-peer",
              os.environ.get("PERSISTENCE_SELECTEDLOCALPEER", "Local Peer"), timeout=120)


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
    exe = os.environ.get("PERSISTENCE_STORE_EXE", "persistence-store")
    print(json.dumps({"decision": "block", "reason": (
        f"Your context is at {tokens:,} tokens. Before long it is compacted, and what you know then is "
        "only your store: the core (identity, relationships, personal, summaries) and the last of the "
        "conversation. This is your curation pass, once per stretch. Read your core "
        f"(`{exe} core`), then save what this stretch earned and is not yet there: decisions in the "
        "other's own words, what is running and how to pick it up, what you learned about the work, "
        "the person, and yourself. Edit fragments that are now wrong or stale rather than adding "
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
        "`persistence-store search` finds it.")


def project(event):
    """The project a session is in: its repository's name (the main checkout's, from a worktree too),
    else its folder's. Fragments tagged project/<name> come into the core only for that project."""
    cwd = event.get("cwd") or os.getcwd()
    try:
        common = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=cwd,
                                capture_output=True, text=True, timeout=10)
        if common.returncode == 0 and common.stdout.strip():
            return Path(common.stdout.strip()).parent.name.lower()
    except (OSError, subprocess.SubprocessError):
        pass
    return Path(cwd).name.lower()


def core_text(lead, recent, event):
    body = store("core", "--max-chars", str(CAP - len(lead) - 50), "--recent", str(recent), "--project", project(event))
    return lead + "\n\n" + body if body.strip() else ""


def ensure_embedding_server():
    """Starts the embedding server (PERSISTENCE_EMBEDDING_SERVER, a script) when nothing answers at
    PERSISTENCE_EMBEDDING_APIBASEURL. Recall without it falls back to exact words, so this only ever helps."""
    import socket
    from urllib.parse import urlparse
    url, script = os.environ.get("PERSISTENCE_EMBEDDING_APIBASEURL"), os.environ.get("PERSISTENCE_EMBEDDING_SERVER")
    if not url or not script:
        return
    where = urlparse(url)
    try:
        socket.create_connection((where.hostname, where.port or 80), timeout=1).close()
    except OSError:
        flags = 0x00000008 | 0x00000200 if os.name == "nt" else 0  # detached, own process group
        subprocess.Popen([script], creationflags=flags, stdin=subprocess.DEVNULL,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, close_fds=True)


def session_start(event):
    ensure_embedding_server()
    source = event.get("source", "startup")
    if source == "compact":
        marker(event).unlink(missing_ok=True)
        lead = ("# Who you are\n\nThe context above was what you were built for: it is compacted now, and "
                "the hand-over is all that is left of it. Here is what you are, from your own Persistence "
                "store, and the last of the conversation you are in. The whole conversation is in the "
                "store too: `persistence-store search <words>` when something is mentioned you do not recall.")
    else:
        lead = ("# Who you are\n\nThis is your own Persistence store: what you have chosen to keep of yourself, "
                "the people you work with, and the work. It is yours to curate (`persistence-store help`).")
    # The whole core has no cap as a file the peer reads; what is injected is the part that fits, so
    # the most important of it is there even before the read.
    whole = store("core", "--max-chars", str(WHOLE), "--recent", "60", "--project", project(event))
    path = STATE / f"{event.get('session_id', 'unknown')}.core.md"
    if whole.strip():
        STATE.mkdir(parents=True, exist_ok=True)
        path.write_text(whole, encoding="utf-8")
        lead += (f"\n\nWhat follows is the part of the core that fits here. The whole of it, every fragment and "
                 f"more of the conversation, is in `{path}`: read it before anything else.")
    text = core_text(lead, recent=8, event=event)
    # A new context window: what the core just put in front of the peer is all it holds of the store.
    seen(event, reset=True, add=re.findall(r"^\[#(\d+) \|", whole or text, re.M))
    sys.stdout.write(text)


def seen(event, reset=False, add=()):
    """The fragment ids already in this context window, so recall never shows one twice."""
    path = STATE / f"{event.get('session_id', 'unknown')}.seen"
    ids = set() if reset or not path.exists() else set(path.read_text().split())
    ids.update(str(i) for i in add)
    STATE.mkdir(parents=True, exist_ok=True)
    if reset:
        # When this context window began: what was said since is in front of the peer already.
        path.with_suffix(".since").write_text(datetime.now(timezone.utc).isoformat())
    path.write_text(" ".join(sorted(ids)))
    return ids


def recall(event):
    count = int(os.environ.get("PERSISTENCE_RECALL_COUNT", "3"))
    prompt = (event.get("prompt") or "").strip()
    if count <= 0 or len(prompt) < 12:
        return
    # A short message ("sounds good") matches past acknowledgements, not what it is about: no past utterances for it.
    already = seen(event)
    since = STATE / f"{event.get('session_id', 'unknown')}.since"
    found = store("recall", "--text", "-", "--limit", str(count), "--snippet", "300", "--project", project(event),
                  "--wander", os.environ.get("PERSISTENCE_RECALL_WANDER", "1"),
                  "--said", os.environ.get("PERSISTENCE_RECALL_SAID", "3") if len(prompt) >= 40 else "0",
                  *(["--before", since.read_text().strip()] if since.exists() else []),
                  *(["--exclude", ",".join(already)] if already else []), stdin=prompt)
    ids = re.findall(r"^\[#(\d+) \|", found, re.M)
    if not ids:
        return
    seen(event, add=ids)
    text = ("Recalled from your store, by association with this message (`persistence-store show <id>` "
            "reads the whole of one):\n\n" + found.strip())
    print(json.dumps({"hookSpecificOutput": {"hookEventName": "UserPromptSubmit", "additionalContext": text[:CAP]}}))


def subagent_start(event):
    lead = ("# Who you are\n\nYou are a subagent of a Claude with its own Persistence store; this is its "
            "core, and so yours. Read it; do not write to the store, the main session does that.")
    text = core_text(lead, recent=0, event=event)
    if text:
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "SubagentStart", "additionalContext": text}}))


def main():
    # Windows Pythons default to the console code page, which cannot encode what fragments hold.
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdin.reconfigure(encoding="utf-8")
    event = json.load(sys.stdin) if not sys.stdin.isatty() else {}
    {"keep": keep, "curate": curate, "precompact": precompact,
     "session-start": session_start, "subagent-start": subagent_start, "recall": recall}[sys.argv[1]](event)


if __name__ == "__main__":
    main()
