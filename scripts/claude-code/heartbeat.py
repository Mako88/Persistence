"""The heartbeat (docs/design/heartbeat.md, merged at 2524fe7, approved by Ember at 027382d): a script on a 10-minute
timer that compares facts and calls no model. It nudges, retries or defers only by adding a wake item; everything else
is a report to the journal.

Built in slices on arden/heartbeat-impl. This file has: the switch, the beat's three phases, reports (keys, lines, the
outbox, at-least-once delivery), check B (overdue wakes), check D (units), the hourly cap and the usage-limit reset
parser. Rows A and C and the ledger's derived states come in the next slices.

It reads only JSON records it or the harness wrote, the process table and systemd: never the room, a reply, a note, a
transcript, a fragment, a wake's reason or a task's text. Standard library only."""
import json
import os
import re
import sys
import threading
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import coordination  # noqa: E402

STATE = Path(os.environ.get("PERSISTENCE_STATE_DIR") or Path.home() / ".claude" / "persistence" / "state")
RUN_DUE_GRACE = 120  # hook.run_due's default grace for an item whose session may still have a live waiter
OVERDUE = 600  # B: still queued 10 min past `at` plus the grace
MARK_WAIT = 30  # phase 3 retakes heartbeat.lock blocking with this timeout
PER_HOUR = 3

# D: the fixed unit list, a literal per self, chosen by the effective user's name. Nothing here comes from a setting,
# an argument or systemctl's output; an unknown user checks nothing. Claude and Ember add their own tuple when they opt in.
UNITS = {
    "arden": ("persistence-wakes@arden.service", "persistence-room@arden.service", "persistence-remote@arden.service",
              "persistence-sleep@arden.service", "persistence-room-coverage@arden.service",
              "persistence-store-arden.service", "persistence-backup-arden.service"),
}

# Reports: closed sets. Claim conditions are claims' (task-claims.md section 4, b0b8383); the rest are this design's.
CLAIM_CONDITIONS = {"stalled after nudge": "stalled", "stalled after nudge, touched since": "stalled",
                    "unowned": "unowned", "liveness unknown": "liveness-unknown", "start failed": "start-failed",
                    "no valid cwd": "no-valid-cwd"}
WAKE_CONDITIONS = {"overdue": "overdue", "never started": "never-started", "crashed": "crashed", "failed": "failed",
                   "usage limit, deferrals used up": "usage-deferrals-used-up", "no valid cwd": "no-valid-cwd"}
WAKE_KINDS = set(WAKE_CONDITIONS.values())
CLAIM_KINDS = set(CLAIM_CONDITIONS.values())


def enabled(home=None):
    """PERSISTENCE_HEARTBEAT from this self's ~/.claude/settings.json env block only, never os.environ (as claims'
    switch): on when "true" or "1"."""
    return _setting(home, "PERSISTENCE_HEARTBEAT", "").strip().lower() in ("true", "1")


def per_hour(home=None):
    try:
        return max(0, int(_setting(home, "PERSISTENCE_HEARTBEAT_PER_HOUR", str(PER_HOUR))))
    except ValueError:
        return PER_HOUR


def _setting(home, name, default):
    try:
        env = json.loads((Path(home or Path.home()) / ".claude" / "settings.json").read_text(encoding="utf-8")).get("env") or {}
    except (OSError, ValueError, AttributeError):
        return default
    value = env.get(name) if isinstance(env, dict) else None
    return default if value is None else str(value)


def units_for(user):
    return UNITS.get(user, ())


# Keys and lines. A key is ids and a closed kind slug only; a line is the content plus " key=<key>" (sink framing).
def _uuid(value):
    if not isinstance(value, str) or str(uuid.UUID(value)) != value:
        raise ValueError(f"{value!r} is not a full id")
    return value


def hhmm(moment):
    return moment.astimezone(timezone.utc).strftime("%H:%MZ")


def wake_key(firing, kind):
    if kind not in WAKE_KINDS:
        raise ValueError(f"{kind!r} is not a wake report kind")
    return f"wake:{_uuid(firing)}:{kind}"


def claim_key(claim, spell, kind):
    if kind not in CLAIM_KINDS or not isinstance(spell, int) or spell < 0:
        raise ValueError(f"bad claim key parts {kind!r}, {spell!r}")
    return f"claim:{_uuid(claim)}:{spell}:{kind}"


UNIT_NAME = re.compile(r"^[a-z0-9-]+(@[a-z0-9-]+)?\.service$")
INVOCATION = re.compile(r"^[0-9a-f]*$")


