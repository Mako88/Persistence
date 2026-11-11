"""The heartbeat, slice 3 (docs/design/heartbeat.md on main 2524fe7): the decisions. C (a fired wake that didn't finish
well: one ordinary retry and up to three usage-limit deferrals per origin, every attempt tied to the origin; work
firings A doesn't own reported, never retried; claims off vs on), row A (claims section 4 row by row: nudge once,
report once with the wording fixed at decision, the hand-off rows, owner never started, both split-nudge halves,
not retroactive), the claims-module marks, and phase 3's claim marks tied to the key's spell.

Fake clock, temporary state, fake process table; nothing is launched (run-due isn't run here: slice 4's
cross-document tests run it with the fake claude)."""
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

T0 = datetime(2026, 11, 11, 23, 0, tzinfo=timezone.utc)
USAGE = {"type": "result", "subtype": "success", "is_error": True, "api_error_status": 429, "error": "usage_limit_reached",
         "result": "Claude usage limit reached; resets 2pm (America/New_York)"}


def sid():
    return str(uuid.uuid4())


class Base(unittest.TestCase):
    claims_on = True

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
        self.settings()
        room = root / "room-session.json"
        room.write_text(json.dumps({"id": sid()}), encoding="utf-8")
        self.patches = [mock.patch.object(hook, "STATE", self.state),
                        mock.patch.object(hook, "WAKES", self.state.parent / "wakes.json"),
                        mock.patch.object(hook, "ROOM_SESSION_FILE", room),
                        mock.patch.object(claims, "process_table", side_effect=lambda *a: self.table)]
        for p in self.patches:
            p.start()
        self.c = claims.Claims(self.state, home=self.home, now=lambda: self.now)

    def tearDown(self):
        for p in reversed(self.patches):
            p.stop()
        self.tmp.cleanup()

    def settings(self, **extra):
        env = {"PERSISTENCE_HEARTBEAT": "true", **({"PERSISTENCE_TASKCLAIMS": "1"} if self.claims_on else {}), **extra}
        (self.home / ".claude" / "settings.json").write_text(json.dumps({"env": env}), encoding="utf-8")

    def beat(self, minutes=None):
        if minutes is not None:
            self.now = self.now + timedelta(minutes=minutes)
        return heartbeat.beat(state=self.state, home=self.home, now=lambda: self.now, sink=self.lines.append,
                              systemctl=lambda argv: (1, ""), user="nobody")

    # The ledger, written as run-due and the run line would.
    def fact(self, firing, kind, minutes_ago=30, **kw):
        coordination.append(self.state, {"firing": firing, "fact": kind,
                                         "at": (self.now - timedelta(minutes=minutes_ago)).isoformat(), **kw})

    def fire(self, firing=None, session=None, how="crashed", output=None, minutes_ago=30, **enqueued):
        """A fired wake in a given end state: enqueued, then started/finished as `how` says."""
        firing, session = firing or sid(), session or sid()
        item = {"id": firing, "session": session, "cwd": str(self.work), "reason": "SECRET", **enqueued}
        coordination.append(self.state, {**coordination.enqueued_fact(item, (self.now - timedelta(minutes=minutes_ago)).isoformat())})
        if how in ("crashed", "failed", "ok", "usage"):
            self.fact(firing, "started", minutes_ago - 1, pid=999999, start_time=1, session=session)
            heartbeat.load(self.state)
            data = heartbeat.load(self.state)
            data["crashed_seen"][firing] = (self.now - timedelta(minutes=minutes_ago - 2)).isoformat()
            heartbeat.save(self.state, data)
        if how in ("failed", "ok", "usage"):
            out = f"wake-{firing}.json"
            body = USAGE if how == "usage" else {"is_error": how == "failed", "result": "SECRET"}
            (self.state / out).write_text(json.dumps(body), encoding="utf-8")
            self.fact(firing, "finished", minutes_ago - 2, exit=0, output=out)
        return firing, session

    def items(self):
        return hook.wakes()

    def drain(self, how="crashed", output=None):
        """run-due, as far as the ledger sees it: each queued heartbeat item fired and ended `how`."""
        fired = []
        for w in self.items():
            self.fire(w["id"], w["session"], how=how, minutes_ago=20, **{k: w.get(k) for k in coordination.ENQUEUED_FIELDS
                                                                          if k in w and k not in ("item_at", "reason_sha256", "session")})
            fired.append(w)
        hook.wakes(lambda items: [])
        return fired

    def transcript(self, session):
        d = self.home / ".claude" / "projects" / "-home-work"
        d.mkdir(parents=True, exist_ok=True)
        (d / f"{session}.jsonl").write_text("", encoding="utf-8")


