"""The heartbeat, slice 1 (docs/design/heartbeat.md on main 2524fe7): the switch, reports (keys, lines, the outbox and
the three phases, at-least-once delivery), check B (overdue wakes), check D (units: a fixed list, observe only), the
hourly cap and the usage-limit reset parser. Rows A and C, the ledger's derived states and the cross-document scenarios
are later slices.

Every test uses a fake clock, a temporary state directory, a fake sink and a fake systemctl: no test calls a model."""
import json
import os
import re
import sys
import tempfile
import unittest
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import coordination  # noqa: E402
import heartbeat  # noqa: E402

T0 = datetime(2026, 11, 11, 22, 0, tzinfo=timezone.utc)
ARDEN_UNITS = ("persistence-wakes@arden.service", "persistence-room@arden.service", "persistence-remote@arden.service",
               "persistence-sleep@arden.service", "persistence-room-coverage@arden.service",
               "persistence-store-arden.service", "persistence-backup-arden.service")
CLAUDE_UNITS = ("persistence-wakes@ubuntu.service", "persistence-room@ubuntu.service", "persistence-remote@ubuntu.service",
                "persistence-sleep@ubuntu.service", "persistence-room-coverage@ubuntu.service",
                "persistence-daily@ubuntu.service", "persistence-store.service", "persistence-backup.service",
                "persistence-embedder.service", "persistence-mirror.service")


def sid():
    return str(uuid.uuid4())


class Sink:
    """The journal, faked: records lines; can refuse; asserts no heartbeat lock is held while it writes."""

    def __init__(self, refuse=False):
        self.lines, self.refuse = [], refuse

    def __call__(self, line):
        assert coordination.held_locks() == [], f"emitted while holding {coordination.held_locks()}"
        if self.refuse:
            raise OSError("journal refused")
        self.lines.append(line)


class Systemctl:
    """A fake systemctl: `failed` maps unit -> invocation id. Records every argv; only is-failed and show answer."""

    def __init__(self, failed=None):
        self.failed, self.calls = dict(failed or {}), []

    def __call__(self, argv):
        self.calls.append(list(argv))
        assert argv[0] == "systemctl" and argv[-2] == "--", argv  # the unit is always after "--"
        verb, unit = argv[1], argv[-1]
        if verb == "is-failed" and len(argv) == 4:
            return (0, "failed\n") if unit in self.failed else (1, "active\n")
        if verb == "show" and argv[2:4] == ["-p", "InvocationID"]:
            return 0, f"InvocationID={self.failed.get(unit, '')}\n"
        raise AssertionError(f"systemctl {verb} must never run")


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.home = self.tmp / "home"
        (self.home / ".claude").mkdir(parents=True)
        self.state = self.home / ".claude" / "persistence" / "state"
        self.state.mkdir(parents=True)
        self.wakes = self.state.parent / "wakes.json"
        self.now = T0
        self.sink = Sink()
        self.systemctl = Systemctl()
        self.settings({"PERSISTENCE_HEARTBEAT": "true"})

    def settings(self, env):
        (self.home / ".claude" / "settings.json").write_text(json.dumps({"env": env}), encoding="utf-8")

    def beat(self, user="nobody", **kw):
        return heartbeat.beat(state=self.state, home=self.home, now=lambda: self.now, sink=self.sink,
                              systemctl=self.systemctl, user=user, **kw)

    def queue(self, items):
        self.wakes.write_text(json.dumps(items), encoding="utf-8")

    def hb(self):
        return json.loads((self.state / "heartbeat.json").read_text(encoding="utf-8"))


class TheSwitch(Base):
    def test_off_exits_before_reading_any_state(self):
        self.settings({})
        self.queue([{"id": sid(), "session": sid(), "at": (T0 - timedelta(hours=1)).timestamp(), "reason": "x"}])
        with mock.patch.object(heartbeat, "read_queue", side_effect=AssertionError("read state while off")):
            self.assertEqual(self.beat(), "off")
        self.assertFalse((self.state / "heartbeat.json").exists())
        self.assertFalse((self.state / "heartbeat.lock").exists())
        self.assertEqual(self.sink.lines, [])

    def test_environment_alone_does_not_turn_it_on(self):
        self.settings({})
        with mock.patch.dict(os.environ, {"PERSISTENCE_HEARTBEAT": "true"}):
            self.assertEqual(self.beat(), "off")

    def test_on_by_true_or_1_only(self):
        for value, on in (("true", True), ("1", True), ("yes", False), ("false", False), ("", False)):
            self.settings({"PERSISTENCE_HEARTBEAT": value})
            self.assertEqual(heartbeat.enabled(self.home), on, value)

    def test_quiet_state_costs_nothing(self):
        self.assertEqual(self.beat(), "ok")
        self.assertEqual(self.sink.lines, [])
        self.assertFalse(self.wakes.exists())
        self.assertEqual(self.hb()["outbox"], {})


