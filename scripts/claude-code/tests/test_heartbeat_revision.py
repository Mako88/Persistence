"""PR #28's revision after a83ef07: Ember's CHANGES (room 1570857894172168193) and Claude's review (1570858021154848909,
1570858022857875550). Each class names the point it closes. Written before the fixes; each was seen failing on a83ef07.

Fake clock, temporary state, a fake process table; nothing is launched (run-due is simulated, or faked as in
test_heartbeat_ledger)."""
import contextlib
import io
import json
import os
import sys
import threading
import unittest
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude
sys.path.insert(0, str(Path(__file__).resolve().parent))
import test_heartbeat_decisions as D  # noqa: E402
import test_heartbeat_ledger as L  # noqa: E402
import test_heartbeat_stalled as S  # noqa: E402
from test_heartbeat_stalled import T0, USAGE, at, claims, coordination, heartbeat, hook, observed, sid  # noqa: E402

UNKNOWN = {**USAGE, "result": "You've hit your limit"}  # a usage limit with no reset time in it
MAIN_ENQUEUED = ("session", "cwd", "item_at", "work", "by", "claim", "launch", "retry_of", "follows", "attempt",
                 "deferral", "reason_sha256")  # coordination.ENQUEUED_FIELDS on main 01755e3


class Base(S.Base):
    def fire_hb(self, w, body, ack=True):
        """One queued heartbeat item fired by run-due, started, its block acked (or not), finished with `body`."""
        f = w["id"]
        coordination.append(self.state, coordination.enqueued_fact(w, self.now.isoformat(), stalled=True))
        hook.wakes(lambda items: [x for x in items if x.get("id") != f])
        self.fact(f, "started", self.now, pid=999999, start_time=1, session=w["session"])
        if ack:
            self.ack(w["session"])
        self.finish(f, body)

    def finish(self, f, body):
        out = f"wake-{f}.json"
        (self.state / out).write_text(json.dumps(body), encoding="utf-8")
        self.fact(f, "finished", self.now, exit=0, output=out)

    def unread(self, *keys, when=None):
        data = self.hb()
        for k in keys:
            data["reported"][k] = (when or self.now - timedelta(hours=2)).isoformat()
        heartbeat.save(self.state, data)

    def status(self):
        out = []
        code = heartbeat.command(["status"], state=self.state, now=self.now, out=out.append, home=self.home)
        self.assertEqual(code, 0, out)
        return out


# ------------------------------------------------------------------------------- Ember 1, G1 and G2: feature off
class FeatureOffCap(S.Base):
    def beats(self, stalled):
        self.settings(PERSISTENCE_HEARTBEAT_PER_HOUR="0", PERSISTENCE_HEARTBEAT_STALLED="true" if stalled else "")
        windows, keys = [], set()
        for i in range(4):
            self.now = at(12, 10 * i)
            with heartbeat.Beat(self.state, self.home, lambda: self.now, print, None, "arden") as b:
                self.assertFalse(b.take_cap())
            data = self.hb()
            windows.append(data["cap"]["window"])
            keys |= {k for k in data["outbox"] if k.startswith("cap:")}
        return windows, keys, self.hb()

    def test_per_hour_zero_keeps_one_window_an_hour_as_main_did(self):
        for stalled in (False, True):
            with self.subTest(stalled=stalled):
                self.tearDown(); self.setUp()
                windows, keys, data = self.beats(stalled)
                self.assertEqual(set(windows), {at(12, 0).isoformat()})
                self.assertEqual(keys, {"cap:2026-11-13T12:00Z"})
                self.assertEqual(data["cap"], {"window": at(12, 0).isoformat(), "count": 0})
                if stalled:
                    self.assertEqual(data["delivery_fault"]["rev"], 1)  # one fault per window, not one per beat