def unit_key(unit, invocation):
    if not UNIT_NAME.match(unit) or not INVOCATION.match(invocation):
        raise ValueError(f"bad unit key parts {unit!r}, {invocation!r}")
    return f"unit:{unit}:{invocation}:failed"


def cap_key(window_start):
    return f"cap:{window_start.astimezone(timezone.utc).strftime('%Y-%m-%dT%H:%MZ')}"


def ledger_key(offset):
    return f"ledger:{int(offset)}:unreadable"


def claim_line(claim, condition, owner, at):
    if condition not in CLAIM_CONDITIONS:
        raise ValueError(f"{condition!r} is not a claims report condition")
    return f"heartbeat claim {_uuid(claim)}: {condition}, owner {_uuid(owner)[:8] if owner else 'unowned'}, at {hhmm(at)}"


def wake_line(firing, condition, owner, at):
    if condition not in WAKE_CONDITIONS:
        raise ValueError(f"{condition!r} is not a wake report condition")
    return f"heartbeat wake {_uuid(firing)}: {condition}, owner {_uuid(owner)[:8] if owner else 'unowned'}, at {hhmm(at)}"


# Usage limit (heartbeat.md "Usage limit"): keyed on is_error and the api error, never on subtype. The reset is read with
# one anchored pattern from `result`; nothing else in `result` is used.
RESET = re.compile(r"resets (1[0-2]|[1-9])(?::([0-5][0-9]))?(am|pm) \(([A-Za-z]+(?:/[A-Za-z_+-]+){0,2})\)")
FALLBACK = timedelta(hours=2)


def usage_limited(output):
    if not isinstance(output, dict) or output.get("is_error") is not True:
        return False
    return output.get("api_error_status") in (429, "429") or "usage_limit_reached" in (output.get("error"),
                                                                                      output.get("api_error_type"))


def reset_time(result, finished):
    """The next occurrence of the reset's local time after `finished`, within 24 h; anything else: finished + 2 h."""
    from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

    m = RESET.search(result or "")
    if not m:
        return finished + FALLBACK
    try:
        zone = ZoneInfo(m.group(4))
    except (ZoneInfoNotFoundError, ValueError):
        return finished + FALLBACK
    hour = int(m.group(1)) % 12 + (12 if m.group(3) == "pm" else 0)
    local = finished.astimezone(zone)
    for days in (0, 1):
        day = (local + timedelta(days=days)).date()
        candidate = datetime(day.year, day.month, day.day, hour, int(m.group(2) or 0), tzinfo=zone)
        if finished < candidate <= finished + timedelta(hours=24):
            return candidate.astimezone(timezone.utc)
    return finished + FALLBACK


