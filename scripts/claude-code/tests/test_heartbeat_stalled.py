"""The heartbeat's stalled-work layer (docs/design/heartbeat-stalled-work.md, PR #26 merged at faaa67a; interface in
docs/design/heartbeat-core-interface.md): the beat order, cap_has_room() and take_cap(), the standing delivery fault,
R and Q with their queued/delivered lifecycle, the two-file order and reconcile, and the working mode with its tick
judging and breakers. Section numbers below are the design's.

Fake clock, temporary state, a fake process table; nothing is launched (run-due is simulated by `drain`)."""
import json
import os
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
import claims  # noqa: E402
import coordination  # noqa: E402
import heartbeat  # noqa: E402
import hook  # noqa: E402
import observed  # noqa: E402

T0 = datetime(2026, 11, 13, 6, 33, tzinfo=timezone.utc)
USAGE = {"type": "result", "is_error": True, "api_error": "usage_limit_reached",
         "result": "You've hit your 5-hour limit · resets 2pm (America/New_York)"}


def sid():
    return str(uuid.uuid4())


def at(hh, mm, day=13):
    return datetime(2026, 11, day, hh, mm, tzinfo=timezone.utc)


class Kill(BaseException):
    """The beat killed at a fault point: nothing after it runs (the Beat's final save included)."""


class Base(unittest.TestCase):
    extra = {}

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(os.path.realpath(self.tmp.name))
        self.home = root / "home"
        self.work = self.home / "work"
        self.work.mkdir(parents=True)
        (self.home / ".claude").mkdir()
        self.state = self.home / ".claude" / "persistence" / "state"
        self.state.mkdir(parents=True)
        self.now = T0
        self.table = []
        self.lines = []
        self.room_id = sid()
        self.room = root / "room-session.json"
        self.room.write_text(json.dumps({"id": self.room_id}), encoding="utf-8")
        self.settings()
        self.patches = [mock.patch.object(hook, "STATE", self.state),
                        mock.patch.object(hook, "WAKES", self.state.parent / "wakes.json"),
                        mock.patch.object(hook, "ROOM_SESSION_FILE", self.room),
                        mock.patch.object(claims, "process_table", side_effect=lambda *a: self.table)]
        for p in self.patches:
            p.start()
        self.c = claims.Claims(self.state, home=self.home, now=lambda: self.now)

    def tearDown(self):
        for p in reversed(self.patches):
            p.stop()
        self.tmp.cleanup()

    def settings(self, **extra):
        env = {"PERSISTENCE_HEARTBEAT": "true", "PERSISTENCE_HEARTBEAT_STALLED": "true", **self.extra, **extra}
        (self.home / ".claude" / "settings.json").write_text(json.dumps({"env": env}), encoding="utf-8")

    def beat(self, when=None):
        if when is not None:
            self.now = when
        return heartbeat.beat(state=self.state, home=self.home, now=lambda: self.now, sink=self.lines.append,
                              systemctl=lambda argv: (1, ""), user="arden")

    def cmd(self, *args):
        out = []
        code = heartbeat.command(list(args), state=self.state, now=self.now, out=out.append)
        return code, out

    def hb(self):
        return heartbeat.load(self.state)

    def items(self):
        return hook.wakes()

    def hb_items(self, kind=None):
        return [w for w in self.items() if w.get("hb") and (kind is None or w["hb"].split(":")[0] == kind)]

    # The ledger, as run-due and the run line write it.
    def fact(self, firing, kind, when, **kw):
        coordination.append(self.state, {"firing": firing, "fact": kind, "at": when.isoformat(), **kw})

    def activity(self, when):
        """A work firing (not the heartbeat's) that ended ok at `when`."""
        firing, session = sid(), sid()
        coordination.append(self.state, coordination.enqueued_fact({"id": firing, "session": session, "reason": "x",
                                                                    "set": (when - timedelta(minutes=2)).isoformat()},
                                                                   (when - timedelta(minutes=1)).isoformat()))
        self.fact(firing, "started", when - timedelta(minutes=1), pid=999999, start_time=1, session=session)
        out = f"wake-{firing}.json"
        (self.state / out).write_text(json.dumps({"is_error": False, "result": "x"}), encoding="utf-8")
        self.fact(firing, "finished", when, exit=0, output=out)
        return firing

    def drain(self, how="ok", ack=True, kinds=None, output=None):
        """run-due and the session, as far as the ledger sees them: each queued heartbeat item (of `kinds`) is enqueued,
        then ends `how` (ok, failed, crashed, never-started, usage, running). With `ack`, the Observed-now block reached
        its session and the source's ack ran (a started session only)."""
        fired = []
        for w in self.hb_items():
            if kinds and w["hb"].split(":")[0] not in kinds:
                continue
            f = w["id"]
            coordination.append(self.state, coordination.enqueued_fact(w, self.now.isoformat()))
            hook.wakes(lambda items, f=f: [x for x in items if x.get("id") != f])
            if how == "never-started":
                self.backdate(f, minutes=6)
                fired.append(w)
                continue
            self.fact(f, "started", self.now, pid=999999, start_time=1, session=w["session"])
            if ack:
                self.ack(w["session"])
            if how == "running":
                self.table.append(["claude", "--session-id", w["session"]])
            elif how == "crashed":
                data = heartbeat.load(self.state)
                data["crashed_seen"][f] = (self.now - timedelta(minutes=11)).isoformat()
                heartbeat.save(self.state, data)
            else:
                out = f"wake-{f}.json"
                body = USAGE if how == "usage" else {"is_error": how == "failed", "result": "x"}
                (self.state / out).write_text(json.dumps(body), encoding="utf-8")
                self.fact(f, "finished", self.now, exit=0, output=out)
            fired.append(w)
        return fired

    def backdate(self, firing, minutes):
        """Rewrite this firing's enqueued fact to `minutes` ago (past the start grace: never started)."""
        path = self.state / "fired.jsonl"
        lines = []
        for raw in path.read_text(encoding="utf-8").splitlines():
            f = json.loads(raw)
            if f.get("firing") == firing and f.get("fact") == "enqueued":
                f["at"] = (self.now - timedelta(minutes=minutes)).isoformat()
            lines.append(json.dumps(f))
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")

    def source(self, session):
        ctx = observed.Context(state=self.state, setting=lambda k, d="": d, run=None, now=self.now)
        return observed.heartbeat_read({"session_id": session}, ctx), ctx

    def ack(self, session):
        obs, ctx = self.source(session)
        if obs.ack is not None:
            observed.heartbeat_ack(obs, {"session_id": session}, ctx)
        return obs

    def mode(self):
        return heartbeat.read_mode(self.state)

    def working(self):
        code, out = self.cmd("working")
        self.assertEqual(code, 0, out)
        return self.mode()["gen"]

    def fill_cap(self, count=3, window=None):
        data = heartbeat.load(self.state)
        data["cap"] = {"window": (window or self.now).isoformat(), "count": count}
        heartbeat.save(self.state, data)

    def nudgeable_claim(self):
        """A claimed claim whose owner isn't running and has no wake: A nudges it (a required wake)."""
        owner = sid()
        record = self.c.claim("SECRET TASK", owner, str(self.work))
        return record, owner


