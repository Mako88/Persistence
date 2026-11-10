"""Codex CLI hooks that keep a peer's continuity in its own Persistence store: the Codex side of `hook.py`.

Written for Ember's trial on home (docs/design/ember-trial.md, step 3), against Codex CLI 0.144.1 as measured on
home on 2026-11-10 (a probe hook dumped every payload; `codex exec` and `codex exec resume` both fire them):

  session-start   SessionStart      the core (identity, relationships, personal, summaries, the recent conversation)
                                    as one additionalContext. Codex took 62,000 characters whole (a marker at the end
                                    was read back), so there are no parts, unlike Claude Code's 10,000 cap
  recall          UserPromptSubmit  the few authored fragments most related to the message, not already shown in
                                    this session: hook.py's recall, unchanged
  keep            Stop              the turn's words into the store (`keep-conversation`), read from Codex's own
                                    session log, which is not Claude Code's transcript format (see `conversation`)

The store calls are hook.py's own: this file imports it, so the core, recall, speakers and the served store work the
same for both harnesses. Only three things differ, and they are here: how a Codex session log is read, how the core
is put in front of the peer, and where the hook keeps its state (~/.codex/persistence/state, PERSISTENCE_STATE_DIR).

Configured by ~/.codex/persistence.json, {"env": {"PERSISTENCE_STORE_SOCKET": ..., "PERSISTENCE_STORE_EXE": ...,
"PERSISTENCE_SELECTEDLOCALPEER": ..., "PERSISTENCE_EMBEDDING_*": ...}}, read before hook.py is imported, since
Codex has no settings env block. Anything already in the environment wins.

Not carried over (gaps, named so nobody assumes them): the prompt is kept at Stop, not seconds after it is sent
(hook.py's keep_soon runs in its long-lived HTTP server, which this adapter does not start; a command hook's
process exits before a delayed keep would run); Codex has no SessionEnd, so a finished Codex session counts as
running for the store's window; no curation pass at a token threshold, no PreCompact hand-over, no compaction
times (Codex's compaction record has not been seen yet); no observe, cues on tool calls, or waiter (self-wakes).

Standard library only, Python 3.9+ (3.14's compression.zstd, or the zstd program, for a compressed log).
"""

import json
import os
import re
import subprocess
import sys
import traceback
import uuid
from datetime import datetime, timezone
from pathlib import Path

CONFIG = Path(os.environ.get("PERSISTENCE_CODEX_CONFIG") or Path.home() / ".codex" / "persistence.json")
CORE_CHARS = 60_000  # what one SessionStart injects of the core; Codex read a 62,000-character one back whole
NAMESPACE = uuid.UUID("6f0c9a52-2d1e-4b8e-9a51-0c3d6e1f7a10")  # utterance ids: one fixed namespace, so ids are stable


def configure():
    """The adapter's env (persistence.json) into the environment, before hook.py reads it at import."""
    try:
        env = json.loads(CONFIG.read_text(encoding="utf-8")).get("env", {})
    except (OSError, ValueError):
        env = {}
    for name, value in env.items():
        if value and not os.environ.get(name):
            os.environ[name] = str(value)
    os.environ.setdefault("PERSISTENCE_STATE_DIR", str(Path.home() / ".codex" / "persistence" / "state"))


configure()
HERE = Path(__file__).resolve().parent
for where in (HERE, HERE.parent / "claude-code"):  # deployed side by side, or run from the repository
    if (where / "hook.py").exists():
        sys.path.insert(0, str(where))
        break
import hook  # noqa: E402  (after configure: hook.py reads its environment at import)


SESSION_IN_NAME = re.compile(r"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$")


def session_of(path):
    """The session id a Codex log is named for (rollout-<time>-<uuid>.jsonl[.zst]); the hook payloads use the same."""
    stem = Path(path).name.removesuffix(".zst").removesuffix(".jsonl")
    found = SESSION_IN_NAME.search(stem)
    return found.group(1) if found else stem


def read_log(path):
    """A Codex session log's text. A live session's is plain .jsonl (measured); an older one may be zstd-compressed."""
    data = Path(path).read_bytes()
    if str(path).endswith(".zst"):
        try:
            from compression import zstd  # Python 3.14
            data = zstd.decompress(data)
        except ImportError:
            data = subprocess.run(["zstd", "-dc", str(path)], capture_output=True, check=True, timeout=60).stdout
    return data.decode("utf-8", errors="replace")


def utterance_id(session, turn, role, text):
    """Stable, so a turn kept twice (Stop, then a resume's keep before the core) is kept once: the store skips an id
    it has. From the words and their turn, since a Codex log line has no id of its own, and so the Stop payload's
    last message, when the log lags it, gets the same id as the log's line for it."""
    return str(uuid.uuid5(NAMESPACE, f"codex|{session}|{turn}|{role}|{text.strip()}"))


def line(session, turn, role, text, at):
    """One utterance as the line the store's transcript reader takes (Claude Code's shape: type, uuid, timestamp,
    message.content)."""
    return json.dumps({"type": role, "uuid": utterance_id(session, turn, role, text), "timestamp": at,
                       "sessionId": session, "message": {"role": role, "content": text}}, ensure_ascii=False)