class FeatureOffBytes(L.RunDueHeartbeatItems):
    def settings(self, stalled):
        env = {"PERSISTENCE_HEARTBEAT": "true", "PERSISTENCE_HEARTBEAT_STALLED": "true" if stalled else ""}
        (self.home / ".claude" / "settings.json").write_text(json.dumps({"env": env}), encoding="utf-8")

    def test_off_the_enqueued_fact_has_mains_fields_exactly(self):
        self.settings(stalled=False)
        item = self.item("resume")
        hook.run_due(home=self.home)
        fact, = self.facts("enqueued")
        self.assertEqual(list(fact), ["firing", "fact", "at", *MAIN_ENQUEUED])
        self.assertNotIn("hb", fact)
        self.assertNotIn("set", fact)
        self.assertEqual(fact["firing"], item["id"])

    def test_on_the_enqueued_fact_also_carries_set(self):
        self.settings(stalled=True)
        item = self.item("resume")
        hook.run_due(home=self.home)
        fact, = self.facts("enqueued")
        self.assertEqual(fact["set"], item["set"])
        self.assertEqual(list(fact)[:15], ["firing", "fact", "at", *MAIN_ENQUEUED])

    def test_main_never_had_enqueued_fields_grow(self):
        self.assertEqual(coordination.ENQUEUED_FIELDS, MAIN_ENQUEUED)

    def test_recovery_and_nudge_items_keep_mains_key_order(self):
        o = sid()
        retry = hook.add_heartbeat_wake(id=sid(), kind="crashed", session=sid(), launch="resume", retry_of=o, follows=o,
                                        attempt="retry", cwd=str(self.home / "work"), at=1.0)
        nudge = hook.add_heartbeat_wake(id=sid(), kind="nudge", session=sid(), launch="resume", claim=sid(),
                                        cwd=str(self.home / "work"), at=1.0)
        deferral = hook.add_heartbeat_wake(id=sid(), kind="usage-limit", session=sid(), launch="resume", retry_of=o,
                                           follows=o, attempt="deferral", deferral=2, at=1.0)
        head = ["id", "session", "at", "by", "launch", "reason", "set"]
        self.assertEqual(list(retry), head + ["cwd", "retry_of", "follows", "attempt"])
        self.assertEqual(list(nudge), head + ["cwd", "claim"])
        self.assertEqual(list(deferral), head + ["retry_of", "follows", "attempt", "deferral"])
        on_disk = json.loads((self.state.parent / "wakes.json").read_text(encoding="utf-8"))
        self.assertEqual([list(w) for w in on_disk], [list(retry), list(nudge), list(deferral)])


# --------------------------------------------------------------------- Ember 2, G3: one queue/ledger snapshot
class Interleaving(Base):
    """run-due fired at every point of a beat where it could take wakes.lock. It holds wakes.lock across its two writes
    (the enqueued fact, then the queue), so a point where the beat holds wakes.lock makes it wait: it runs after."""

    def setup_kind(self, kind):
        self.activity(at(6, 33))
        if kind == "reports":
            self.unread("unit:x.service:ab:failed", when=at(6, 0))
            self.beat(at(6, 40))
        else:
            self.beat(at(8, 8))
        [w] = self.hb_items(kind)
        return w

    def run_due(self, w, done):
        """In another thread, as another process would: take wakes.lock without waiting, then fact, then queue."""
        got = []

        def go():
            try:
                with coordination.lock(self.state, "wakes", wait=0):
                    coordination.append(self.state, coordination.enqueued_fact(w, self.now.isoformat(), stalled=True))
                    hook.write_queue_locked([x for x in hook.queue_locked() if x.get("id") != w["id"]])
                    got.append(True)
            except coordination.LockBusy:
                got.append(False)

        t = threading.Thread(target=go)
        t.start()
        t.join()
        if got[0]:
            done.append(True)

    def beat_with_run_due_at(self, w, point):
        calls, done = [], []
        real_queue, real_snap = heartbeat.read_queue, coordination.snapshot

        def maybe():
            calls.append(1)
            if len(calls) == point and not done:
                self.run_due(w, done)

        def read_queue(state):
            maybe()
            return real_queue(state)

        def snapshot(state, *a, **kw):
            if threading.current_thread() is threading.main_thread():
                maybe()
            return real_snap(state, *a, **kw)

        with mock.patch.object(heartbeat, "read_queue", read_queue), mock.patch.object(coordination, "snapshot", snapshot):
            self.beat(self.now + timedelta(minutes=10))
        if not done:
            self.run_due(w, done)  # it waited for the lock: it runs now
        return len(calls)

    def live(self, kind):
        fired = {f["firing"] for f in coordination.snapshot(self.state)[0]
                 if f.get("fact") == "enqueued" and (f.get("hb") or "").split(":")[0] == kind}
        return fired | {w["id"] for w in self.hb_items(kind)}

    def test_never_two_live_wakes_for_one_key_at_any_point(self):
        for kind in ("quiet", "reports"):
            self.tearDown(); self.setUp()
            w = self.setup_kind(kind)
            n = self.beat_with_run_due_at(w, point=10 ** 6)  # count the points
            for point in range(1, n + 1):
                with self.subTest(kind=kind, point=point):
                    self.tearDown(); self.setUp()
                    w = self.setup_kind(kind)
                    self.beat_with_run_due_at(w, point)
                    self.assertEqual(self.live(kind), {w["id"]})
                    if kind == "quiet":
                        self.assertEqual(self.hb()["quiet"]["wake"], w["id"])
                    else:
                        self.assertEqual({e["wake"] for e in self.hb()["reports_q"].values()}, {w["id"]})