# ---------------------------------------------------------------------------------------------------------- switch
class Switch(Base):
    def test_off_keeps_the_beat_and_the_file_as_before(self):
        self.settings(PERSISTENCE_HEARTBEAT_STALLED="")
        self.activity(T0)
        self.beat(at(12, 0))
        self.assertEqual(self.hb_items(), [])
        raw = json.loads((self.state / "heartbeat.json").read_text())
        self.assertEqual(set(raw), {"reported", "outbox", "crashed_seen", "cap"})
        self.assertFalse((self.state / "heartbeat-mode.json").exists())

    def test_environment_alone_does_not_turn_it_on(self):
        self.settings(PERSISTENCE_HEARTBEAT_STALLED="")
        with mock.patch.dict(os.environ, {"PERSISTENCE_HEARTBEAT_STALLED": "true"}):
            self.assertFalse(heartbeat.stalled_on(self.home))

    def test_quiet_after_and_tick_gap_settings(self):
        self.settings(PERSISTENCE_HEARTBEAT_QUIET_AFTER="45", PERSISTENCE_HEARTBEAT_TICK_GAP="45")
        self.assertEqual(heartbeat.quiet_after(self.home), timedelta(minutes=45))
        self.assertEqual(heartbeat.tick_gap(self.home), (timedelta(minutes=45), False))
        self.settings(PERSISTENCE_HEARTBEAT_QUIET_AFTER="x", PERSISTENCE_HEARTBEAT_TICK_GAP="20")
        self.assertEqual(heartbeat.quiet_after(self.home), timedelta(minutes=90))
        self.assertEqual(heartbeat.tick_gap(self.home), (timedelta(minutes=30), True))


# ------------------------------------------------------------------------------------------------- section 4: cases
class AcceptanceCases(Base):
    def test_morning_one_quiet_wake_then_backoff(self):
        self.activity(at(6, 33))
        for m in (8, 18, 28, 38, 48, 58):
            self.beat(at(7, m))
        self.assertEqual(self.hb_items(), [])  # 06:33 + 90 min is 08:03
        self.beat(at(8, 8))
        [q] = self.hb_items("quiet")
        self.assertEqual(q["hb"], f"quiet:{at(6, 33).isoformat()}")
        self.assertIn("running since 06:33Z and nothing scheduled before none", q["reason"])
        self.assertEqual(q["cwd"], str(self.work))
        self.assertEqual(self.hb()["quiet"]["state"], "queued")
        self.drain("ok")  # the session read the block and did nothing
        self.assertEqual(self.hb()["quiet"]["state"], "delivered")
        for t in (at(8, 18), at(9, 8), at(10, 8), at(10, 58)):
            self.beat(t)
            self.assertEqual(self.hb_items(), [], t)
        self.beat(at(11, 8))  # 2H after 08:08
        self.assertEqual(len(self.hb_items("quiet")), 1)

    def test_afternoon_nothing_while_a_wake_is_due_within_H(self):
        self.activity(at(15, 31))
        self.c  # noqa
        hook.wakes(lambda items: items + [{"id": sid(), "session": sid(), "at": at(17, 11).timestamp(), "reason": "x",
                                           "set": at(15, 30).isoformat()}])
        t = at(15, 38)
        while t <= at(17, 8):
            self.beat(t)
            t += timedelta(minutes=10)
        self.assertEqual(self.hb_items(), [])

    def test_backoff_over_a_twelve_hour_night(self):
        self.activity(at(0, 0, day=14))
        got = []
        t = at(0, 8, day=14)
        while t <= at(12, 8, day=14):
            self.beat(t)
            for w in self.drain("ok"):
                got.append(t)
            t += timedelta(minutes=10)
        # H, then 2H after it, then 4H after that: 01:38, 04:38, 10:38. The next would need 8H.
        self.assertEqual(got, [at(1, 38, day=14), at(4, 38, day=14), at(10, 38, day=14)])


