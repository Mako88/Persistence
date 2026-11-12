"""The run line (docs/design/heartbeat.md, "The ledger of fired wakes"): `run_line.py <program> <args...>` runs one
launch of a fired wake and puts its start and end on the ledger. First, for each firing id in PERSISTENCE_FIRINGS, a
`started` fact (this wrapper's pid and its /proc start time, and the session's full id) under fired.lock, released
before the launch. Then the program, with this process's stdin, stdout and stderr. Then a `finished` fact per firing
(the exit status and the output file's name), whatever the exit. Without firing ids it only runs the program.

Every value it writes is an id, a number or a file name; it reads no text. Run with -I: it imports only the standard
library and coordination.py from its own directory."""
import os
import subprocess
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import coordination  # noqa: E402


def _full(value):
    try:
        return value if value and str(uuid.UUID(value)) == value else None
    except ValueError:
        return None


def start_time(pid):
    """Field 22 of /proc/<pid>/stat (clock ticks since boot), or None: a recycled pid has another."""
    try:
        return int(Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[19])
    except (OSError, ValueError, IndexError):
        return None


def _now():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def facts_from(environ):
    """(state, firings, session, output) from the environment, or None when anything isn't an id or a plain name."""
    firings = [_full(f) for f in (environ.get("PERSISTENCE_FIRINGS") or "").split(",") if f]
    session = _full(environ.get("PERSISTENCE_FIRING_SESSION"))
    state = environ.get("PERSISTENCE_FIRING_STATE")
    output = environ.get("PERSISTENCE_FIRING_OUTPUT") or ""
    if not firings or None in firings or not session or not state or not os.path.isabs(state) or "/" in output:
        return None
    return Path(state), firings, session, output


def main(argv, environ=os.environ):
    if not argv:
        print("run_line: nothing to run", file=sys.stderr)
        return 2
    ledger = facts_from(environ)
    env = {k: v for k, v in environ.items() if not k.startswith("PERSISTENCE_FIRING")}
    if ledger:
        state, firings, session, output = ledger
        me = os.getpid()
        for firing in firings:
            coordination.append(state, {"firing": firing, "fact": "started", "at": _now(), "pid": me,
                                        "start_time": start_time(me), "session": session})
    try:
        code = subprocess.call(argv, env=env)
    except OSError as e:
        print(f"run_line: {e}", file=sys.stderr)
        code = 127
    if ledger:
        for firing in firings:
            coordination.append(state, {"firing": firing, "fact": "finished", "at": _now(), "exit": code, "output": output})
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