# ----------------------------------------------------------- Ember 3 and Claude's reset point: structured, bounded
class ResetParse(unittest.TestCase):
    FIN = datetime(2026, 11, 11, 16, 36, 53, tzinfo=timezone.utc)

    def test_unknown_is_none_never_an_invented_time(self):
        for text in ("resets soon", "resets 14pm (America/New_York)", "resets 2pm (Mars/Olympus)", "resets 2pm",
                     "resets 2pm (../../etc/passwd)", "", None, "You've hit your limit", "resets 25pm (UTC)"):
            self.assertIsNone(heartbeat.reset_time(text, self.FIN), text)

    def test_parsed_is_bounded_to_24_hours(self):
        got = heartbeat.reset_time("resets 2pm (America/New_York)", self.FIN)
        self.assertEqual(got, datetime(2026, 11, 11, 19, 0, tzinfo=timezone.utc))
        self.assertLessEqual(got - self.FIN, timedelta(hours=24))


class UsageHold(Base):
    """Q, R and the tick: a usage-limited heartbeat wake is no fault and no failed attempt, and holds the stalled layer.
    Claude: R and Q had no hold test. Ember: missing or malformed reset is a visible unknown."""

    def quiet_usage(self, body):
        self.activity(at(6, 33))
        self.beat(at(8, 8))
        [q] = self.hb_items("quiet")
        self.fire_hb(q, body, ack=True)
        self.beat(at(8, 18))
        return q

    def test_q_parsed_reset_holds_until_reset_plus_five(self):
        self.quiet_usage(USAGE)
        data = self.hb()
        self.assertEqual(data["holds"]["usage"]["reset"], "parsed")
        self.assertEqual(data["holds"]["usage"]["until"], at(19, 5).isoformat())
        self.assertNotIn("delivery_fault", data)
        t = at(8, 28)
        while t < at(19, 5):
            self.beat(t)
            self.assertEqual(self.hb_items(), [], t)
            t += timedelta(minutes=30)
        self.beat(at(19, 10))
        self.assertEqual(len(self.hb_items("quiet")), 1)

    def test_r_parsed_reset_holds_and_counts_no_failure(self):
        self.activity(at(6, 33))
        key = "unit:x.service:ab:failed"
        self.unread(key, when=at(6, 0))
        self.beat(at(6, 40))
        [r] = self.hb_items("reports")
        self.fire_hb(r, USAGE, ack=False)  # the block never reached a session: the limit came first
        self.beat(at(6, 50))
        data = self.hb()
        self.assertEqual(data["reports_q"][key]["state"], "none")
        self.assertEqual(data["reports_q"][key]["failures"], 0)
        self.assertNotIn("delivery_fault", data)
        self.assertEqual(data["holds"]["usage"]["reset"], "parsed")
        data["misc"]["reports_at"] = at(0, 0).isoformat()  # R's own six-hour pace out of the way: only the hold left
        heartbeat.save(self.state, data)
        self.beat(at(18, 0))
        self.assertEqual(self.hb_items("reports"), [])
        self.beat(at(19, 10))
        self.assertEqual(len(self.hb_items("reports")), 1)

    def test_r_and_q_respect_an_existing_hold(self):
        self.activity(at(6, 33))
        self.unread("unit:x.service:ab:failed", when=at(6, 0))
        data = self.hb()
        data["holds"] = {"usage": {"reset": "parsed", "since": at(6, 0).isoformat(), "until": at(12, 0).isoformat()}}
        heartbeat.save(self.state, data)
        for t in (at(8, 8), at(9, 8), at(11, 50)):
            self.beat(t)
            self.assertEqual(self.hb_items(), [], t)
        self.beat(at(12, 0))
        self.assertEqual(len(self.hb_items("reports")), 1)

    def test_unknown_reset_is_visible_logged_once_and_rechecked(self):
        self.quiet_usage(UNKNOWN)
        hold = self.hb()["holds"]["usage"]
        self.assertEqual(hold["reset"], "unknown")
        self.assertEqual(hold["until"], (at(8, 18) + heartbeat.UNKNOWN_RECHECK).isoformat())
        self.assertNotEqual(hold["until"], (at(8, 18) + timedelta(hours=2)).isoformat())
        self.assertIn("usage hold: reset unknown (no reset time in the output); rechecked every beat, next try 09:18Z",
                      self.status())
        for t in (at(8, 28), at(8, 38), at(9, 8)):
            self.beat(t)
            self.assertEqual(self.hb_items(), [], t)
        notes = [l for l in self.lines if "usage" in l]
        self.assertEqual(notes, ["heartbeat: usage limit, reset unknown; the stalled layer holds and rechecks every "
                                 "beat, next try 09:18Z"])
        self.beat(at(9, 18))  # the recheck: one wake may try
        self.assertEqual(len(self.hb_items("quiet")), 1)

    def test_unknown_hold_clears_early_when_a_firing_ends_without_the_limit(self):
        self.quiet_usage(UNKNOWN)
        self.now = at(8, 30)
        self.activity(at(8, 30))  # a work firing ended ok: the provider answers again
        self.beat(at(8, 38))
        self.assertNotIn("usage", self.hb().get("holds", {}))
        self.assertIn("heartbeat: usage hold over (a firing ended without the limit)", self.lines)

    def test_parsed_hold_in_status(self):
        self.quiet_usage(USAGE)
        self.assertIn("usage hold until 19:05Z (reset read from the output)", self.status())

    def test_tick_unknown_reset_holds_ticks(self):
        self.working()
        self.beat(at(8, 0))
        [t] = self.hb_items("tick")
        self.fire_hb(t, UNKNOWN, ack=False)
        self.beat(at(8, 10))
        data = self.hb()
        self.assertEqual(data["ticks"][t["id"]]["judged"], "neither")
        self.assertEqual(data["holds"]["usage"]["reset"], "unknown")
        self.beat(at(8, 40))  # past the tick gap, but held
        self.assertEqual(self.hb_items("tick"), [])
        self.beat(at(9, 10))
        self.assertEqual(len(self.hb_items("tick")), 1)


