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
    """The real shape (Claude's wake-b88e7b01): `api_error` is "usage_limit_reached" and `api_error_status` 429. Either
    alone is enough; there is no `error` or `api_error_type` field to read."""
    if not isinstance(output, dict) or output.get("is_error") is not True:
        return False
    return output.get("api_error_status") in (429, "429") or output.get("api_error") == "usage_limit_reached"


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

    def check_ledger(self):
        """The ledger, read whole under fired.lock. A partial tail (repaired by the read) means no ledger-based decision
        this beat; a bad line that isn't the tail is reported once and nothing is decided."""
        try:
            facts, complete = coordination.snapshot(self.state)
        except coordination.LedgerError as e:
            offset = getattr(e, "offset", 0)
            self.report(ledger_key(offset), f"heartbeat ledger: unreadable, at {hhmm(self.now)}")
            return
        if complete:
            self.ledger_decisions(facts)

    def outputs(self, facts):
        """Each finished firing's output, parsed, by its plain file name in state/; None when missing or unreadable."""
        out = {}
        for f in facts:
            name = f.get("output") if f.get("fact") == "finished" else None
            if isinstance(name, str) and name and "/" not in name and name not in out:
                try:
                    out[name] = json.loads((self.state / name).read_text(encoding="utf-8"))
                except (OSError, ValueError):
                    out[name] = None
        return out

    def ledger_decisions(self, facts):
        """Rows A and C from a complete snapshot. Recovery decisions (C, A's hand-off retry and nudge deferrals) run
        before A's report decisions, so a usage-refused nudge is deferred, not reported as a stall."""
        import claims

        self.facts = facts
        self.table = claims.process_table()
        self.outs = self.outputs(facts)
        self.states = firing_states(facts, self.now, self.table, wrapper_alive, self.data["crashed_seen"], self.outs)
        self.enq = {f["firing"]: f for f in facts if f.get("fact") == "enqueued" and isinstance(f.get("firing"), str)}
        self.claims_on = claims.enabled(self.home)
        self.check_fired()
        if self.claims_on:
            self.check_claims()

    # Shared by C and A: the recovery chain of an origin firing.
    def chain(self, origin):
        """Every attempt for an origin, queued or fired, once each: dicts with firing, attempt, deferral, follows. In
        the window where run-due wrote an attempt's enqueued fact and died before rewriting the queue, its id is in
        both; the fact wins (Ember's B3), so nothing is counted twice."""
        out = [f for f in self.enq.values() if f.get("retry_of") == origin]
        fired = {a["firing"] for a in out}
        out += [dict(w, firing=w["id"]) for w in read_queue(self.state)
                if w.get("retry_of") == origin and w.get("id") not in fired]
        return out

    def answered(self, firing):
        """An attempt is handled exactly when some item follows it or its report key exists."""
        if any(a.get("follows") == firing for chain in [self.enq.values(), read_queue(self.state)] for a in chain):
            return True
        return any(k.startswith(f"wake:{firing}:") for k in list(self.data["reported"]) + list(self.data["outbox"]))

    def busy(self, origin):
        """An attempt for this origin is queued, or a fired one is running."""
        if any(w.get("id") == origin or w.get("retry_of") == origin for w in read_queue(self.state)):
            return True
        return any(self.states.get(a.get("firing")) in ("running", None) and a.get("firing") in self.enq
                   for a in self.chain(origin))

    def finished_output(self, firing):
        f = next((x for x in self.facts if x.get("firing") == firing and x.get("fact") == "finished"), None)
        return (self.outs.get(f.get("output")) if f else None), (_time(f.get("at")) if f else None)

    def recover(self, firing, e, claim=None, cwd=None, nudge=False):
        """C's rule for one unanswered failed attempt: a usage limit is deferred (up to 3 per origin); anything else
        gets the origin's one ordinary retry, else is reported. A nudge has no ordinary retry. Returns the report
        condition when it reports, else None."""
        import claims

        origin = e.get("retry_of") or firing
        out, finished = self.finished_output(firing)
        chain = self.chain(origin)
        if usage_limited(out):
            used = sum(1 for a in chain if a.get("attempt") == "deferral")
            if used >= 3:
                return "usage limit, deferrals used up"
            at = reset_time(out.get("result"), finished or self.now) + timedelta(minutes=5)
            self.add(origin, firing, e, "usage-limit", "deferral", used + 1, at, claim, cwd)
            return None
        if nudge or any(a.get("attempt") == "retry" for a in chain):
            return {"never-started": "never started"}.get(self.states[firing], self.states[firing])
        self.add(origin, firing, e, self.states[firing], "retry", None, self.now, claim, cwd)
        return None

    def add(self, origin, follows, e, kind, attempt, deferral, at, claim, cwd):
        """One recovery item through add_heartbeat_wake, against the hourly cap; the same session, kept structurally."""
        import claims
        import hook

        session = e.get("session")
        cwd = cwd if cwd is not None else e.get("cwd")
        if claims.static_cwd_problem(cwd, os.path.realpath(str(self.home or Path.home()))):
            self.report_wake(follows, "no valid cwd", session)
            return
        if not self.take_cap():
            return
        launch = "resume" if transcript_exists(self.home, session) else "session-id"
        try:
            hook.add_heartbeat_wake(id=str(uuid.uuid4()), kind=kind, session=session, launch=launch, claim=claim,
                                    retry_of=origin, follows=follows, attempt=attempt, deferral=deferral,
                                    at=at.timestamp(), cwd=cwd)
        except ValueError as e:
            print(f"heartbeat: no recovery for firing {follows}: {e}", file=sys.stderr)

    def report_wake(self, firing, condition, owner):
        try:
            self.report(wake_key(firing, WAKE_CONDITIONS[condition]), wake_line(firing, condition, owner, self.now))
        except ValueError:
            pass

    def check_fired(self):
        """C: a fired wake that didn't finish well. With claims on, a firing whose enqueued fact has a claim is A's.
        A `work` firing A doesn't own is reported, never retried; with claims off that is every work firing, and the
        claim is never read or written."""
        import claims

        for firing, e in self.enq.items():
            state = self.states.get(firing)
            if state not in ("never-started", "crashed", "failed") or self.answered(firing):
                continue
            if self.claims_on and e.get("claim"):
                continue
            if e.get("work"):
                self.report_wake(firing, {"never-started": "never started"}.get(state, state), e.get("session"))
                continue
            origin = e.get("retry_of") or firing
            if claims.running(e.get("session"), self.table) is not False or self.busy(origin):
                continue
            condition = self.recover(firing, e)
            if condition:
                self.report_wake(firing, condition, e.get("session"))

    # A: claims section 4, row by row.
    def report_claim(self, record, spell, condition):
        key = claim_key(record["id"], spell, CLAIM_CONDITIONS[condition])
        self.report(key, claim_line(record["id"], condition, record["owner"], self.now))

    def check_claims(self):
        import claims

        c = claims.Claims(self.state, home=self.home, now=lambda: self.now)
        c.settle_pauses()
        items = read_queue(self.state)
        for record in c.all():
            spell = claims.spell_start(record)
            if record["status"] == "open":
                self.open_row(c, record, spell or 0)
            elif record["status"] == "claimed":
                self.claimed_row(c, record, spell or 0, items)

    def spawn_firing(self, record):
        """The firing that spawn-claimed this spell (by harness), if that's how the spell began."""
        start = claims_spell_entry(record)
        return start["firing"] if start and start["by"] == "harness" and start["action"] == "claim" else None

    def open_row(self, c, record, spell):
        if record["dispatch"] == "none":
            self.report_claim(record, spell, "unowned")
            return
        # open/handoff: its firing failed before the spawn claim.
        firing = next((f for f, e in self.enq.items() if e.get("claim") == record["id"] and not e.get("retry_of")), None)
        if not firing or self.states.get(firing) != "never-started":
            return
        latest = last_attempt(self.chain(firing))
        if latest is None and not self.answered(firing):
            if self.take_cap():
                import hook

                e = self.enq[firing]
                try:
                    hook.add_heartbeat_wake(id=str(uuid.uuid4()), kind="never-started", session=e["session"],
                                            launch="session-id", claim=record["id"], retry_of=firing, follows=firing,
                                            attempt="retry", at=self.now.timestamp(), cwd=record["cwd"])
                except ValueError as err:
                    print(f"heartbeat: no retry for claim {record['id']}: {err}", file=sys.stderr)
            return
        if latest is not None and self.states.get(latest.get("firing")) in ("never-started", "crashed", "failed"):
            self.report_claim(record, spell, "start failed")

    def claimed_row(self, c, record, spell, items):
        import claims
        import hook

        owner = record["owner"]
        f0 = self.spawn_firing(record)
        if f0 and any(k.startswith(f"wake:{f0}:") for k in self.data["reported"]):
            return  # C reported it while claims were off: no A action until a session's own transition (not retroactive)
        if self.table is None:
            self.report_claim(record, spell, "liveness unknown")
            return
        if claims.running(owner, self.table):
            return
        nudge = nudge_entry(record)
        # Reconcile both split-nudge halves by firing id (heartbeat.md, "Split writes in a nudge").
        if nudge is None:
            half = self.unmarked_nudge(record)
            if half:
                with coordination.lock(self.state, "wakes"):
                    c.mark_nudged(record["id"], half)
                return
        elif nudge["firing"] not in self.enq and not any(w.get("id") == nudge["firing"] for w in items) \
                and not any(f.get("firing") == nudge["firing"] and f.get("fact") == "taken" for f in self.facts):
            self.nudge(c, record, nudge["firing"], readd=True)
            return
        if claims.pending_for(record, items):
            return
        if nudge is None:
            self.nudge(c, record, str(uuid.uuid4()))
            return
        # The nudge fired. A usage refusal of it is deferred (recovery before the report decision).
        f = nudge["firing"]
        if self.states.get(f) in ("never-started", "crashed", "failed") and not self.answered(f):
            out, _ = self.finished_output(f)
            if usage_limited(out):
                if self.recover(f, self.enq.get(f, {"session": owner}), claim=record["id"], cwd=record["cwd"], nudge=True) is None:
                    return
        if record["reported"] or _time(record["nudged"]) is None or self.now - _time(record["nudged"]) < BEAT:
            return
        if self.states.get(f) in (None, "running") and f in self.enq:
            return
        if f0 and not transcript_exists(self.home, owner):
            self.report_claim(record, spell, "start failed")
            return
        touched = _time(record["updated"]) and _time(record["updated"]) > _time(record["nudged"])
        self.report_claim(record, spell, "stalled after nudge, touched since" if touched else "stalled after nudge")

    def unmarked_nudge(self, record):
        """A heartbeat nudge item for this claim that's queued, enqueued or taken in this spell, with no mark yet."""
        import hook

        start = _time(claims_spell_entry(record)["at"]) if claims_spell_entry(record) else None
        for w in read_queue(self.state):
            if w.get("by") == "heartbeat" and w.get("claim") == record["id"] and not w.get("retry_of"):
                return w["id"]
        for f in self.facts:
            ours = f.get("fact") == "taken" or (f.get("fact") == "enqueued" and f.get("by") == "heartbeat"
                                                 and not f.get("retry_of"))
            if f.get("claim") == record["id"] and ours:
                if start is None or (_time(f.get("at")) or start) >= start:
                    return f["firing"]
        return None

    def nudge(self, c, record, firing, readd=False):
        """Nudge once: the item first (minted firing id), then the mark, under wakes.lock -> tasks/.lock. A claim with
        no valid cwd is reported instead. A re-add (the mark without its item) writes only the item, same id."""
        import claims
        import hook

        spell = claims.spell_start(record) or 0
        if claims.static_cwd_problem(record["cwd"], c.home_real):
            self.report_claim(record, spell, "no valid cwd")
            return
        if not readd and not self.take_cap():
            return
        f0 = self.spawn_firing(record)
        launch = "resume" if transcript_exists(self.home, record["owner"]) else "session-id"
        with coordination.lock(self.state, "wakes"):
            try:
                hook.add_heartbeat_wake(id=firing, kind="nudge", session=record["owner"], launch=launch,
                                        claim=record["id"], at=self.now.timestamp(), cwd=record["cwd"])
            except ValueError as e:
                print(f"heartbeat: no nudge for claim {record['id']}: {e}", file=sys.stderr)
                return
            if not readd:
                c.mark_nudged(record["id"], firing)

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