class C_Retry(Base):
    def test_a_crashed_wake_gets_one_retry_tied_to_its_origin(self):
        f0, s = self.fire()
        self.transcript(s)
        self.beat()
        item, = self.items()
        self.assertEqual((item["retry_of"], item["follows"], item["attempt"], item["session"], item["launch"]),
                         (f0, f0, "retry", s, "resume"))
        self.assertEqual(item["cwd"], str(self.work))
        self.assertNotIn("SECRET", json.dumps(item))
        self.beat(10)
        self.assertEqual(len(self.items()), 1)  # answered once: no second retry while it's queued

    def test_no_transcript_means_launch_session_id(self):
        self.fire(how="never-started" if False else "crashed")
        self.beat()
        self.assertEqual(self.items()[0]["launch"], "session-id")

    def test_the_retry_failing_is_reported_once_and_never_retried(self):
        f0, s = self.fire(how="failed")
        self.beat()
        r1, = self.drain(how="failed")
        self.beat(10)
        self.beat(10)
        self.assertEqual(self.items(), [])
        self.assertEqual([l.split(" key=")[1] for l in self.lines], [f"wake:{r1['id']}:failed"])
        self.assertTrue(self.lines[0].startswith(f"heartbeat wake {r1['id']}: failed, owner {s[:8]}"))

    def test_running_session_or_young_firing_is_left_alone(self):
        f0, s = self.fire()
        self.table = [["claude", "--resume", s]]
        self.beat()
        self.assertEqual(self.items(), [])
        self.table = []
        self.fire(how="never-started", minutes_ago=3)  # inside the 5-minute start grace
        self.beat()
        self.assertEqual(self.items(), [])  # f0 was running a beat ago: crashed only after a full beat; the new one is young
        self.beat(11)
        self.assertEqual(len(self.items()), 2)

    def test_cap_three_per_hour_then_one_cap_report(self):
        for _ in range(4):
            self.fire()
        self.beat()
        self.assertEqual(len(self.items()), 3)
        self.assertEqual([l.split(" key=")[1].split(":")[0] for l in self.lines], ["cap"])


class C_Deferrals(Base):
    def test_four_usage_refusals_three_deferrals_then_reported(self):
        f0, s = self.fire(how="usage")
        seen = [f0]
        for n in (1, 2, 3):
            self.beat(10)
            d, = self.items()
            self.assertEqual((d["retry_of"], d["follows"], d["attempt"], d["deferral"]), (f0, seen[-1], "deferral", n))
            self.assertGreater(d["at"], self.now.timestamp())  # the reset + 5 min, not now
            seen.append(d["id"])
            self.drain(how="usage")
            self.now = self.now + timedelta(hours=1)
        self.beat(10)
        self.assertEqual(self.items(), [])
        self.assertEqual([l.split(" key=")[1] for l in self.lines], [f"wake:{seen[-1]}:usage-deferrals-used-up"])
        self.assertIn("usage limit, deferrals used up", self.lines[0])

    def test_deferral_at_is_the_reset_plus_five_minutes(self):
        f0, s = self.fire(how="usage", minutes_ago=30)
        self.beat()
        d, = self.items()
        finished = self.now - timedelta(minutes=28)
        self.assertEqual(datetime.fromtimestamp(d["at"], timezone.utc),
                         heartbeat.reset_time(USAGE["result"], finished) + timedelta(minutes=5))

    def test_mixed_chain_keeps_both_budgets(self):
        f0, s = self.fire(how="usage")
        self.beat()
        d1, = self.drain(how="failed")  # the deferral failed ordinarily: the one retry follows it
        self.beat(10)
        r1, = self.items()
        self.assertEqual((r1["retry_of"], r1["follows"], r1["attempt"]), (f0, d1["id"], "retry"))
        self.drain(how="usage")  # the retry hit the limit: deferral 2, the count carries on
        self.beat(10)
        d2, = self.items()
        self.assertEqual((d2["retry_of"], d2["follows"], d2["attempt"], d2["deferral"]), (f0, r1["id"], "deferral", 2))
        self.drain(how="failed")
        self.now += timedelta(hours=1)
        self.beat(10)
        self.assertEqual(self.items(), [])
        self.assertEqual([l.split(" key=")[1] for l in self.lines], [f"wake:{d2['id']}:failed"])