class UsageResetC(D.C_Deferrals):
    """C's deferral with an unknown reset: the deferral waits UNKNOWN_RECHECK from the finish, visibly; never 2 h."""

    def test_unknown_reset_defers_to_the_recheck_with_a_journal_line(self):
        with mock.patch.dict(D.USAGE, {"result": "You've hit your limit"}):
            f0, s = self.fire(how="usage", minutes_ago=30)
            self.beat()
        d, = self.items()
        finished = self.now - timedelta(minutes=28)
        self.assertEqual(datetime.fromtimestamp(d["at"], timezone.utc), finished + heartbeat.UNKNOWN_RECHECK)
        self.assertEqual([l for l in self.lines if "reset unknown" in l],
                         [f"heartbeat: usage limit on wake {f0}, reset unknown; deferral 1 tries at "
                          f"{heartbeat.hhmm(finished + heartbeat.UNKNOWN_RECHECK)}"])


class UsageResetA(D.Base):
    """A's nudge refused by the usage limit with no reset time: deferred to the recheck, visibly."""

    def test_unknown_reset_on_a_nudge(self):
        owner = sid()
        record = self.c.claim("SECRET", owner, str(self.work))
        self.beat()
        n, = self.items()
        self.assertEqual(n["claim"], record["id"])
        with mock.patch.dict(D.USAGE, {"result": "You've hit your limit"}):
            self.drain(how="usage")
            self.beat(10)
        d, = self.items()
        self.assertEqual((d["attempt"], d["deferral"], d["follows"]), ("deferral", 1, n["id"]))
        finished = self.now - timedelta(minutes=10 + 18)
        self.assertEqual(datetime.fromtimestamp(d["at"], timezone.utc), finished + heartbeat.UNKNOWN_RECHECK)
        self.assertTrue([l for l in self.lines if f"usage limit on wake {n['id']}, reset unknown" in l])