class OverdueWake(Base):
    def item(self, minutes_late):
        return {"id": sid(), "session": sid(), "reason": "SECRET-REASON",
                "at": (T0 - timedelta(minutes=minutes_late)).timestamp()}

    def test_overdue_past_grace_and_ten_minutes_is_reported_once(self):
        late, fresh = self.item(13), self.item(11)  # grace 2 min + 10 min: 13 is over, 11 is not
        self.queue([late, fresh])
        self.beat()
        self.assertEqual(self.sink.lines, [f"heartbeat wake {late['id']}: overdue, owner {late['session'][:8]}, at 22:00Z "
                                           f"key=wake:{late['id']}:overdue"])
        self.now = T0 + timedelta(minutes=10)
        self.beat()
        self.assertEqual(len(self.sink.lines), 2)  # `fresh` is overdue now; `late` is never emitted again
        self.assertIn(f"key=wake:{fresh['id']}:overdue", self.sink.lines[1])
        self.assertNotIn("SECRET-REASON", "".join(self.sink.lines))

    def test_work_item_has_no_grace(self):
        w = self.item(11)
        w["work"] = True
        self.queue([w])
        self.beat()
        self.assertEqual(len(self.sink.lines), 1)

    def test_pid_items_are_not_overdue(self):
        self.queue([{"id": sid(), "session": sid(), "pid": 1, "reason": "x"}])
        self.beat()
        self.assertEqual(self.sink.lines, [])


class Units(Base):
    def test_arden_list_is_exactly_the_seven(self):
        self.assertEqual(heartbeat.units_for("arden"), ARDEN_UNITS)

    def test_claude_list_is_exactly_the_ten(self):
        self.assertEqual(heartbeat.units_for("ubuntu"), CLAUDE_UNITS)

    def test_claude_unit_failure_reported_under_ubuntu_only(self):
        self.systemctl.failed = {"persistence-store.service": "ccc333"}
        self.beat(user="ubuntu")
        self.assertEqual(self.sink.lines, ["heartbeat unit persistence-store.service: failed, invocation ccc333, "
                                           "at 22:00Z key=unit:persistence-store.service:ccc333:failed"])
        self.beat(user="arden")
        self.assertEqual(len(self.sink.lines), 1)  # Arden's beat never checks Claude's units

    def test_every_name_is_fully_instantiated(self):
        for user, units in heartbeat.UNITS.items():
            for unit in units:
                self.assertRegex(unit, r"^[a-z0-9-]+(@[a-z0-9-]+)?\.service$", (user, unit))

    def test_unknown_user_checks_nothing(self):
        self.beat(user="mallory")
        self.assertEqual(self.systemctl.calls, [])

    def test_failed_unit_reported_once_per_invocation_never_restarted(self):
        self.systemctl.failed = {"persistence-room@arden.service": "aaa111"}
        self.beat(user="arden")
        self.beat(user="arden")
        self.assertEqual(self.sink.lines, ["heartbeat unit persistence-room@arden.service: failed, invocation aaa111, "
                                           "at 22:00Z key=unit:persistence-room@arden.service:aaa111:failed"])
        self.systemctl.failed = {"persistence-room@arden.service": "bbb222"}
        self.beat(user="arden")
        self.assertEqual(len(self.sink.lines), 2)
        verbs = {c[1] for c in self.systemctl.calls}
        self.assertLessEqual(verbs, {"is-failed", "show"})
        for c in self.systemctl.calls:
            self.assertEqual(c[-2], "--")  # argv list, no shell, a unit name can never be read as an option