# ------------------------------------------------------------------------------------------- 3.0 and 3.0.1: liveness
class Liveness(Base):
    def setUp(self):
        super().setUp()
        self.activity(at(6, 33))
        self.now = at(8, 8)

    def test_remote_control_and_an_unrelated_claude_are_not_running(self):
        self.table = [["claude", "remote-control"], ["claude", "--session-id", sid()]]
        self.beat()
        self.assertEqual(len(self.hb_items("quiet")), 1)

    def test_a_live_ledger_firing_is_running(self):
        f, s = sid(), sid()
        coordination.append(self.state, coordination.enqueued_fact({"id": f, "session": s, "reason": "x"},
                                                                   (self.now - timedelta(minutes=30)).isoformat()))
        self.fact(f, "started", self.now - timedelta(minutes=29), pid=999999, start_time=1, session=s)
        self.table = [["claude", "--resume", s]]
        self.beat()
        self.assertEqual(self.hb_items(), [])

    def test_a_room_turn_in_progress_is_running_only_with_the_rooms_id(self):
        self.table = [["claude", "--resume", self.room_id]]
        self.beat()
        self.assertEqual(self.hb_items(), [])
        self.room.write_text(json.dumps({"id": sid()}), encoding="utf-8")
        self.beat()
        self.assertEqual(len(self.hb_items("quiet")), 1)

    def test_unknown_liveness_decides_nothing(self):
        self.table = None
        self.beat()
        self.assertEqual(self.hb_items(), [])
        self.table = []
        self.room.write_text("{not json", encoding="utf-8")
        self.beat()
        self.assertEqual(self.hb_items(), [])


class QReset(Base):
    def test_a_turn_that_changed_nothing_leaves_the_spell(self):
        self.activity(at(6, 33))
        self.beat(at(7, 0))
        spell = self.hb()["quiet"]["spell"]
        self.table = [["claude", "--resume", self.room_id]]
        self.beat(at(7, 30))  # a room turn: talk only
        self.table = []
        self.beat(at(8, 8))
        self.assertEqual(self.hb()["quiet"]["spell"], spell)
        self.assertEqual(len(self.hb_items("quiet")), 1)

    def test_a_wake_set_starts_a_new_spell_at_H(self):
        self.activity(at(6, 33))
        hook.wakes(lambda items: items + [{"id": sid(), "session": sid(), "at": at(9, 0).timestamp(), "reason": "x",
                                           "set": at(7, 30).isoformat(), "by": "room"}])
        hook.wakes(lambda items: [])  # it was cancelled again, but the enqueued fact never came: use the ledger
        f = sid()
        coordination.append(self.state, coordination.enqueued_fact(
            {"id": f, "session": sid(), "reason": "x", "set": at(7, 30).isoformat()}, at(7, 31).isoformat()))
        self.fact(f, "taken", at(7, 31))
        self.beat(at(8, 8))
        self.assertEqual(self.hb_items(), [])  # 07:30 + 90 is 09:00
        self.beat(at(9, 0))
        self.assertEqual(len(self.hb_items("quiet")), 1)

    def test_a_claim_change_starts_a_new_spell(self):
        self.activity(at(6, 33))
        self.now = at(7, 40)
        record, owner = self.nudgeable_claim()
        self.c.done(record["id"], owner, "x")
        self.beat(at(8, 8))
        self.assertEqual(self.hb_items(), [])
        self.beat(at(9, 10))
        self.assertEqual(len(self.hb_items("quiet")), 1)

    def test_the_heartbeats_own_firing_is_never_activity(self):
        self.activity(at(6, 33))
        self.beat(at(8, 8))
        self.drain("ok")
        spell = self.hb()["quiet"]["spell"]
        self.beat(at(8, 18))
        self.assertEqual(self.hb()["quiet"]["spell"], spell)
        self.assertEqual(self.hb()["quiet"]["backoff"], 1)