# --------------------------------------------------------------------------------------- Ember 4, G5: source_off
class SourceOff(Base):
    def test_stalled_without_the_heartbeat_source_is_refused_visibly(self):
        self.settings(PERSISTENCE_OBSERVE_SOURCES="prs, claims")
        self.activity(at(6, 33))
        self.unread("unit:x.service:ab:failed", when=at(6, 0))
        self.working()
        t = at(6, 40)
        while t < at(14, 0):
            self.beat(t)
            t += timedelta(minutes=10)
        data = self.hb()
        self.assertEqual(self.hb_items(), [])
        self.assertFalse(data.get("ticks"))
        self.assertEqual(data.get("reports_q", {}), {})
        self.assertNotIn("delivery_fault", data)
        self.assertEqual(data["misc"]["source_off"], at(6, 40).isoformat())
        self.assertEqual([l for l in self.lines if "source_off" in l],
                         ["heartbeat: source_off: PERSISTENCE_HEARTBEAT_STALLED is on but 'heartbeat' isn't in "
                          "PERSISTENCE_OBSERVE_SOURCES; no R, Q or ticks"])
        self.assertIn("stalled layer: source_off since 06:40Z ('heartbeat' isn't in PERSISTENCE_OBSERVE_SOURCES): "
                      "no R, Q or ticks", self.status())
        self.settings(PERSISTENCE_OBSERVE_SOURCES="prs, heartbeat")
        self.beat(at(14, 0))
        self.assertNotIn("source_off", self.hb()["misc"])
        self.assertIn("heartbeat: source_off over: the heartbeat source is named again", self.lines)
        self.assertTrue(self.hb_items())


# ------------------------------------------------------------------------- Claude: status names its reader
class StatusReader(Base):
    def test_status_names_the_settings_file_it_reads(self):
        self.assertIn(f"settings: {self.home / '.claude' / 'settings.json'} (its env block only; the environment is "
                      f"never read)", self.status())


# --------------------------------------------------------------------- Ember 5, G6: the four choices, told apart
class Choices(Base):
    def test_1_usage_limited_q_is_no_failed_attempt_and_no_fault(self):
        self.activity(at(6, 33))
        self.beat(at(8, 8))
        [q] = self.hb_items("quiet")
        self.fire_hb(q, USAGE, ack=False)
        self.beat(at(8, 18))
        data = self.hb()
        self.assertEqual((data["quiet"]["state"], data["quiet"]["failures"]), ("none", 0))
        self.assertNotIn("delivery_fault", data)
        self.assertIn("usage", data["holds"])

    def test_2_an_undecided_firing_counts_as_running(self):
        self.activity(at(6, 33))
        f = sid()
        coordination.append(self.state, coordination.enqueued_fact({"id": f, "session": sid(), "reason": "x", "work": True},
                                                                   at(8, 6).isoformat()))
        self.beat(at(8, 8))  # enqueued 2 min ago, no start yet: undecided, so running
        self.assertEqual(self.hb_items(), [])

    def test_2b_stuck_enqueued_never_started_reaches_cs_report(self):
        """Claude: a firing that never starts can't hold Q forever: past the start grace it is never-started and C
        reports it (a work firing), and it no longer counts as running."""
        self.activity(at(6, 33))
        f = sid()
        coordination.append(self.state, coordination.enqueued_fact({"id": f, "session": sid(), "reason": "x", "work": True},
                                                                   at(8, 6).isoformat()))
        self.beat(at(8, 8))
        self.beat(at(8, 18))
        self.assertIn(f"wake:{f}:never-started", self.hb()["reported"])
        self.beat(at(9, 50))  # 08:06 is activity (the firing's last fact); 90 min later Q may run
        self.assertEqual(len(self.hb_items("quiet")), 1)

    def test_3_the_heartbeats_own_queued_wake_counts_as_due(self):
        self.activity(at(6, 33))
        self.unread("unit:x.service:ab:failed", when=at(6, 0))
        self.beat(at(8, 8))  # R runs first and queues its wake for now: Q sees a wake due and stays silent
        self.assertEqual(len(self.hb_items("reports")), 1)
        self.assertEqual(self.hb_items("quiet"), [])

    def test_4_a_paused_claim_key_skips_the_hour(self):
        owner = sid()
        self.now = at(6, 0, day=12)
        record = self.c.claim("x", owner, str(self.work))
        self.c.pause(record["id"], owner)
        self.activity(at(6, 0, day=12))
        self.beat(at(6, 10))  # 24 h 10 min paused: the key is emitted and marked
        key = next(k for k in self.hb()["reported"] if k.startswith("paused:"))
        self.beat(at(6, 20))  # ten minutes later, not an hour: R takes it
        self.assertEqual(self.hb()["reports_q"][key]["state"], "queued")


