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
# The real shape (Claude's wake-b88e7b01, review 1570218788572635177), less the 429: the api error alone must be enough.
USAGE = {"type": "result", "subtype": "success", "is_error": True, "api_error": "usage_limit_reached",
         "api_error_origin": "first_party", "terminal_reason": "api_error",
         "result": "You've hit your 5-hour limit \u00b7 resets 2pm (America/New_York)"}


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


    def test_the_ledger_written_queue_not_yet_removed_window_reconciles(self):
        """Ember's B3: run-due died after the retry's enqueued fact (`at` ISO text) and before the queue rewrite (the
        item's `at` a number). The beat must not compare the two. Inside the start grace it does nothing; run-due's
        next minute only drops an already-enqueued item, so that attempt never starts, and once the grace has passed
        it is reported once as `start failed` (and B reports the stuck item overdue). Never a second
        retry."""
        record = self.c.create("t", owner=None, cwd=str(self.work), by=sid(), dispatch="handoff", status="open",
                               origin_session=sid())
        f0, s = self.fire(how="never-started", work=True, claim=record["id"])
        self.beat()
        r1, = self.items()
        coordination.append(self.state, coordination.enqueued_fact(r1, self.now.isoformat()))  # and then a crash
        self.assertEqual(self.beat(1), "ok")
        self.assertEqual([w["id"] for w in self.items()], [r1["id"]])
        self.assertEqual(self.lines, [])
        self.assertEqual(self.beat(10), "ok")  # still queued (run-due not back yet), past the grace
        self.assertEqual(self.beat(10), "ok")
        self.assertEqual([w["id"] for w in self.items()], [r1["id"]])  # nothing added
        self.assertEqual(sorted(l.split(" key=")[1] for l in self.lines),
                         [f"claim:{record['id']}:0:start-failed", f"wake:{r1['id']}:overdue"])  # B sees run-due is late

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


class OneRecoveryPerSpawn(Base):
    """Q4 (Ember's call, 1570222344146780254): one recovery total per logical spawn attempt per spell. Usage-limit
    deferrals keep their own budget. Both orders: the hand-off's retry went through the spawn claim (it was the one
    recovery, so no nudge after it), and the spawn claim on the first firing (the session-id nudge is the one)."""

    def handoff(self):
        return self.c.create("t", owner=None, cwd=str(self.work), by=sid(), dispatch="handoff", status="open",
                             origin_session=sid())

    def spawn(self, record, owner, firing):
        with coordination.lock(self.state, "wakes"), coordination.lock(self.state, "tasks"):
            self.c.spawn_locked(record["id"], owner, firing, record["cwd"], bind=False)

    def retried_then_spawned(self):
        """f0 failed before the spawn claim; A's retry r1 then took the spawn claim (run-due's _heartbeat_spawn)."""
        record = self.handoff()
        f0, s = self.fire(how="never-started", work=True, claim=record["id"])
        self.beat()
        r1, = self.items()
        self.assertEqual((r1["attempt"], r1["launch"]), ("retry", "session-id"))
        self.spawn(record, s, r1["id"])
        return record, f0, s, r1

    def keys(self):
        return [l.split(" key=")[1] for l in self.lines]

    def test_retry_then_spawn_claim_then_failure_is_reported_not_nudged(self):
        record, f0, s, r1 = self.retried_then_spawned()
        self.drain(how="failed")  # the owner never ran: no transcript
        for _ in range(3):
            self.beat(10)
        self.assertEqual(self.items(), [])
        spell = claims.spell_start(self.c.get(record["id"]))
        self.assertEqual(self.keys(), [f"claim:{record['id']}:{spell}:start-failed"])
        self.assertIsNone(self.c.get(record["id"])["nudged"])

    def test_retry_then_spawn_claim_then_usage_limit_is_deferred_then_reported(self):
        record, f0, s, r1 = self.retried_then_spawned()
        self.drain(how="usage")
        self.beat(10)
        d1, = self.items()
        self.assertEqual((d1["attempt"], d1["deferral"], d1["retry_of"], d1["follows"], d1["claim"], d1["launch"]),
                         ("deferral", 1, f0, r1["id"], record["id"], "session-id"))
        self.drain(how="failed")
        self.now += timedelta(hours=1)
        for _ in range(2):
            self.beat(10)
        self.assertEqual(self.items(), [])
        self.assertEqual([k.rsplit(":", 1)[1] for k in self.keys()], ["start-failed"])
        self.assertIsNone(self.c.get(record["id"])["nudged"])

    def test_retry_still_queued_or_young_waits(self):
        record, f0, s, r1 = self.retried_then_spawned()
        coordination.append(self.state, coordination.enqueued_fact(r1, self.now.isoformat()))
        hook.wakes(lambda items: [])
        self.beat(1)
        self.assertEqual((self.items(), self.lines), ([], []))

    def test_spawn_claim_first_then_the_nudge_is_the_one_recovery(self):
        record = self.handoff()
        owner, f0 = sid(), sid()
        self.spawn(record, owner, f0)
        self.fire(f0, owner, how="failed", work=True, claim=record["id"])
        self.beat()
        n, = self.items()
        self.assertEqual((n["launch"], n["session"]), ("session-id", owner))
        self.drain(how="crashed")
        for _ in range(3):
            self.beat(10)
        self.assertEqual(self.items(), [])  # no C retry of the nudge, no second nudge
        self.assertEqual([k.rsplit(":", 1)[1] for k in self.keys()], ["start-failed"])

    def test_spawn_claim_first_then_the_nudge_hits_the_usage_limit(self):
        record = self.handoff()
        owner, f0 = sid(), sid()
        self.spawn(record, owner, f0)
        self.fire(f0, owner, how="failed", work=True, claim=record["id"])
        self.beat()
        n, = self.items()
        self.drain(how="usage")
        self.beat(10)
        d1, = self.items()
        self.assertEqual((d1["attempt"], d1["deferral"], d1["follows"], d1["launch"]), ("deferral", 1, n["id"], "session-id"))
        self.drain(how="failed")
        self.now += timedelta(hours=1)
        for _ in range(2):
            self.beat(10)
        self.assertEqual(self.items(), [])
        self.assertEqual([k.rsplit(":", 1)[1] for k in self.keys()], ["start-failed"])