# ------------------------------------------------------------------------------------------- 3.3: the delivery fault
class DeliveryFault(Base):
    def test_no_loop_six_hours_one_fault_counts_cap_six(self):
        for working in (False, True):
            with self.subTest(working=working):
                self.tearDown(); self.setUp()
                self.extra = {"PERSISTENCE_TASKCLAIMS": "1"}
                self.settings()
                self.now = at(10, 0)
                if working:
                    self.working()
                self.nudgeable_claim()
                for h in range(6):
                    self.now = at(10 + h, 0)
                    self.fill_cap()
                    for m in (5, 15, 25, 35, 45, 55):
                        self.beat(at(10 + h, m))
                    hook.wakes(lambda items: [w for w in items if not w.get("hb")])
                f = self.hb()["delivery_fault"]
                self.assertEqual(f["counts"]["cap"], 6)
                self.assertEqual(self.hb().get("reports_q", {}), {})
                self.assertEqual(self.hb_items(), [])
                self.assertFalse(self.hb().get("ticks"))

    def test_deferral_raises_nothing(self):
        for working in (True, False):
            with self.subTest(working=working):
                self.tearDown(); self.setUp()
                self.activity(at(6, 33))
                self.now = at(8, 8)
                if working:
                    self.working()
                self.fill_cap(window=at(8, 0))
                calls = []
                real = heartbeat.Beat.take_cap
                with mock.patch.object(heartbeat.Beat, "take_cap", lambda b, required=True: calls.append(required) or real(b, required)):
                    for m in (8, 18, 28, 38, 48, 58):
                        self.beat(at(8, m))
                self.assertEqual(calls, [])
                data = self.hb()
                self.assertNotIn("delivery_fault", data)
                self.assertFalse([k for k in list(data["reported"]) + list(data["outbox"]) if k.startswith("cap:")])
                self.assertNotIn("last_tick", data.get("misc", {}))
                self.assertFalse(data.get("ticks"))
                if not working:
                    self.assertEqual(data["quiet"]["state"], "none")
                    self.assertEqual(data["quiet"]["backoff"], 0)
                what = "tick" if working else "quiet"
                self.assertEqual([l for l in self.lines if "deferred" in l], [f"heartbeat: {what} deferred, cap full"])
                self.beat(at(9, 8))  # the window reopens
                self.assertEqual(len(self.hb_items(what)), 1)

    def test_own_wakes_failing_fold_into_one_fault(self):
        self.activity(at(6, 33))
        self.beat(at(8, 8))  # quiet
        self.drain("crashed", ack=False)
        data = self.hb()
        data["reported"]["unit:x.service:ab:failed"] = at(6, 0).isoformat()
        heartbeat.save(self.state, data)
        self.beat(at(8, 18))  # reports wake
        [r] = self.hb_items("reports")
        self.drain("crashed", ack=False)
        self.working()
        self.beat(at(8, 28))  # a tick (Q silent in working mode)
        self.drain("crashed", ack=False)
        self.beat(at(8, 38))
        f = self.hb()["delivery_fault"]
        self.assertEqual(f["counts"]["hb_failed"], 3)
        self.assertFalse([k for k in self.hb()["reported"] if k.startswith("wake:")])
        self.assertFalse([k for k in self.hb()["outbox"] if k.startswith("wake:")])
        self.assertEqual(len(self.hb_items("reports")), 0)

    def test_undeliverable_after_three_failures_raises_once_and_waits(self):
        self.activity(at(6, 33))
        data = self.hb()
        self.beat(at(6, 40))
        data = self.hb()
        data["reported"]["unit:x.service:ab:failed"] = at(6, 0).isoformat()
        heartbeat.save(self.state, data)
        t = at(7, 0)
        for _ in range(3):
            self.beat(t)
            self.assertEqual(len(self.hb_items("reports")), 1, t)
            self.drain("ok", ack=False)  # the block never reached the session
            t += timedelta(hours=6)
        self.beat(t)
        f = self.hb()["delivery_fault"]
        self.assertEqual(f["counts"]["undeliverable"], 1)
        self.assertEqual([l for l in self.lines if "undeliverable" in l], ["heartbeat: report undeliverable (unit:x.service:ab:failed)"])
        self.assertEqual(self.hb_items("reports"), [])

    def test_visible_until_acknowledged_and_the_read_writes_nothing(self):
        self.fill_cap(window=at(6, 30))
        self.extra = {"PERSISTENCE_TASKCLAIMS": "1"}
        self.settings()
        self.nudgeable_claim()
        self.beat(at(6, 40))
        f = self.hb()["delivery_fault"]
        before = {p.name: p.read_bytes() for p in self.state.iterdir() if p.is_file()}
        for session in (self.room_id, sid()):
            obs, _ = self.source(session)
            self.assertEqual(obs.lines[0], f"heartbeat delivery fault (rev {f['rev']}) since 06:40Z: 1 cap, 0 failed "
                                           f"heartbeat wakes, 0 undeliverable. pheartbeat ack-fault {f['rev']} acknowledges it")
        self.assertEqual({p.name: p.read_bytes() for p in self.state.iterdir() if p.is_file()}, before)
        self.assertEqual(self.cmd("ack-fault", str(f["rev"]))[0], 0)
        obs, _ = self.source(sid())
        self.assertEqual(obs.lines, ())

    def test_stale_ack_refused_and_rev_never_resets(self):
        b = self.fault_beat()
        for _ in range(3):
            b.raise_fault("hb_failed", sid())
        heartbeat.save(self.state, b.data)
        b.raise_fault("cap", "w")  # grew to rev 4 after rev 3 was shown
        heartbeat.save(self.state, b.data)
        code, out = self.cmd("ack-fault", "3")
        self.assertEqual(code, 1)
        self.assertIn("rev 4", out[0])
        self.assertEqual(self.cmd("ack-fault", "4")[0], 0)
        b.data = self.hb()
        b.raise_fault("cap", "w2")
        heartbeat.save(self.state, b.data)
        f = self.hb()["delivery_fault"]
        self.assertEqual((f["rev"], f["counts"], f["acked_rev"]), (5, {"cap": 1, "hb_failed": 0, "undeliverable": 0}, 4))
        code, out = self.cmd("ack-fault", "4")
        self.assertEqual(code, 1)
        self.assertIn("rev 5", out[0])
        self.assertTrue(observed.fault_showing(f))

    def fault_beat(self):
        b = heartbeat.Beat(self.state, self.home, lambda: self.now, None, None, "arden")
        b.data = heartbeat.load(self.state)
        return b


# ------------------------------------------------------------------------------------------------ 3.1: the lifecycle
class Lifecycle(Base):
    def setUp(self):
        super().setUp()
        self.activity(at(6, 33))
        data = self.hb()
        data["reported"]["unit:x.service:ab:failed"] = at(5, 0).isoformat()
        heartbeat.save(self.state, data)

    def entries(self):
        d = self.hb()
        return d["quiet"], d["reports_q"]["unit:x.service:ab:failed"]

    def test_never_started_goes_back_and_requeues_as_attempt_two(self):
        self.beat(at(8, 8))
        self.assertEqual(len(self.hb_items()), 2)  # R then Q
        self.drain("never-started", ack=False)
        self.now = at(8, 18)
        self.beat()
        q, r = self.entries()
        self.assertEqual((q["state"], q["attempt"]), ("queued", 2))
        self.assertEqual(r["state"], "none")  # R waits 6 h between wakes
        self.assertEqual(r["attempt"], 1)

    def test_cancelled_goes_back_to_none(self):
        self.beat(at(8, 8))
        hook.wakes(lambda items: [])
        self.beat(at(8, 18))
        q, r = self.entries()
        self.assertEqual(q["state"], "queued")
        self.assertEqual(r["state"], "none")

    def test_crashed_or_finished_without_ack_goes_back(self):
        for how in ("crashed", "ok"):
            with self.subTest(how=how):
                self.tearDown(); self.setUp()
                self.beat(at(8, 8))
                self.drain(how, ack=False)
                self.beat(at(8, 28))
                q, r = self.entries()
                self.assertEqual(r["state"], "none")
                self.assertEqual(q["failures"], 1)

    def test_delivered_only_in_the_firings_own_session(self):
        self.beat(at(8, 8))
        [rw] = self.hb_items("reports")
        obs, _ = self.source(sid())
        self.assertIsNone(obs.ack)
        self.drain("ok")
        q, r = self.entries()
        self.assertEqual((q["state"], r["state"]), ("delivered", "delivered"))
        obs = self.source(rw["session"])[0]
        self.assertIsNone(obs.ack)  # nothing queued now: nothing renders

    def test_block_names_counts_and_ids(self):
        self.beat(at(8, 8))
        [rw] = self.hb_items("reports")
        coordination.append(self.state, coordination.enqueued_fact(rw, self.now.isoformat()))
        obs, _ = self.source(rw["session"])
        self.assertIn("1 reports for you", obs.lines[0])
        self.assertIn("unit:x.service:ab:failed", obs.lines)

    def test_late_ack_changes_nothing(self):
        self.beat(at(8, 8))
        [q1] = self.hb_items("quiet")
        coordination.append(self.state, coordination.enqueued_fact(q1, self.now.isoformat()))
        stale, ctx = self.source(q1["session"])
        hook.wakes(lambda items: [])
        self.backdate(q1["id"], 6)
        self.beat(at(8, 18))
        self.beat(at(8, 28))
        self.assertEqual(self.hb()["quiet"]["attempt"], 2)
        observed.heartbeat_ack(stale, {"session_id": q1["session"]}, ctx)
        self.assertEqual(self.hb()["quiet"]["state"], "queued")

    def test_late_ack_for_a_report_key_changes_nothing(self):
        self.table = [["claude", "--resume", self.room_id]]  # Q out of it
        self.beat(at(8, 8))
        [r1] = self.hb_items("reports")
        coordination.append(self.state, coordination.enqueued_fact(r1, self.now.isoformat()))
        stale, ctx = self.source(r1["session"])
        hook.wakes(lambda items: [])
        self.backdate(r1["id"], 6)
        self.beat(at(14, 8))
        [r2] = self.hb_items("reports")
        observed.heartbeat_ack(stale, {"session_id": r1["session"]}, ctx)
        e = self.hb()["reports_q"]["unit:x.service:ab:failed"]
        self.assertEqual((e["state"], e["wake"], e["attempt"]), ("queued", r2["id"], 2))

    def test_quiet_failure_never_suppresses_or_backs_off(self):
        self.beat(at(8, 8))
        self.drain("ok", kinds=("reports",))  # a queued R wake is due, so it would hold Q back on its own
        self.drain("failed", ack=False, kinds=("quiet",))
        self.beat(at(8, 18))
        self.assertEqual(self.hb()["quiet"]["backoff"], 0)
        self.assertEqual(len(self.hb_items("quiet")), 1)