class AtLeastOnce(Base):
    def setUp(self):
        super().setUp()
        self.w = {"id": sid(), "session": sid(), "reason": "x", "at": (T0 - timedelta(hours=1)).timestamp()}
        self.queue([self.w])

    def test_crash_between_emit_and_mark_repeats_the_identical_line_once(self):
        with mock.patch.object(heartbeat, "mark", side_effect=SystemExit("killed after phase 2")):
            with self.assertRaises(SystemExit):
                self.beat()
        self.now = T0 + timedelta(minutes=10)  # the repeat carries the decision time, not this beat's
        self.beat()
        self.beat()
        self.assertEqual(len(self.sink.lines), 2)
        self.assertEqual(self.sink.lines[0], self.sink.lines[1])
        self.assertEqual(self.hb()["outbox"], {})

    def test_refusing_sink_marks_nothing_and_the_next_beat_tries_again(self):
        self.sink.refuse = True
        self.beat()
        self.assertIn(f"wake:{self.w['id']}:overdue", self.hb()["outbox"])
        self.assertNotIn(f"wake:{self.w['id']}:overdue", self.hb()["reported"])
        self.sink.refuse = False
        self.beat()
        self.assertEqual(len(self.sink.lines), 1)
        self.assertIn(f"wake:{self.w['id']}:overdue", self.hb()["reported"])

    def test_phase_3_that_cannot_retake_the_lock_marks_nothing(self):
        import threading

        held, done = threading.Event(), threading.Event()

        def hold():  # another holder (its own thread, so its own lock bookkeeping)
            with coordination.lock(self.state, "heartbeat"):
                held.set()
                done.wait(5)

        t = threading.Thread(target=hold)
        t.start()
        held.wait(5)
        try:
            self.assertEqual(self.beat(), "busy")  # a beat that can't take heartbeat.lock exits without writing
            with mock.patch.object(heartbeat, "MARK_WAIT", 0.05):
                heartbeat.mark(self.state, [f"wake:{self.w['id']}:overdue"], self.now)  # phase 3 gives up quietly
        finally:
            done.set()
            t.join()
        self.assertFalse((self.state / "heartbeat.json").exists())
        self.assertEqual(self.sink.lines, [])

    def test_emission_holds_no_lock(self):
        self.beat()  # Sink asserts held_locks() == [] on every line
        self.assertEqual(len(self.sink.lines), 1)


class Keys(unittest.TestCase):
    U = "0b6a4c55-3c1e-4f3c-9a43-6a8f0b3d1c2e"
    GRAMMAR = re.compile(r"^(wake:[0-9a-f-]{36}:(overdue|never-started|crashed|failed|usage-deferrals-used-up|no-valid-cwd)"
                         r"|claim:[0-9a-f-]{36}:\d+:(stalled|unowned|liveness-unknown|start-failed|no-valid-cwd)"
                         r"|unit:[a-z0-9@.-]+:[0-9a-f]*:failed|cap:\d{4}-\d\d-\d\dT\d\d:\d\dZ|ledger:\d+:unreadable)$")

    def test_every_key_matches_its_grammar(self):
        for key in (heartbeat.wake_key(self.U, "overdue"), heartbeat.claim_key(self.U, 3, "stalled"),
                    heartbeat.unit_key("persistence-room@arden.service", "abc123"), heartbeat.cap_key(T0),
                    heartbeat.ledger_key(120)):
            self.assertRegex(key, self.GRAMMAR)

    def test_closed_sets_refuse_anything_else(self):
        with self.assertRaises(ValueError):
            heartbeat.wake_key(self.U, "it broke because of SECRET")
        with self.assertRaises(ValueError):
            heartbeat.claim_line(self.U, "made up", self.U, T0)
        with self.assertRaises(ValueError):
            heartbeat.wake_key("not-a-uuid", "overdue")

    def test_claim_line_is_claims_fixed_shape(self):
        self.assertEqual(heartbeat.claim_line(self.U, "stalled after nudge, touched since", self.U, T0),
                         f"heartbeat claim {self.U}: stalled after nudge, touched since, owner {self.U[:8]}, at 22:00Z")
        self.assertEqual(heartbeat.claim_line(self.U, "unowned", None, T0),
                         f"heartbeat claim {self.U}: unowned, owner unowned, at 22:00Z")