# Reading what the heartbeat compares.
def read_queue(state):
    """wakes.json, read plainly (no lock): B only reads it. Missing or unreadable is empty."""
    try:
        items = json.loads((Path(state).parent / "wakes.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return []
    return [w for w in items if isinstance(w, dict)] if isinstance(items, list) else []


def systemctl_run(argv):
    import subprocess

    r = subprocess.run(argv, capture_output=True, text=True, timeout=20)
    return r.returncode, r.stdout


class Beat:
    """One beat's phase 1, holding heartbeat.lock (taken without waiting). Decisions go into the outbox; `take_cap`
    counts heartbeat wakes against the hourly cap. Leaving the block writes heartbeat.json and releases the lock."""

    def __init__(self, state, home, now, sink, systemctl, user):
        self.state, self.home, self.clock, self.sink, self.systemctl, self.user = Path(state), home, now, sink, systemctl, user
        self.now = now()
        self._lock = None

    def __enter__(self):
        self._lock = coordination.lock(self.state, "heartbeat", wait=0)
        self._lock.__enter__()
        try:
            self.data = load(self.state)
        except BaseException:
            self._lock.__exit__(*sys.exc_info())
            raise
        return self

    def __exit__(self, *exc):
        try:
            if exc[0] is None:
                save(self.state, self.data)
        finally:
            self._lock.__exit__(*exc)
        return False

    def report(self, key, line):
        """Into the outbox, unless the key is already there or already marked. The line is fixed now, at decision."""
        if key in self.data["reported"] or key in self.data["outbox"]:
            return False
        self.data["outbox"][key] = f"{line} key={key}"
        return True

    def take_cap(self):
        """One heartbeat wake against the hourly cap; False (and one `cap: reached` report per window) when full."""
        cap = self.data["cap"]
        start = _time(cap.get("window")) if cap.get("window") else None
        if start is None or self.now - start >= timedelta(hours=1):
            start = self.now.replace(second=0, microsecond=0)
            cap.update(window=start.isoformat(), count=0)
        if cap["count"] < per_hour(self.home):
            cap["count"] += 1
            return True
        self.report(cap_key(start), f"heartbeat cap: reached, at {hhmm(self.now)}")
        return False

    def check_overdue(self):
        """B: a wake still queued 10 min past its `at` plus run-due's grace (none for a room's work item)."""
        for w in read_queue(self.state):
            if not isinstance(w.get("at"), (int, float)) or not w.get("id"):
                continue
            late = w["at"] + (0 if w.get("work") else RUN_DUE_GRACE) + OVERDUE
            if self.now.timestamp() > late:
                try:
                    owner = w.get("session") if w.get("session") else None
                    self.report(wake_key(w["id"], "overdue"), wake_line(w["id"], "overdue", owner, self.now))
                except ValueError:
                    continue  # an item whose id or session isn't a full id: nothing structural to name it by

    def check_units(self):
        """D: `systemctl is-failed` and `show -p InvocationID` on the fixed list only; never start, restart, stop, reset."""
        for unit in units_for(self.user):
            code, out = self.systemctl(["systemctl", "is-failed", "--", unit])
            if out.strip() != "failed":
                continue
            _, shown = self.systemctl(["systemctl", "show", "-p", "InvocationID", "--", unit])
            invocation = shown.strip().partition("=")[2].strip()
            if not INVOCATION.match(invocation):
                invocation = ""
            self.report(unit_key(unit, invocation), f"heartbeat unit {unit}: failed, invocation {invocation}, at {hhmm(self.now)}")


def _time(value):
    try:
        moment = datetime.fromisoformat(value)
    except (TypeError, ValueError):
        return None
    return moment if moment.tzinfo else None


EMPTY = {"reported": {}, "outbox": {}, "crashed_seen": {}, "cap": {}}


def load(state):
    try:
        data = json.loads((Path(state) / "heartbeat.json").read_text(encoding="utf-8"))
    except FileNotFoundError:
        data = {}
    except (OSError, ValueError):
        data = {}  # unreadable: start clean; the worst case is a repeated report, never a lost wake
    return {k: dict(data.get(k) or {}) if isinstance(data.get(k) or {}, dict) else {} for k in EMPTY}


def save(state, data):
    path = Path(state) / "heartbeat.json"
    tmp = path.with_name(f".heartbeat.json.{os.getpid()}.{threading.get_ident()}.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(json.dumps(data, indent=1, sort_keys=True))
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)


def journal(line):
    print(line, flush=True)


def emit(state, sink):
    """Phase 2, holding no lock: every outbox line, including ones an earlier beat left. Returns the confirmed keys."""
    confirmed = []
    for key, line in sorted(load(state)["outbox"].items()):
        try:
            sink(line)
        except OSError:
            break  # the journal refused: nothing after this is marked; the next beat emits again
        confirmed.append(key)
    return confirmed


def mark(state, keys, now):
    """Phase 3: retake heartbeat.lock (30 s), mark each confirmed key and drop it from the outbox. Claim marks come
    with row A in a later slice."""
    if not keys:
        return
    try:
        with coordination.lock(state, "heartbeat", wait=MARK_WAIT):
            data = load(state)
            for key in keys:
                if key in data["outbox"]:
                    data["reported"][key] = now.isoformat(timespec="seconds")
                    del data["outbox"][key]
            save(state, data)
    except coordination.LockBusy:
        return  # nothing marked; the next beat emits again


def beat(state=None, home=None, now=None, sink=journal, systemctl=systemctl_run, user=None):
    """One beat. Returns "off", "busy" (another beat holds heartbeat.lock) or "ok"."""
    if not enabled(home):
        return "off"
    state = Path(state or STATE)
    now = now or (lambda: datetime.now(timezone.utc))
    if user is None:
        import pwd

        user = pwd.getpwuid(os.geteuid()).pw_name
    try:
        with Beat(state, home, now, sink, systemctl, user) as b:
            b.check_overdue()
            b.check_units()
    except coordination.LockBusy:
        return "busy"
    mark(state, emit(state, sink), now())
    return "ok"


def main(argv=None):
    return 0 if beat() in ("off", "ok", "busy") else 1


if __name__ == "__main__":
    sys.exit(main())