START_GRACE = timedelta(minutes=5)
BEAT = timedelta(minutes=10)


def wrapper_alive(pid, start):
    """The run line's pid is alive with the same /proc start time (a recycled pid has another)."""
    import run_line

    return pid is not None and start is not None and run_line.start_time(pid) == start


def firing_states(facts, now, table, alive, seen, outputs):
    """Per firing id, its derived state (heartbeat.md, "States"), or None while no decision can be made yet: running,
    never-started (enqueued, no start, past the start grace), crashed (started, nothing alive, for a full beat after
    first seen so), failed (finished with is_error, a missing or unparseable output, or a non-zero exit), ok, taken.
    `seen` is heartbeat.json's crashed_seen, updated here; `outputs` maps an output file name to its parsed JSON or None."""
    import claims

    by = {}
    for f in facts:
        if isinstance(f.get("firing"), str):
            by.setdefault(f["firing"], {}).setdefault(f.get("fact"), f)
    states = {}
    for firing, got in by.items():
        state = None
        if "taken" in got:
            state = "taken"
        elif "finished" in got:
            out = outputs.get(got["finished"].get("output"))
            bad = got["finished"].get("exit") != 0 or not isinstance(out, dict) or out.get("is_error") is not False
            state = "failed" if bad else "ok"
        elif "started" in got:
            s = got["started"]
            live = claims.running(s.get("session"), table) if claims.full_id(s.get("session")) else False
            if live or live is None or alive(s.get("pid"), s.get("start_time")):
                seen.pop(firing, None)
                state = "running" if live or alive(s.get("pid"), s.get("start_time")) else None
            else:
                first = _time(seen.get(firing))
                if first is None:
                    seen[firing] = now.isoformat(timespec="seconds")
                elif now - first >= BEAT:
                    state = "crashed"
        elif "enqueued" in got:
            at = _time(got["enqueued"].get("at"))
            if at is not None and now - at >= START_GRACE:
                state = "never-started"
        states[firing] = state
    return states


