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
                                  conversation) in front of every session, and after every compaction
  subagent-start  SubagentStart   gives every subagent the same core, without the conversation

Configured by environment (settings.json "env"): PERSISTENCE_STORE_DB (this Claude's store, one per
Claude), PERSISTENCE_STORE_EXE (the published persistence-store), PERSISTENCE_SELECTEDLOCALPEER (the
human's name), PERSISTENCE_CURATE_AT (tokens, default 500000; set autoCompactWindow above it).

Standard library only, Python 3.9+: hooks run under whatever `python` the shell finds.
"""

import json
import os
import subprocess
import sys
from pathlib import Path

CAP = 9500  # Claude Code replaces injected context over 10,000 characters with a file path and a preview
STATE = Path.home() / ".claude" / "persistence" / "state"


def store(*args, timeout=60):
    exe = os.environ.get("PERSISTENCE_STORE_EXE", "persistence-store")
    out = subprocess.run([exe, *args], capture_output=True, text=True, encoding="utf-8", timeout=timeout)
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


def core_text(lead, recent):
    body = store("core", "--max-chars", str(CAP - len(lead) - 50), "--recent", str(recent))
    return lead + "\n\n" + body if body.strip() else ""


def session_start(event):
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
    sys.stdout.write(core_text(lead, recent=20))


def subagent_start(event):
    lead = ("# Who you are\n\nYou are a subagent of a Claude with its own Persistence store; this is its "
            "core, and so yours. Read it; do not write to the store, the main session does that.")
    text = core_text(lead, recent=0)
    if text:
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "SubagentStart", "additionalContext": text}}))


def main():
    event = json.load(sys.stdin) if not sys.stdin.isatty() else {}
    {"keep": keep, "curate": curate, "precompact": precompact,
     "session-start": session_start, "subagent-start": subagent_start}[sys.argv[1]](event)


if __name__ == "__main__":
    main()