class C_WorkFirings(Base):
    claims_on = False

    def test_claims_off_a_failed_work_firing_with_a_claim_is_reported_once_and_the_claim_untouched(self):
        record = self.c.create("SECRET TASK", owner=None, cwd=str(self.work), by=sid(), dispatch="handoff", status="open",
                               origin_session=sid())
        before = (self.state / "tasks" / f"{record['id']}.json").read_bytes()
        for how in ("never-started", "crashed", "failed"):
            self.fire(how=how, work=True, claim=record["id"])
        with mock.patch.object(claims.Claims, "settle_pauses", side_effect=AssertionError("A ran with claims off")):
            self.beat()
            self.beat(10)
        self.assertEqual(self.items(), [])
        self.assertEqual(len(self.lines), 3)
        self.assertEqual(sorted(l.split(" key=")[1].rsplit(":", 1)[1] for l in self.lines), ["crashed", "failed", "never-started"])
        self.assertEqual((self.state / "tasks" / f"{record['id']}.json").read_bytes(), before)
        self.assertNotIn("SECRET", "".join(self.lines))

    def test_claims_off_a_work_firing_without_a_claim_too(self):
        self.fire(work=True)
        self.beat()
        self.assertEqual(self.items(), [])
        self.assertEqual(len(self.lines), 1)


class C_SkipsWhatAOwns(Base):
    def test_claims_on_C_does_nothing_for_a_firing_with_a_claim(self):
        record = self.c.create("t", owner=None, cwd=str(self.work), by=sid(), dispatch="handoff", status="open",
                               origin_session=sid())
        for how in ("crashed", "failed", "usage"):
            self.fire(how=how, work=True, claim=record["id"])
        self.beat()
        wake_lines = [l for l in self.lines if l.startswith("heartbeat wake")]
        self.assertEqual(wake_lines, [])
        self.assertFalse(any(w.get("retry_of") and not w.get("claim") for w in self.items()))

    def test_claims_on_a_work_firing_with_no_claim_is_reported_not_retried(self):
        self.fire(how="failed", work=True)
        self.beat()
        self.assertEqual(self.items(), [])
        self.assertEqual(len(self.lines), 1)