# ----------------------------------------------------------------------------------------------- 3.2: two files
class TwoFiles(Base):
    def setUp(self):
        super().setUp()
        self.activity(at(6, 33))
        self.now = at(8, 8)

    def kill_at(self, point):
        real = hook.add_heartbeat_wake

        def fake(**kw):
            if point == "B":
                real(**kw)
            raise Kill()
        return mock.patch.object(hook, "add_heartbeat_wake", side_effect=fake)

    def test_after_A_back_to_none_then_exactly_one(self):
        with self.kill_at("A"), self.assertRaises(Kill):
            self.beat()
        self.assertEqual(self.hb()["quiet"]["state"], "queued")
        self.assertEqual(self.hb_items(), [])
        self.beat(at(8, 18))
        self.assertEqual(len(self.hb_items("quiet")), 1)
        self.assertEqual(self.hb()["quiet"]["attempt"], 1)

    def test_after_B_nothing_changes(self):
        with self.kill_at("B"), self.assertRaises(Kill):
            self.beat()
        self.beat(at(8, 18))
        self.assertEqual(len(self.hb_items("quiet")), 1)
        self.assertEqual(self.hb()["quiet"]["wake"], self.hb_items()[0]["id"])

    def test_lost_state_is_adopted_never_doubled(self):
        before = (self.state / "heartbeat.json").read_bytes() if (self.state / "heartbeat.json").exists() else None
        self.beat()
        [w] = self.hb_items()
        if before is None:
            (self.state / "heartbeat.json").unlink()
        else:
            (self.state / "heartbeat.json").write_bytes(before)
        self.beat(at(8, 18))
        self.assertEqual(len(self.hb_items()), 1)
        self.assertEqual((self.hb()["quiet"]["state"], self.hb()["quiet"]["wake"]), ("queued", w["id"]))
        self.assertIn(f"heartbeat: adopted orphan wake {w['id']}", self.lines)
        self.drain("ok", ack=False)
        (self.state / "heartbeat.json").unlink()
        self.beat(at(8, 28))
        q = self.hb()["quiet"]
        # Adopted as delivered (a session ran for it), and since that session has ended with the spell unchanged, it
        # counts toward the backoff like any delivered quiet wake. No second wake.
        self.assertEqual((q["state"], q["backoff"], q["wake"]), ("none", 1, w["id"]))
        self.assertEqual(self.hb_items(), [])

    def test_a_duplicate_live_wake_is_removed(self):
        self.beat()
        [w] = self.hb_items()
        dup = dict(w, id=sid(), session=sid())
        hook.wakes(lambda items: items + [dup])
        self.beat(at(8, 18))
        self.assertEqual([x["id"] for x in self.hb_items()], [w["id"]])

    def test_same_id_twice_writes_nothing(self):
        wid = sid()
        kw = dict(id=wid, kind="tick", session=sid(), launch="session-id", hb=f"tick:{sid()}", fill={"tick": wid})
        self.assertIsNotNone(hook.add_heartbeat_wake(**kw))
        self.assertIsNone(hook.add_heartbeat_wake(**kw))
        hook.wakes(lambda items: [])
        self.fact(wid, "enqueued", self.now)
        self.assertIsNone(hook.add_heartbeat_wake(**kw))

    def test_after_C_one_attempt(self):
        self.beat()
        [w] = self.hb_items()
        coordination.append(self.state, coordination.enqueued_fact(w, self.now.isoformat()))  # run-due died here
        self.beat(at(8, 18))
        self.assertEqual(len(self.hb_items()), 1)
        self.assertEqual(self.hb()["quiet"]["attempt"], 1)