# --------------------------------------------------------------- Ember's ack calls: rendered identity only
class AckRendered(Base):
    def setUp(self):
        super().setUp()
        self.activity(at(6, 33))
        self.keys = [f"unit:u{i}.service:ab:failed" for i in range(8)]
        self.unread(*self.keys, when=at(6, 0))
        self.beat(at(6, 40))
        [self.r] = self.hb_items("reports")

    def test_only_rendered_keys_are_delivered_the_omitted_go_back(self):
        f = self.r["id"]
        coordination.append(self.state, coordination.enqueued_fact(self.r, self.now.isoformat(), stalled=True))
        hook.wakes(lambda items: [])
        self.fact(f, "started", self.now, pid=999999, start_time=1, session=self.r["session"])
        obs, ctx = self.source(self.r["session"])
        [(_, text)] = observed.render([obs], now=self.now)
        shown = [k for k in self.keys if any(k in line for line in text)]
        self.assertTrue(0 < len(shown) < len(self.keys))
        self.assertIn(f"- heartbeat: {len(self.keys) - len(shown)} more not shown", text)
        observed.heartbeat_ack(obs, {"session_id": self.r["session"]}, ctx)
        q = self.hb()["reports_q"]
        self.assertEqual({k for k in self.keys if q[k]["state"] == "delivered"}, set(shown))
        for k in set(self.keys) - set(shown):
            self.assertEqual((q[k]["state"], q[k]["failures"]), ("none", 0), k)
        self.finish(f, {"is_error": False, "result": "x"})
        self.beat(at(6, 50))
        self.assertNotIn("delivery_fault", self.hb())
        self.beat(at(12, 50))  # R's next round carries the omitted ones
        [r2] = self.hb_items("reports")
        self.assertEqual({k for k, e in self.hb()["reports_q"].items() if e.get("wake") == r2["id"]},
                         set(self.keys) - set(shown))


class AckLockLost(Base):
    def test_lost_ack_lock_is_retryable_and_moves_no_breaker(self):
        self.activity(at(6, 33))
        key = "unit:x.service:ab:failed"
        self.unread(key, when=at(6, 0))
        self.beat(at(6, 40))
        [r] = self.hb_items("reports")
        f = r["id"]
        coordination.append(self.state, coordination.enqueued_fact(r, self.now.isoformat(), stalled=True))
        hook.wakes(lambda items: [])
        self.fact(f, "started", self.now, pid=999999, start_time=1, session=r["session"])
        obs, ctx = self.source(r["session"])
        import fcntl

        fd = os.open(str(self.state / "heartbeat.lock"), os.O_RDWR | os.O_CREAT, 0o600)
        fcntl.flock(fd, fcntl.LOCK_EX)  # a beat holds heartbeat.lock past the ack's wait
        try:
            with mock.patch.object(observed, "HB_ACK_WAIT", 0.1):
                observed.acknowledge([obs], {"session_id": r["session"]}, ctx)  # swallowed, as in the hook
        finally:
            os.close(fd)
        self.assertEqual(self.hb()["reports_q"][key]["state"], "queued")
        self.finish(f, {"is_error": False, "result": "x"})  # the session read it and ended ok
        for _ in range(3):  # three times over: a failed attempt each time would trip undeliverable
            self.beat(self.now + timedelta(minutes=10))
            e = self.hb()["reports_q"][key]
            self.assertEqual((e["state"], e["failures"]), ("none", 0))
            self.assertNotIn("delivery_fault", self.hb())
            self.beat(self.now + timedelta(hours=6))
            [r] = self.hb_items("reports")  # re-delivered
            f = r["id"]
            coordination.append(self.state, coordination.enqueued_fact(r, self.now.isoformat(), stalled=True))
            hook.wakes(lambda items: [])
            self.fact(f, "started", self.now, pid=999999, start_time=1, session=r["session"])
            obs, ctx = self.source(r["session"])
            fd = os.open(str(self.state / "heartbeat.lock"), os.O_RDWR | os.O_CREAT, 0o600)
            fcntl.flock(fd, fcntl.LOCK_EX)
            try:
                with mock.patch.object(observed, "HB_ACK_WAIT", 0.1):
                    observed.acknowledge([obs], {"session_id": r["session"]}, ctx)
            finally:
                os.close(fd)
            self.finish(f, {"is_error": False, "result": "x"})
        self.assertIsNone(self.hb()["holds"].get(key))
        self.assertEqual([l for l in self.lines if "undeliverable" in l], [])
        lost = self.state / "heartbeat-ack-lost"
        self.assertEqual(sorted(p.name for p in lost.iterdir()), [f])  # the last one waits for its beat; the rest consumed