class A_Claimed(Base):
    def claimed(self, transcript=True, cwd=None):
        owner = sid()
        record = self.c.claim("SECRET TASK", owner, str(cwd or self.work))
        if transcript:
            self.transcript(owner)
        return record, owner

    def test_nudge_once_with_the_claims_cwd_and_one_history_entry(self):
        record, owner = self.claimed()
        self.beat()
        item, = self.items()
        self.assertEqual((item["claim"], item["session"], item["launch"], item["by"]), (record["id"], owner, "resume", "heartbeat"))
        self.assertEqual(item["cwd"], record["cwd"])
        now = self.c.get(record["id"])
        self.assertIsNotNone(now["nudged"])
        last = now["history"][-1]
        self.assertEqual((last["action"], last["by"], last["firing"], last["from_status"], last["to_status"]),
                         ("nudge", "heartbeat", item["id"], "claimed", "claimed"))
        self.assertEqual(len(now["history"]), len(record["history"]) + 1)
        self.beat(10)
        self.assertEqual(len(self.items()), 1)  # pending: nothing more
        self.assertEqual(self.lines, [])

    def test_report_once_after_the_nudge_fired_and_the_owner_stayed_gone(self):
        record, owner = self.claimed()
        self.beat()
        self.drain(how="ok")
        self.beat(10)
        self.beat(10)
        key = f"claim:{record['id']}:{claims.spell_start(self.c.get(record['id']))}:stalled"
        self.assertEqual([l.split(" key=")[1] for l in self.lines], [key])
        self.assertTrue(self.lines[0].startswith(f"heartbeat claim {record['id']}: stalled after nudge, owner {owner[:8]}"))
        now = self.c.get(record["id"])
        self.assertIsNotNone(now["reported"])
        self.assertEqual((now["history"][-1]["action"], now["history"][-1]["by"]), ("report", "heartbeat"))
        self.assertEqual(len(self.items()), 0)

    def test_no_report_until_a_full_beat_after_the_nudge(self):
        record, owner = self.claimed()
        self.beat()
        self.drain(how="ok")
        self.beat(1)
        self.assertEqual(self.lines, [])
        self.beat(9)
        self.assertEqual(len(self.lines), 1)

    def test_silent_stall_touch_after_nudge_still_reports_saying_touched_since(self):
        record, owner = self.claimed()
        self.beat()
        self.drain(how="ok")
        self.now += timedelta(minutes=2)
        self.c.touch(record["id"], owner)
        self.beat(10)
        self.assertEqual(len(self.lines), 1)
        self.assertIn("stalled after nudge, touched since", self.lines[0])
        self.assertEqual(self.items(), [])  # touch never starts a second nudge

    def test_paused_done_abandoned_get_nothing(self):
        for verb in ("pause", "done", "abandon"):
            record, owner = self.claimed()
            getattr(self.c, verb)(record["id"], owner)
        self.beat()
        self.assertEqual((self.items(), self.lines), ([], []))

    def test_expired_pause_is_settled_by_pause_expiry_then_nudged_as_a_new_spell(self):
        record, owner = self.claimed()
        self.c.pause(record["id"], owner, until=(self.now - timedelta(minutes=1)).isoformat())
        self.beat()
        now = self.c.get(record["id"])
        actions = [(h["action"], h["by"]) for h in now["history"]]
        self.assertIn(("expire", "pause-expiry"), actions)
        self.assertNotIn(("expire", "heartbeat"), actions)
        self.assertEqual(now["status"], "claimed")
        self.assertEqual(len(self.items()), 1)

    def test_liveness_unknown_reports_and_does_not_nudge(self):
        record, owner = self.claimed()
        self.table = None
        self.beat()
        self.assertEqual(self.items(), [])
        self.assertIn("liveness unknown", self.lines[0])

    def test_no_valid_cwd_reports_and_does_not_nudge(self):
        record, owner = self.claimed()
        raw = json.loads((self.state / "tasks" / f"{record['id']}.json").read_text())
        raw["cwd"] = "/tmp"
        (self.state / "tasks" / f"{record['id']}.json").write_text(json.dumps(raw))
        self.beat()
        self.assertEqual(self.items(), [])
        self.assertIn(": no valid cwd,", self.lines[0])

    def test_owner_never_started_is_nudged_with_session_id(self):
        record = self.c.create("t", owner=None, cwd=str(self.work), by=sid(), dispatch="handoff", status="open",
                               origin_session=sid())
        owner, firing = sid(), sid()
        with coordination.lock(self.state, "wakes"), coordination.lock(self.state, "tasks"):
            self.c.spawn_locked(record["id"], owner, firing, record["cwd"], bind=False)
        self.fire(firing, owner, how="failed", work=True, claim=record["id"])
        self.beat()
        item, = self.items()
        self.assertEqual((item["launch"], item["session"], item["claim"]), ("session-id", owner, record["id"]))
        self.assertEqual(self.c.get(record["id"])["owner"], owner)  # no new owner
        self.drain(how="failed")
        self.beat(10)
        self.assertEqual(self.items(), [])
        self.assertEqual(len(self.lines), 1)
        self.assertIn(": start failed,", self.lines[0])

    def test_claims_off_A_does_nothing(self):
        self.settings(PERSISTENCE_TASKCLAIMS="0")
        record, owner = self.claimed()
        with mock.patch.object(claims.Claims, "settle_pauses", side_effect=AssertionError("settled with claims off")):
            self.beat()
        self.assertEqual((self.items(), self.lines), ([], []))


class A_Open(Base):
    def test_open_none_is_reported_unowned_once_and_wakes_nobody(self):
        record = self.c.create("t", owner=None, cwd=str(self.work), by=sid(), dispatch="none", status="open")
        self.beat()
        self.beat(10)
        self.assertEqual(self.items(), [])
        self.assertEqual(len(self.lines), 1)
        self.assertIn(f"heartbeat claim {record['id']}: unowned, owner unowned", self.lines[0])

    def test_open_handoff_never_started_is_retried_once_then_start_failed(self):
        record = self.c.create("t", owner=None, cwd=str(self.work), by=sid(), dispatch="handoff", status="open",
                               origin_session=sid())
        f0, s = self.fire(how="never-started", work=True, claim=record["id"])
        self.beat()
        r1, = self.items()
        self.assertEqual((r1["launch"], r1["session"], r1["claim"], r1["retry_of"], r1["attempt"]),
                         ("session-id", s, record["id"], f0, "retry"))
        self.drain(how="never-started")
        self.beat(10)
        self.assertEqual(self.items(), [])
        self.assertEqual(len(self.lines), 1)
        self.assertIn(": start failed,", self.lines[0])