class HbField(Base):
    def test_kinds_validate_structure_only(self):
        good = dict(id=sid(), kind="quiet", session=sid(), launch="session-id", hb=f"quiet:{T0.isoformat()}",
                    fill={"since": "06:33Z", "before": "none", "claims": 2, "reports": 0, "user": "arden"})
        hook.add_heartbeat_wake(**good)
        for bad in (dict(hb="quiet:yesterday"), dict(hb="reports"), dict(fill={**good["fill"], "since": "a b"}),
                    dict(fill={"since": "06:33Z"}), dict(claim=sid()), dict(launch="resume")):
            with self.assertRaises(ValueError, msg=bad):
                hook.add_heartbeat_wake(**{**good, "id": sid(), **bad})
        with self.assertRaises(ValueError):
            hook.add_heartbeat_wake(id=sid(), kind="nudge", session=sid(), launch="resume", claim=sid(), hb="reports")

    def test_hb_rides_in_the_enqueued_fact(self):
        w = {"id": sid(), "session": sid(), "reason": "x", "hb": "reports", "set": T0.isoformat()}
        fact = coordination.enqueued_fact(w, T0.isoformat())
        self.assertEqual((fact["hb"], fact["set"]), ("reports", T0.isoformat()))


# ------------------------------------------------------------------------------------------ R: paused, unread, spacing
class Reports(Base):
    def test_paused_without_until_for_24h_makes_exactly_one_reports_wake(self):
        self.extra = {"PERSISTENCE_TASKCLAIMS": "1"}
        self.settings()
        owner = sid()
        self.now = at(17, 28, day=12)
        record = self.c.claim("x", owner, str(self.work))
        self.c.pause(record["id"], owner)
        self.table = [["claude", "--resume", self.room_id]]  # keep Q out of it
        t = at(17, 28, day=13)
        while t <= at(19, 0, day=13):
            self.beat(t)
            t += timedelta(minutes=10)
        self.assertEqual(len(self.hb_items("reports")), 1)
        self.assertIn(f"heartbeat claim {record['id']}: paused with no until", " ".join(self.lines))

    def test_unread_waits_an_hour_and_reports_are_six_hours_apart(self):
        self.table = [["claude", "--resume", self.room_id]]
        data = self.hb()
        data["reported"]["unit:a.service:1:failed"] = at(7, 0).isoformat()
        heartbeat.save(self.state, data)
        self.beat(at(7, 50))
        self.assertEqual(self.hb_items(), [])
        self.beat(at(8, 0))
        self.assertEqual(len(self.hb_items("reports")), 1)
        self.drain("ok")
        data = self.hb()
        data["reported"]["unit:b.service:2:failed"] = at(8, 10).isoformat()
        heartbeat.save(self.state, data)
        self.beat(at(13, 50))
        self.assertEqual(self.hb_items(), [])
        self.beat(at(14, 0))
        self.assertEqual(len(self.hb_items("reports")), 1)

    def test_a_cap_notice_never_queues_r(self):
        self.table = [["claude", "--resume", self.room_id]]
        data = self.hb()
        data["reported"][heartbeat.cap_key(at(5, 0))] = at(5, 0).isoformat()
        heartbeat.save(self.state, data)
        self.beat(at(8, 0))
        self.assertEqual(self.hb_items(), [])

    def test_cap_keys_are_never_r_keys(self):
        self.assertFalse(heartbeat.r_eligible("cap:2026-11-13T08:00Z", set()))
        f = sid()
        self.assertFalse(heartbeat.r_eligible(f"wake:{f}:crashed", {f}))
        self.assertTrue(heartbeat.r_eligible(f"wake:{f}:crashed", set()))