def last_attempt(attempts):
    """The attempt no other attempt in the chain follows: chosen by structure, never by comparing `at`s, since an
    enqueued fact's is ISO text and a queued item's a number (Ember's B3). None for an empty chain."""
    followed = {a.get("follows") for a in attempts}
    ends = [a for a in attempts if a.get("firing") not in followed]
    return ends[-1] if ends else None


def transcript_exists(home, session):
    """A stat of the session's transcript path, never opened."""
    return bool(session) and any((Path(home or Path.home()) / ".claude" / "projects").glob(f"*/{session}.jsonl"))


def claims_spell_entry(record):
    import claims

    i = claims.spell_start(record)
    return record["history"][i] if i is not None else None


def nudge_entry(record):
    """This spell's nudge mark, if any."""
    import claims

    start = claims.spell_start(record)
    return next((h for h in reversed(record["history"][start or 0:]) if h["action"] == "nudge"), None)


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


def mark(state, keys, now, home=None):
    """Phase 3: retake heartbeat.lock (30 s), mark each confirmed key and drop it from the outbox. A claimed claim's key
    sets its `reported` through the claims module (under tasks/.lock) only while the claim is still in the key's
    spell; otherwise the entry is just dropped."""
    if not keys:
        return
    try:
        with coordination.lock(state, "heartbeat", wait=MARK_WAIT):
            data = load(state)
            for key in keys:
                if key not in data["outbox"]:
                    continue
                del data["outbox"][key]
                if key.startswith("claim:"):
                    import claims

                    _, claim, spell, _kind = key.split(":")
                    c = claims.Claims(state, home=home, now=lambda: now)
                    try:
                        record = c.get(claim)
                    except claims.Refused:
                        continue
                    if record["status"] == "claimed":
                        if not c.mark_reported(claim, int(spell)):
                            continue  # the spell ended between emission and marking: dropped
                    elif record["status"] != "open":
                        continue
                data["reported"][key] = now.isoformat(timespec="seconds")
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
            b.check_ledger()
            b.check_units()
    except coordination.LockBusy:
        return "busy"
    mark(state, emit(state, sink), now(), home=home)
    return "ok"


def main(argv=None):
    return 0 if beat() in ("off", "ok", "busy") else 1


if __name__ == "__main__":
    sys.exit(main())