class SplitNudge(Base):
    def test_wake_written_mark_missing_is_completed_with_that_firing(self):
        owner = sid()
        record = self.c.claim("t", owner, str(self.work))
        self.transcript(owner)
        f = sid()
        self.now += timedelta(minutes=30)  # the nudge comes after the spell began (drain backdates its facts 20 min)
        hook.add_heartbeat_wake(id=f, kind="nudge", session=owner, launch="resume", claim=record["id"], cwd=record["cwd"])
        # crash before the mark; then run-due fired it
        self.drain(how="ok")
        self.beat()
        now = self.c.get(record["id"])
        self.assertEqual(now["history"][-1]["firing"], f)
        self.assertEqual(self.items(), [])  # no second nudge

    def test_claim_marked_wake_absent_re_adds_the_same_id_once(self):
        owner = sid()
        record = self.c.claim("t", owner, str(self.work))
        self.transcript(owner)
        f = sid()
        self.c.mark_nudged(record["id"], f)  # a restored backup: the mark with no item and no fact
        self.beat()
        item, = self.items()
        self.assertEqual(item["id"], f)
        self.beat(1)
        self.assertEqual(len(self.items()), 1)


class NotRetroactive(Base):
    claims_on = False

    def test_claims_turned_on_later_leave_a_C_reported_claim_alone_until_a_new_spell(self):
        record = self.c.create("t", owner=None, cwd=str(self.work), by=sid(), dispatch="handoff", status="open",
                               origin_session=sid())
        owner, firing = sid(), sid()
        with coordination.lock(self.state, "wakes"), coordination.lock(self.state, "tasks"):
            self.c.spawn_locked(record["id"], owner, firing, record["cwd"], bind=False)
        self.fire(firing, owner, how="failed", work=True, claim=record["id"])
        self.beat()
        self.assertEqual(len(self.lines), 1)
        self.claims_on = True
        self.settings()
        for _ in range(3):
            self.beat(10)
        self.assertEqual(self.items(), [])
        self.assertEqual(len(self.lines), 1)
        self.c.pause(record["id"], owner)
        self.c.resume(record["id"], owner)  # the session's own transition: a new spell
        self.transcript(owner)
        self.beat(10)
        self.assertEqual(len(self.items()), 1)


class Phase3(Base):
    def test_a_spell_that_ends_between_emit_and_mark_gets_no_reported(self):
        owner = sid()
        record = self.c.create("t", owner=None, cwd=str(self.work), by=sid(), dispatch="none", status="open")
        real = heartbeat.mark

        def mark_after_change(state, keys, now, home=None):
            self.c.abandon(record["id"], sid())
            return real(state, keys, now, home=home)

        with mock.patch.object(heartbeat, "mark", mark_after_change):
            self.beat()
        self.assertEqual(len(self.lines), 1)
        self.assertIsNone(self.c.get(record["id"])["reported"])
        self.assertEqual(heartbeat.load(self.state)["outbox"], {})


class Phase3Claimed(Base):
    claimed = A_Claimed.claimed

    def test_a_claimed_spell_that_ends_between_emit_and_mark_gets_no_reported(self):
        record, owner = self.claimed()
        self.beat()
        self.drain(how="ok")
        real = heartbeat.mark

        def new_spell_then_mark(state, keys, now, home=None):
            self.c.pause(record["id"], owner)
            self.c.resume(record["id"], owner)
            return real(state, keys, now, home=home)

        with mock.patch.object(heartbeat, "mark", new_spell_then_mark):
            self.beat(10)
        self.assertEqual(len(self.lines), 1)
        now = self.c.get(record["id"])
        self.assertIsNone(now["reported"])
        self.assertNotIn("report", [h["action"] for h in now["history"]])
        self.assertEqual(heartbeat.load(self.state)["outbox"], {})


class MarkMethods(Base):
    def test_each_mark_is_one_history_entry_by_heartbeat(self):
        owner = sid()
        record = self.c.claim("t", owner, str(self.work))
        n = len(record["history"])
        f = sid()
        after = self.c.mark_nudged(record["id"], f)
        self.assertEqual(len(after["history"]), n + 1)
        self.assertEqual((after["history"][-1]["action"], after["history"][-1]["firing"]), ("nudge", f))
        self.assertTrue(self.c.mark_reported(record["id"], claims.spell_start(after)))
        after = self.c.get(record["id"])
        self.assertEqual(len(after["history"]), n + 2)
        self.assertEqual(after["history"][-1]["action"], "report")
        self.assertFalse(self.c.mark_reported(record["id"], claims.spell_start(after) + 7))
        with self.assertRaises(claims.Refused):
            self.c.mark_nudged(record["id"], "not-an-id")


if __name__ == "__main__":
    unittest.main()