# ----------------------------------------------------------------------------------------------- 5: working mode
class Working(Base):
    def setUp(self):
        super().setUp()
        self.now = at(10, 0)
        self.gen = self.working()

    def tick_progress(self, how="ok"):
        """Drain the queued tick; a progress-making tick says `continue` from inside its session."""
        for w in self.hb_items("tick"):
            coordination.append(self.state, coordination.enqueued_fact(w, self.now.isoformat()))
            hook.wakes(lambda items, f=w["id"]: [x for x in items if x.get("id") != f])
            self.fact(w["id"], "started", self.now, pid=999999, start_time=1, session=w["session"])
            self.assertEqual(self.cmd("continue", w["id"])[0], 0)
            out = f"wake-{w['id']}.json"
            (self.state / out).write_text(json.dumps({"is_error": False}), encoding="utf-8")
            self.fact(w["id"], "finished", self.now, exit=0, output=out)

    def ticks_at(self, start, end, step=10, progress=True, before=None):
        got = []
        t = start
        while t <= end:
            if before:
                before(t)
            self.beat(t)
            for w in self.hb_items("tick"):
                got.append(t)
                self.tick_progress() if progress else self.drain("ok")
            t += timedelta(minutes=step)
        return got

    def test_a_full_hour(self):
        self.table = [["claude", "remote-control"]]
        self.assertEqual(self.ticks_at(at(10, 8), at(11, 8)), [at(10, 8), at(10, 38), at(11, 8)])

    def test_no_expiry(self):
        self.ticks_at(at(10, 8), at(10, 8))
        self.now = at(10, 8) + timedelta(hours=72)
        got = self.ticks_at(self.now, self.now)
        self.assertEqual(len(got), 1)
        self.assertEqual(self.mode()["mode"], "working")

    def fire_nudges(self):
        """run-due for A's nudges: each fired and finished (so it is neither due nor running at the next beat)."""
        for w in [w for w in self.items() if w.get("by") == "heartbeat" and not w.get("hb")]:
            coordination.append(self.state, coordination.enqueued_fact(w, self.now.isoformat()))
            hook.wakes(lambda items, f=w["id"]: [x for x in items if x.get("id") != f])
            self.fact(w["id"], "started", self.now, pid=999999, start_time=1, session=w["session"])
            (self.state / f"wake-{w['id']}.json").write_text(json.dumps({"is_error": False}), encoding="utf-8")
            self.fact(w["id"], "finished", self.now, exit=0, output=f"wake-{w['id']}.json")

    def test_an_r_wake_takes_a_slot_and_a_deferred_tick_raises_nothing(self):
        # Tick :08, R :18, A nudge :28: three wakes in the window opened at :08. The :38 tick is deferred on the cap
        # (no fault, no cap key, last_tick stays :08), and the next tick comes at :08 when the window reopens.
        self.extra = {"PERSISTENCE_TASKCLAIMS": "1"}
        self.settings()
        data = self.hb()
        data["reported"]["unit:a.service:1:failed"] = at(9, 0).isoformat()
        data.setdefault("misc", {})["reports_at"] = at(4, 18).isoformat()  # R's 6 h spacing opens at :18
        heartbeat.save(self.state, data)
        got, r_at, nudge_at = [], [], []
        for t in [at(10, m) for m in (8, 18, 28, 38, 48, 58)] + [at(11, 8)]:
            if t == at(10, 28):
                self.nudgeable_claim()
            self.beat(t)
            if self.hb_items("tick"):
                got.append(t)
                self.tick_progress()
            if self.drain("ok", kinds=("reports",)):
                r_at.append(t)
            if [w for w in self.items() if w.get("by") == "heartbeat" and not w.get("hb")]:
                nudge_at.append(t)
                self.fire_nudges()
        self.assertEqual((got, r_at, nudge_at), ([at(10, 8), at(11, 8)], [at(10, 18)], [at(10, 28)]))
        d = self.hb()
        self.assertNotIn("delivery_fault", d)
        self.assertFalse([k for k in list(d["reported"]) + list(d["outbox"]) if k.startswith("cap:")])
        self.assertIn("heartbeat: tick deferred, cap full", self.lines)

    def test_ticks_last_on_a_contested_beat(self):
        for case in ("A", "R"):
            with self.subTest(case=case):
                self.tearDown(); self.setUp()
                self.extra = {"PERSISTENCE_TASKCLAIMS": "1"}
                self.settings()
                self.ticks_at(at(10, 8), at(10, 8))
                self.fill_cap(count=2, window=at(10, 8))
                if case == "A":
                    self.nudgeable_claim()
                else:
                    d = self.hb()
                    d["reported"]["unit:a.service:1:failed"] = at(9, 0).isoformat()
                    heartbeat.save(self.state, d)
                self.beat(at(10, 38))
                self.assertEqual(self.hb_items("tick"), [])
                if case == "A":
                    self.assertEqual(len([w for w in self.items() if w.get("claim")]), 1)
                else:
                    self.assertEqual(len(self.hb_items("reports")), 1)
                d = self.hb()
                self.assertEqual(d["misc"]["last_tick"], at(10, 8).isoformat(timespec="seconds"))
                self.assertNotIn("delivery_fault", d)
                self.assertEqual((d["counters"]["empty"], d["counters"]["failed"]), (0, 0))

    def test_floor(self):
        self.settings(PERSISTENCE_HEARTBEAT_TICK_GAP="20")
        self.assertEqual(self.ticks_at(at(10, 8), at(10, 48)), [at(10, 8), at(10, 38)])
        self.assertIn("heartbeat: PERSISTENCE_HEARTBEAT_TICK_GAP is under 30 minutes; 30 is used", self.lines)
        self.settings(PERSISTENCE_HEARTBEAT_TICK_GAP="45")
        self.assertEqual(self.ticks_at(at(10, 58), at(11, 28)), [at(11, 28)])

    def test_skips_count_as_neither(self):
        self.ticks_at(at(10, 8), at(10, 8))
        s = sid()
        f = sid()
        coordination.append(self.state, coordination.enqueued_fact({"id": f, "session": s, "reason": "x"}, at(10, 30).isoformat()))
        self.fact(f, "started", at(10, 31), pid=999999, start_time=1, session=s)
        self.table = [["claude", "--resume", s]]
        self.beat(at(10, 38))
        self.assertEqual(self.hb_items(), [])
        self.assertEqual(self.hb()["misc"]["last_tick"], at(10, 8).isoformat(timespec="seconds"))
        c = self.hb()["counters"]
        self.assertEqual((c["empty"], c["failed"]), (0, 0))
        self.table = []
        (self.state / f"wake-{f}.json").write_text(json.dumps({"is_error": False}), encoding="utf-8")
        self.fact(f, "finished", at(10, 40), exit=0, output=f"wake-{f}.json")
        self.assertEqual(self.ticks_at(at(10, 48), at(10, 48)), [at(10, 48)])

    def test_found_nothing(self):
        self.beat(at(10, 8))
        [w] = self.hb_items("tick")
        self.cmd("detect")
        self.drain("ok")
        self.beat(at(11, 0))
        self.assertEqual(self.mode()["mode"], "detect")
        self.assertFalse([k for k in list(self.hb()["outbox"]) + list(self.hb()["reported"]) if k.startswith("working:")])
        self.assertEqual(self.hb_items("tick"), [])

    def judge(self, pattern):
        t = at(10, 8)
        for p in pattern:
            self.beat(t)
            self.drain({"E": "ok", "F": "crashed", "P": "ok"}[p], ack=False)
            if p == "P":
                self.activity(t + timedelta(minutes=1))  # not a progress source; a claim change is:
                record, owner = self.nudgeable_claim()
            t += timedelta(minutes=30)
        self.beat(t)
        return t

    def test_no_progress_breaker(self):
        self.judge("EEE")
        m = self.mode()
        self.assertEqual((m["mode"], m["ended"], m["gen"]), ("detect", "3 ticks without progress", self.gen))
        keys = [k for k in list(self.hb()["outbox"]) + list(self.hb()["reported"]) if k.startswith("working:")]
        self.assertEqual(keys, [f"working:ended:{self.gen}"])

    def test_progress_resets(self):
        self.judge("EEPEE")
        self.assertEqual(self.mode()["mode"], "working")

    def test_mixes(self):
        for pattern, mode in (("EEF", "working"), ("FFE", "working"), ("EFEFE", "detect"), ("FEFEF", "working")):
            with self.subTest(pattern=pattern):
                self.tearDown(); self.setUp()
                self.judge(pattern)
                self.assertEqual(self.mode()["mode"], mode)

    def test_failure_breaker_is_a_visible_suspension(self):
        self.beat(at(10, 8)); self.drain("never-started", ack=False)
        self.beat(at(10, 38)); self.drain("crashed", ack=False)
        self.beat(at(11, 8)); self.drain("failed", ack=False)
        self.beat(at(11, 38))
        m = self.mode()
        self.assertEqual((m["mode"], len(m["ticks"])), ("suspended", 3))
        keys = [k for k in list(self.hb()["outbox"]) + list(self.hb()["reported"]) if k.startswith("working:")]
        self.assertEqual(keys, [f"working:suspended:{self.gen}"])
        code, out = self.cmd("status")
        self.assertTrue(any("working mode SUSPENDED" in l for l in out), out)
        obs, _ = self.source(sid())
        self.assertTrue(any("working mode SUSPENDED" in l for l in obs.lines))
        self.activity(at(11, 40))
        hook.wakes(lambda items: [])
        self.beat(at(13, 20))
        self.assertEqual(self.hb_items("tick"), [])
        self.assertEqual(len(self.hb_items("quiet")), 1)  # Q runs while suspended

    def test_acknowledge_from_suspended(self):
        for verb, mode in (("detect", "detect"), ("working", "working")):
            with self.subTest(verb=verb):
                self.tearDown(); self.setUp()
                self.judge("FFF")
                self.assertEqual(self.mode()["mode"], "suspended")
                self.assertEqual(self.cmd(verb)[0], 0)
                self.assertEqual(self.mode()["mode"], mode)
                self.drain("ok")  # the quiet and reports wakes made while suspended (Q and R run then)
                code, out = self.cmd("status")
                self.assertTrue(any("suspended" in l for l in out if l.startswith("earlier")), out)
                if verb == "working":
                    self.assertNotEqual(self.mode()["gen"], self.gen)
                    t = at(13, 0)
                    self.assertEqual(self.ticks_at(t, t), [t])
                    self.assertEqual(self.hb()["counters"]["failed"], 0)

    def test_continue_refusals(self):
        self.beat(at(10, 8))
        [w] = self.hb_items("tick")
        self.assertEqual(self.cmd("continue", w["id"])[0], 1)  # not started
        self.assertEqual(self.cmd("continue", sid())[0], 1)  # unknown
        self.tick_progress()
        self.beat(at(10, 20))
        self.assertEqual(self.cmd("continue", w["id"])[0], 1)  # judged
        self.beat(at(10, 38))
        [w2] = self.hb_items("tick")
        coordination.append(self.state, coordination.enqueued_fact(w2, self.now.isoformat()))
        self.fact(w2["id"], "started", self.now, pid=999999, start_time=1, session=w2["session"])
        self.cmd("working")  # a newer gen
        self.assertEqual(self.cmd("continue", w2["id"])[0], 1)

    def test_usage_limit_moves_nothing_and_holds_ticks(self):
        self.beat(at(10, 8))
        self.drain("usage", ack=False)
        t = at(10, 38)
        while t <= at(19, 0):
            self.beat(t)
            if self.hb_items("tick"):
                break
            t += timedelta(minutes=10)
        c = self.hb()["counters"]
        self.assertEqual((c["empty"], c["failed"]), (0, 0))
        self.assertNotIn("delivery_fault", self.hb())
        self.assertEqual(t, at(19, 8) if t > at(19, 0) else t)
        self.assertGreaterEqual(t, at(19, 5))  # 2pm New York is 19:00Z; plus 5 minutes

    def test_compare_and_set_on_gen(self):
        self.beat(at(10, 8))
        self.drain("crashed", ack=False)
        self.beat(at(10, 38)); self.drain("crashed", ack=False)
        self.beat(at(11, 8))
        [w] = self.hb_items("tick")
        g2 = self.working()
        self.drain("crashed", ack=False)
        self.beat(at(11, 20))
        self.assertEqual((self.mode()["mode"], self.mode()["gen"]), ("working", g2))

    def test_breaker_writes_only_over_its_own_gen(self):
        # The third failure is judged on a beat during which the mode file changed to a new gen (written by a writer
        # that didn't wait for the lock): the breaker must not overwrite it.
        self.judge("FF")
        self.beat(at(11, 8))
        self.drain("crashed", ack=False)
        g2 = str(uuid.uuid4())
        real = heartbeat.Beat.judge_ticks

        def racing(b):
            heartbeat.write_atomic(heartbeat.mode_path(self.state), {"mode": "working", "gen": g2, "set_at": "x"})
            return real(b)
        with mock.patch.object(heartbeat.Beat, "judge_ticks", racing):
            self.beat(at(11, 20))
        self.assertEqual((self.mode()["mode"], self.mode()["gen"]), ("working", g2))

    def test_malformed_mode_file_is_detection_and_left_alone(self):
        for body in ("{not json", json.dumps({"mode": "sleeping"}), json.dumps({"mode": "working"})):
            with self.subTest(body=body):
                self.lines.clear()
                hook.wakes(lambda items: [])
                heartbeat.mode_path(self.state).write_text(body, encoding="utf-8")
                self.beat(self.now + timedelta(hours=1))
                self.assertEqual(heartbeat.mode_path(self.state).read_text(encoding="utf-8"), body)
                self.assertEqual(self.hb_items("tick"), [])
                self.assertEqual(len([l for l in self.lines if "malformed" in l]), 1)

    def test_atomic_write_leaves_the_old_file_whole(self):
        before = heartbeat.mode_path(self.state).read_bytes()
        with mock.patch.object(heartbeat.os, "replace", side_effect=OSError("killed")):
            with self.assertRaises(OSError):
                self.cmd("detect")
        self.assertEqual(heartbeat.mode_path(self.state).read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