# ------------------------------------------------------------ Claude: no literal self name in the templates
class NoLiteralName(Base):
    def test_templates_take_user_and_name_no_self(self):
        for kind in ("quiet", "reports"):
            self.assertNotIn("arden", hook.HEARTBEAT_REASONS[kind])
            self.assertIn("{user}", hook.HEARTBEAT_REASONS[kind])

    def test_the_rendered_lines_name_the_running_user(self):
        self.activity(at(6, 33))
        self.unread("unit:x.service:ab:failed", when=at(6, 0))
        self.beat(at(6, 40))
        [r] = self.hb_items("reports")
        coordination.append(self.state, coordination.enqueued_fact(r, self.now.isoformat(), stalled=True))
        fake = mock.Mock(pw_name="zed")
        with mock.patch("pwd.getpwuid", return_value=fake):
            obs, _ = self.source(r["session"])
        text = "\n".join(obs.lines)
        self.assertIn("persistence-heartbeat@zed", text)
        self.assertNotIn("arden", text)


# --------------------------------------------------------------- Ember 6, point 8: the free-text canary
CANARY = "CANARY-q7Zx free text that must never be copied"


class Canary(Base):
    extra = {"PERSISTENCE_TASKCLAIMS": "1"}

    def test_no_free_text_reaches_state_ledger_correlations_journal_or_output(self):
        # Free text everywhere the heartbeat can see it: a claim's task, a wake's reason, outputs' results.
        self.now = at(6, 0)
        owner = sid()
        self.c.claim(CANARY, owner, str(self.work))
        hook.wakes(lambda items: items + [{"id": sid(), "session": sid(), "at": at(23, 0).timestamp(), "reason": CANARY,
                                           "set": at(6, 0).isoformat()}])
        f = sid()
        coordination.append(self.state, coordination.enqueued_fact({"id": f, "session": sid(), "reason": CANARY},
                                                                   at(6, 1).isoformat(), stalled=True))
        self.fact(f, "started", at(6, 2), pid=999999, start_time=1, session=sid())
        self.finish(f, {"is_error": True, "result": CANARY})
        rendered = []
        t = at(6, 10)
        while t < at(20, 0):
            self.beat(t)
            for w in self.hb_items():
                body = {**USAGE, "result": CANARY} if w["hb"].startswith("quiet") else {"is_error": False, "result": CANARY}
                obs, _ = self.source(w["session"])
                rendered += [line for _, lines in observed.render([obs], now=self.now) for line in lines]
                self.fire_hb(w, body)
            t += timedelta(minutes=10)
            if t == at(9, 0):
                self.working()
        self.assertTrue(self.hb().get("delivery_fault") or self.hb().get("holds"), "the run exercised faults or holds")
        swept = {
            "heartbeat.json": (self.state / "heartbeat.json").read_text(encoding="utf-8"),
            "heartbeat-mode.json": (self.state / "heartbeat-mode.json").read_text(encoding="utf-8"),
            "fired.jsonl": (self.state / "fired.jsonl").read_text(encoding="utf-8"),
            "hb wake items": json.dumps([w for w in self.items() if w.get("hb") or w.get("by") == "heartbeat"]),
            "journal": "\n".join(self.lines),
            "rendered": "\n".join(rendered),
            "status": "\n".join(self.status()),
        }
        for name, text in swept.items():
            self.assertNotIn("CANARY", text, name)
            self.assertNotIn("free text", text, name)


if __name__ == "__main__":
    unittest.main()