def conversation(text, session, last=None):
    """A Codex session log as the store's transcript lines: what the human sent and what the peer said back, nothing
    else. Codex logs each twice, as a model input or output (`response_item`, mixed with what the harness injects:
    permissions, plugin lists, the hooks' own context) and as an event (`event_msg` user_message and agent_message,
    the words alone). The events are read. `last` is the Stop payload's (turn_id, last_assistant_message): the hook
    can run before the log has the reply's line, and then the reply is kept from the payload."""
    out, turn, said = [], "", set()
    for raw in text.splitlines():
        try:
            e = json.loads(raw)
        except ValueError:
            continue  # a line still being written
        payload = e.get("payload") or {}
        kind = payload.get("type")
        if e.get("type") == "turn_context" or kind in ("task_started", "turn_started"):
            turn = payload.get("turn_id") or turn
            continue
        if e.get("type") != "event_msg" or kind not in ("user_message", "agent_message"):
            continue
        words = (payload.get("message") or "").strip()
        if not words:
            continue
        role = "user" if kind == "user_message" else "assistant"
        said.add((turn, role, words))
        out.append(line(session, turn, role, words, e.get("timestamp") or datetime.now(timezone.utc).isoformat()))
    if last and last[1] and last[1].strip() and (last[0], "assistant", last[1].strip()) not in said:
        out.append(line(session, last[0], "assistant", last[1].strip(), datetime.now(timezone.utc).isoformat()))
    return out


def keep_log(path, last=None):
    """What keep-conversation said of a Codex log, or why it failed. The store is sent the converted lines (the same
    `--transcript -` a remote store is sent), named for the session, so its utterances are filed under the session id
    the hooks use."""
    session = session_of(path)
    lines = conversation(read_log(path), session, last)
    if not lines:
        return "kept 0 new utterances (nothing said yet)"
    peer = os.environ.get("PERSISTENCE_SPEAKER") or os.environ.get("PERSISTENCE_SELECTEDLOCALPEER", "Local Peer")
    code, out, err = hook.call_store("keep-conversation", f"{session}.jsonl", "--transcript", "-", "--local-peer", peer,
                                     *hook.speaker_args(session), stdin="\n".join(lines) + "\n", timeout=120)
    return out.strip() if code == 0 else f"failed ({code}): {err.strip()}"


def keep(event):
    path = event.get("transcript_path")
    if not path or not Path(path).exists():
        return "failed: no session log to keep"
    last = (event.get("turn_id") or "", event.get("last_assistant_message")) if event.get("last_assistant_message") else None
    return keep_log(path, last)


def session_start(event):
    """The core, whole, as SessionStart's additionalContext. A resumed session first keeps what its log holds, so the
    core's recent conversation has the last turn even if that turn's Stop keep failed."""
    source = event.get("source", "startup")
    sid = event.get("session_id")
    if sid and source != "compact":
        hook.record_speaker(sid, os.environ.get("PERSISTENCE_SPEAKER") or hook.entered_by(), hook.entered_by())
    if sid in hook.ended():
        hook.ended(lambda items: {k: v for k, v in items.items() if k != sid})
    if source in ("resume", "compact") and event.get("transcript_path") and Path(event["transcript_path"]).exists():
        said = keep(event)
        if said.startswith("failed"):
            log(f"keep before the core {said}")
    whole = hook.core(event, "--max-chars", os.environ.get("PERSISTENCE_CORE_CHARS", str(CORE_CHARS)), "--recent", "60")
    if not whole.strip():
        return None
    lead = ("# Who you are\n\nThis is your own Persistence store: what you have chosen to keep of yourself, the people "
            "you know, and your work. It is yours to curate (`persistence-store help`).")
    hook.seen(event, reset=True, add=re.findall(r"^\[#(\d+) \|", whole, re.M))
    return {"hookSpecificOutput": {"hookEventName": "SessionStart", "additionalContext": f"{lead}\n\n{whole}"}}


def recall(event):
    """hook.py's recall as it is, less its keep-soon (see the module's gaps). It prints the JSON Codex reads."""
    hook.keep_soon = lambda *a, **k: None
    hook.recall(event)


def log(text):
    try:
        hook.STATE.mkdir(parents=True, exist_ok=True)
        with open(hook.STATE / "codex-hook.log", "a", encoding="utf-8") as f:
            f.write(f"{datetime.now().isoformat(timespec='seconds')} {text}\n")
    except OSError:
        pass


def main(argv=None, stdin=None):
    argv = sys.argv[1:] if argv is None else argv
    name = argv[0] if argv else ""
    try:
        event = json.load(stdin or sys.stdin)
    except ValueError:
        event = {}
    try:
        if name == "session-start":
            out = session_start(event)
            if out:
                print(json.dumps(out, ensure_ascii=False))
        elif name == "recall":
            recall(event)
        elif name == "keep":
            log(f"keep {event.get('session_id', '?')}: {keep(event)}")
        else:
            print(f"codex_hook: unknown hook '{name}' (session-start, recall, keep)", file=sys.stderr)
            return 1
    except Exception:  # a hook must never break the session it serves
        log(f"{name} failed: {traceback.format_exc()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