class CorruptState(Base):
    """Ember's B1 and B2: only a missing wakes.json or heartbeat.json may read as empty. Malformed or unreadable, the
    beat makes no decision, leaves the file exactly as it is and says so on stderr (the journal)."""

    def setUp(self):
        super().setUp()
        self.f0, _ = self.fire()  # C would retry this
        owner = sid()
        self.record = self.c.claim("t", owner, str(self.work))  # A would nudge this
        self.transcript(owner)
        self.hb = self.state / "heartbeat.json"

    def tearDown(self):
        for path in (hook.WAKES, self.hb):
            if path.exists():
                path.chmod(0o600)
        super().tearDown()

    def quiet_beat(self, minutes=None):
        import contextlib
        import io

        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            result = self.beat(minutes)
        return result, err.getvalue()

    def assert_no_decision(self):
        self.assertIsNone(self.c.get(self.record["id"])["nudged"])
        self.assertEqual(self.lines, [])
        self.assertFalse((self.state / f"wake-{self.f0}.txt").exists())

    def test_malformed_or_unreadable_wakes_json_aborts_the_beats_decisions_and_is_left_alone(self):
        for bad, mode in ((b"[{", 0o600), (b'{"not": "a list"}', 0o600), (b"[]", 0o000)):
            with self.subTest(bad=bad, mode=mode):
                hook.WAKES.write_bytes(bad)
                hook.WAKES.chmod(mode)
                hb_before = self.hb.read_bytes()
                result, err = self.quiet_beat(10)
                self.assertEqual(result, "queue-unreadable")
                hook.WAKES.chmod(0o600)
                self.assertEqual(hook.WAKES.read_bytes(), bad)
                self.assertEqual(json.loads(self.hb.read_bytes())["cap"], json.loads(hb_before)["cap"])
                self.assertEqual(json.loads(self.hb.read_bytes())["outbox"], {})
                self.assert_no_decision()
                self.assertIn("wakes.json", err)

    def test_missing_wakes_json_is_empty_and_decisions_go_on(self):
        self.assertFalse(hook.WAKES.exists())
        result, _ = self.quiet_beat()
        self.assertEqual(result, "ok")
        self.assertEqual(len(self.items()), 2)  # C's retry and A's nudge

    def test_malformed_or_unreadable_heartbeat_json_fails_closed_and_is_kept(self):
        for bad, mode in ((b"{", 0o600), (b"[]", 0o600), (b'{"reported": ["x"]}', 0o600), (b'{"outbox": "x"}', 0o600),
                          (b'{"reported": {}}', 0o000)):
            with self.subTest(bad=bad, mode=mode):
                self.hb.write_bytes(bad)
                self.hb.chmod(mode)
                result, err = self.quiet_beat(10)
                self.assertEqual(result, "state-unreadable")
                self.hb.chmod(0o600)
                self.assertEqual(self.hb.read_bytes(), bad)
                self.assertEqual(self.items(), [])
                self.assert_no_decision()
                self.assertIn("heartbeat.json", err)

    def test_a_beat_that_fails_closed_exits_non_zero(self):
        self.hb.write_bytes(b"{")
        with mock.patch.object(heartbeat, "beat", return_value="state-unreadable"):
            self.assertEqual(heartbeat.main(), 1)
        with mock.patch.object(heartbeat, "beat", return_value="queue-unreadable"):
            self.assertEqual(heartbeat.main(), 1)

    def test_missing_heartbeat_json_initializes(self):
        self.hb.unlink()
        result, _ = self.quiet_beat()
        self.assertEqual(result, "ok")
        self.assertTrue(self.hb.exists())

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

    def test_marking_the_same_firing_twice_writes_one_entry(self):
        """Claude's M1: the split-nudge reconcile may mark the same firing twice; the second writes nothing."""
        record = self.c.claim("t", sid(), str(self.work))
        n = len(record["history"])
        f = sid()
        first = self.c.mark_nudged(record["id"], f)
        raw = (self.state / "tasks" / f"{record['id']}.json").read_bytes()
        self.now += timedelta(minutes=3)
        second = self.c.mark_nudged(record["id"], f)
        self.assertEqual(len(second["history"]), n + 1)
        self.assertEqual(second["nudged"], first["nudged"])
        self.assertEqual((self.state / "tasks" / f"{record['id']}.json").read_bytes(), raw)


if __name__ == "__main__":
    unittest.main()