class Cap(Base):
    def test_fourth_heartbeat_wake_in_an_hour_is_refused_and_reported_once(self):
        with heartbeat.Beat(self.state, self.home, lambda: self.now, self.sink, self.systemctl, "nobody") as b:
            self.assertEqual([b.take_cap() for _ in range(5)], [True, True, True, False, False])
        self.assertEqual(self.hb()["outbox"], {"cap:2026-11-11T22:00Z": "heartbeat cap: reached, at 22:00Z key=cap:2026-11-11T22:00Z"})
        self.now = T0 + timedelta(minutes=61)
        with heartbeat.Beat(self.state, self.home, lambda: self.now, self.sink, self.systemctl, "nobody") as b:
            self.assertTrue(b.take_cap())

    def test_an_unreadable_cap_is_never_reset_or_counted_on(self):
        """Even past load() (Ember's B2): take_cap fails closed on a cap it can't read, rather than starting a new window
        or counting from a bad count."""
        aware = "2026-11-11T22:30:00+00:00"
        for cap in ({"window": "garbage", "count": 3}, {"window": None, "count": 3}, {"window": "2026-11-11T22:30:00", "count": 3},
                    {"count": 3}, {"window": aware}, {"window": aware, "count": True}, {"window": aware, "count": -5},
                    {"window": aware, "count": "1"}):
            with self.subTest(cap=cap):
                with self.assertRaises(heartbeat.StateUnreadable):
                    with heartbeat.Beat(self.state, self.home, lambda: self.now, self.sink, self.systemctl, "nobody") as b:
                        b.data["cap"] = dict(cap)
                        b.take_cap()
                self.assertFalse((self.state / "heartbeat.json").exists())

    def test_cap_from_settings(self):
        self.settings({"PERSISTENCE_HEARTBEAT": "true", "PERSISTENCE_HEARTBEAT_PER_HOUR": "1"})
        with heartbeat.Beat(self.state, self.home, lambda: self.now, self.sink, self.systemctl, "nobody") as b:
            self.assertEqual([b.take_cap(), b.take_cap()], [True, False])


class UsageLimit(unittest.TestCase):
    """Pinned to the real shape (Claude's wake-b88e7b01, quoted in review 1570218788572635177): is_error true, subtype
    "success", api_error_status 429 (a number), api_error "usage_limit_reached", api_error_origin, terminal_reason,
    the reset only in `result`, and no `error` or `api_error_type` field."""
    FIN = datetime(2026, 11, 11, 16, 36, 53, tzinfo=timezone.utc)  # 11:36:53 in New York
    SAMPLE = {"type": "result", "subtype": "success", "is_error": True, "api_error_status": 429,
              "api_error": "usage_limit_reached", "api_error_origin": "first_party", "terminal_reason": "api_error",
              "result": "You've hit your 5-hour limit \u00b7 resets 2pm (America/New_York)"}

    def test_sample_is_a_usage_limit_whatever_its_subtype(self):
        self.assertTrue(heartbeat.usage_limited(self.SAMPLE))
        self.assertTrue(heartbeat.usage_limited({**self.SAMPLE, "subtype": "error_during_execution"}))
        self.assertFalse(heartbeat.usage_limited({**self.SAMPLE, "is_error": False}))
        self.assertFalse(heartbeat.usage_limited({"is_error": True, "result": "resets 2pm (America/New_York)"}))

    def test_the_api_error_alone_is_a_usage_limit(self):
        without_status = {k: v for k, v in self.SAMPLE.items() if k != "api_error_status"}
        self.assertTrue(heartbeat.usage_limited(without_status))
        self.assertFalse(heartbeat.usage_limited({**without_status, "api_error": "overloaded"}))
        self.assertNotIn("error", self.SAMPLE)
        self.assertNotIn("api_error_type", self.SAMPLE)

    def test_reset_is_the_next_2pm_new_york_after_the_finish(self):
        self.assertEqual(heartbeat.reset_time(self.SAMPLE["result"], self.FIN), datetime(2026, 11, 11, 19, 0, tzinfo=timezone.utc))
        late = datetime(2026, 11, 11, 20, 0, tzinfo=timezone.utc)  # 3pm New York: the next 2pm is tomorrow's
        self.assertEqual(heartbeat.reset_time(self.SAMPLE["result"], late), datetime(2026, 11, 12, 19, 0, tzinfo=timezone.utc))

    def test_minutes_are_allowed(self):
        self.assertEqual(heartbeat.reset_time("resets 2:30pm (America/New_York)", self.FIN),
                         datetime(2026, 11, 11, 19, 30, tzinfo=timezone.utc))

    def test_malformed_or_unknown_zone_takes_two_hours(self):
        for text in ("resets soon", "resets 14pm (America/New_York)", "resets 2pm (Mars/Olympus)", "resets 2pm",
                     "resets 2pm (../../etc/passwd)", ""):
            self.assertEqual(heartbeat.reset_time(text, self.FIN), self.FIN + timedelta(hours=2), text)


if __name__ == "__main__":
    unittest.main()
