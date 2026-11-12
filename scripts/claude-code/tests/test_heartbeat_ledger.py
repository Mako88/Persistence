"""The heartbeat, slice 2 (docs/design/heartbeat.md on main 2524fe7): the ledger's other facts and what is derived from
them. The run line's `started`/`finished`, the Stop waiter's `taken`, `add_heartbeat_wake`, run-due honouring `launch`
for heartbeat items with the heartbeat's speaker, and the derived per-firing states, including the partial-tail and
unreadable-ledger rules in a beat. No test starts a real claude: no_real_claude is first, launches are faked or run a
plain `sh`."""
import json
import os
import subprocess
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
import run_line  # noqa: E402

T0 = datetime(2026, 11, 11, 23, 0, tzinfo=timezone.utc)


def sid():
    return str(uuid.uuid4())


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(os.path.realpath(self.tmp.name))
        self.home = root / "home"
        (self.home / "work").mkdir(parents=True)
        (self.home / ".claude").mkdir()
        self.state = self.home / ".claude" / "persistence" / "state"
        self.state.mkdir(parents=True)
        self.room_file = root / "room-session.json"
        self.room = sid()
        self.room_file.write_text(json.dumps({"id": self.room, "previous": sid()}), encoding="utf-8")
        self.patches = [mock.patch.object(hook, "STATE", self.state),
                        mock.patch.object(hook, "WAKES", self.state.parent / "wakes.json"),
                        mock.patch.object(hook, "ROOM_SESSION_FILE", self.room_file),
                        mock.patch.object(hook.Path, "home", return_value=self.home)]
        for p in self.patches:
            p.start()

    def tearDown(self):
        for p in reversed(self.patches):
            p.stop()
        self.tmp.cleanup()

    def facts(self, kind=None):
        return [f for f in coordination.snapshot(self.state)[0] if kind is None or f["fact"] == kind]


class RunLine(Base):
    def test_started_then_finished_for_each_firing_with_the_exit_status(self):
        f1, f2, session = sid(), sid(), sid()
        out = self.state / f"wake-{f1}.json"
        env = {**os.environ, "PERSISTENCE_FIRINGS": f"{f1},{f2}", "PERSISTENCE_FIRING_SESSION": session,
               "PERSISTENCE_FIRING_STATE": str(self.state), "PERSISTENCE_FIRING_OUTPUT": out.name}
        probe = self.state / "probe"
        # The child looks at the ledger while it runs: `started` is already there, and fired.lock is free.
        child = (f"import fcntl,os; fd=os.open({str(self.state / 'fired.lock')!r}, os.O_RDWR); "
                 f"fcntl.flock(fd, fcntl.LOCK_EX|fcntl.LOCK_NB); "
                 f"open({str(probe)!r},'w').write(open({str(self.state / 'fired.jsonl')!r}).read()); raise SystemExit(3)")
        code = subprocess.call([sys.executable, "-I", str(Path(run_line.__file__)), sys.executable, "-c", child], env=env)
        self.assertEqual(code, 3)
        seen = [json.loads(line) for line in probe.read_text().splitlines()]
        self.assertEqual({(f["firing"], f["fact"]) for f in seen}, {(f1, "started"), (f2, "started")})
        started = self.facts("started")
        self.assertEqual({f["session"] for f in started}, {session})
        for f in started:
            self.assertIsInstance(f["pid"], int)
            self.assertIsInstance(f["start_time"], int)
        finished = self.facts("finished")
        self.assertEqual({(f["firing"], f["exit"], f["output"]) for f in finished}, {(f1, 3, out.name), (f2, 3, out.name)})

    def test_without_firings_it_just_runs(self):
        env = {k: v for k, v in os.environ.items() if not k.startswith("PERSISTENCE_FIRING")}
        self.assertEqual(subprocess.call([sys.executable, "-I", str(Path(run_line.__file__)), "sh", "-c", "exit 4"], env=env), 4)
        self.assertEqual(self.facts(), [])

    def test_bad_ids_append_nothing(self):
        env = {**os.environ, "PERSISTENCE_FIRINGS": "not-an-id", "PERSISTENCE_FIRING_SESSION": sid(),
               "PERSISTENCE_FIRING_STATE": str(self.state), "PERSISTENCE_FIRING_OUTPUT": "x.json"}
        subprocess.call([sys.executable, "-I", str(Path(run_line.__file__)), "sh", "-c", "exit 0"], env=env)
        self.assertEqual(self.facts(), [])

    def test_work_argv_runs_claude_through_the_run_line(self):
        self.assertIn(str(Path(run_line.__file__).resolve()), hook.WORK_ARGV)
        self.assertNotIn("{", " ".join(hook.WORK_ARGV))


class AddHeartbeatWake(Base):
    def add(self, **kw):
        args = dict(id=sid(), kind="nudge", session=sid(), launch="resume", claim=sid(), cwd=str(self.home / "work"))
        args.update(kw)
        return hook.add_heartbeat_wake(**args), args

    def test_nudge_item_shape_and_fixed_reason(self):
        item, a = self.add()
        self.assertEqual(item["id"], a["id"])
        self.assertEqual(item["by"], "heartbeat")
        self.assertEqual(item["launch"], "resume")
        self.assertEqual(item["claim"], a["claim"])
        self.assertEqual(item["cwd"], a["cwd"])
        self.assertIn(a["claim"], item["reason"])
        self.assertIn("ptask show", item["reason"])
        self.assertIsInstance(item["at"], float)
        self.assertNotIn("retry_of", item)
        self.assertEqual(hook.wakes(), [item])

    def test_retry_reason_names_the_firing_and_copies_nothing(self):
        origin = sid()
        item, a = self.add(kind="crashed", claim=None, retry_of=origin, follows=origin, attempt="retry")
        self.assertIn(origin, item["reason"])
        self.assertIn("stopped without finishing", item["reason"])
        self.assertIn(f"wake-{origin}.txt", item["reason"])

    def test_idempotent_by_id_over_queue_and_ledger(self):
        item, a = self.add()
        self.assertIsNone(self.add(id=a["id"])[0])
        hook.wakes(lambda items: [])
        coordination.append(self.state, {"firing": a["id"], "fact": "taken", "at": "x"})
        self.assertIsNone(self.add(id=a["id"])[0])
        self.assertEqual(hook.wakes(), [])

    def test_validation(self):
        for bad in (dict(id="x"), dict(session="abc"), dict(claim="not"), dict(launch="fork"), dict(kind="whatever"),
                    dict(attempt="again", claim=None, retry_of=sid(), follows=sid()), dict(deferral=4, attempt="deferral",
                    claim=None, retry_of=sid(), follows=sid()), dict(retry_of="nope", claim=None, attempt="retry", follows=sid())):
            with self.assertRaises(ValueError, msg=bad):
                self.add(**bad)
        self.assertEqual(hook.wakes(), [])

    def test_refuses_the_room_session_and_its_previous(self):
        with self.assertRaises(ValueError):
            self.add(session=self.room)
        with self.assertRaises(ValueError):
            self.add(session=json.loads(self.room_file.read_text())["previous"])

    def test_peers_add_wake_still_drops_heartbeat_keys(self):
        hook.add_wake({"in": 1, "reason": "r", "session": sid(), "by": "heartbeat", "claim": sid(), "launch": "session-id",
                       "retry_of": sid(), "follows": sid(), "attempt": "retry", "deferral": 1})
        item = hook.wakes()[0]
        for key in ("by", "claim", "launch", "retry_of", "follows", "attempt", "deferral"):
            self.assertNotIn(key, item)


class Taken(Base):
    def waiter(self, items):
        hook.wakes(lambda _: items)
        with mock.patch.object(hook, "alive", return_value=True):
            return hook.wait_for_wake({"session_id": items[0]["session"], "seconds": 1, "pid": None}, poll=0.01)

    def test_a_live_session_taking_a_heartbeat_item_leaves_a_taken_fact(self):
        session = sid()
        item = hook.add_heartbeat_wake(id=sid(), kind="nudge", session=session, launch="resume", claim=sid(),
                                       cwd=str(self.home / "work"), at=datetime.now().timestamp() - 60)
        reply = self.waiter([item])
        self.assertTrue(reply["wake"])
        self.assertEqual([(f["firing"], f["session"], f["claim"]) for f in self.facts("taken")],
                         [(item["id"], session, item["claim"])])
        self.assertEqual(hook.wakes(), [])

    def test_an_ordinary_item_leaves_no_fact(self):
        self.waiter([{"id": sid(), "session": sid(), "at": datetime.now().timestamp() - 60, "reason": "r"}])
        self.assertEqual(self.facts(), [])


class RunDueHeartbeatItems(Base):
    """run-due honours `launch` for a heartbeat item, through cwd-exec, with the heartbeat's speaker."""

    def setUp(self):
        super().setUp()
        self.launched = []

        def fake_launch(fd, argv, **popen):
            os.close(fd)
            self.launched.append({"argv": list(argv), "env": dict(popen.get("env") or {}), "held": coordination.held_locks()})
            return mock.Mock(pid=0)

        self.more = [mock.patch.object(claims, "launch", fake_launch),
                     mock.patch.object(claims, "process_table", return_value=[]),
                     mock.patch.object(hook.subprocess, "Popen", side_effect=AssertionError("a launch outside cwd-exec")),
                     mock.patch.object(hook, "server_environ", side_effect=lambda e: dict(e))]
        for p in self.more:
            p.start()

    def tearDown(self):
        for p in reversed(self.more):
            p.stop()
        super().tearDown()

    def item(self, launch, **kw):
        return hook.add_heartbeat_wake(id=sid(), kind=kw.pop("kind", "crashed"), session=kw.pop("session", sid()),
                                       launch=launch, claim=None, retry_of=(o := sid()), follows=o, attempt="retry",
                                       cwd=str(self.home / "work"), at=T0.timestamp() - 600, **kw)

    def test_resume_launch(self):
        item = self.item("resume")
        hook.run_due(home=self.home)
        call, = self.launched
        argv = call["argv"]
        self.assertEqual(argv[argv.index("--resume") + 1], item["session"])
        self.assertNotIn("--session-id", argv)
        self.assertEqual(call["held"], [])
        self.assertTrue(call["env"]["PERSISTENCE_SPEAKER"].endswith("(the heartbeat)"))
        self.assertEqual(call["env"]["PERSISTENCE_FIRINGS"], item["id"])
        self.assertEqual([f["firing"] for f in self.facts("enqueued")], [item["id"]])
        self.assertTrue((self.state / f"wake-{item['id']}.txt").exists())

    def test_session_id_launch(self):
        item = self.item("session-id", kind="never-started")
        hook.run_due(home=self.home)
        argv = self.launched[0]["argv"]
        self.assertEqual(argv[argv.index("--session-id") + 1], item["session"])
        self.assertNotIn("--resume", argv)

    def test_session_id_refused_when_a_transcript_exists(self):
        item = self.item("session-id", kind="never-started")
        project = self.home / ".claude" / "projects" / "-x"
        project.mkdir(parents=True)
        (project / f"{item['session']}.jsonl").write_text("", encoding="utf-8")
        hook.run_due(home=self.home)
        self.assertEqual(self.launched, [])
        self.assertEqual([f["firing"] for f in self.facts("enqueued")], [item["id"]])  # on record: never started
        self.assertEqual(hook.wakes(), [])

    def test_without_the_run_line_nothing_fires_and_it_says_so_once(self):
        """Claude's Q3: a launch without run_line.py leaves only an enqueued fact, so a resume wake that ran reads as
        never started and gets retried (it runs twice). So run-due refuses: no launch, no fact, the queue untouched,
        one line naming the missing file. Nothing falls back to a bare claude."""
        import contextlib
        import io

        item = self.item("resume")
        hook.wakes(lambda items: items + [{"id": sid(), "session": sid(), "at": 0, "reason": "plain"}])
        before = hook.WAKES.read_bytes()
        err = io.StringIO()
        with mock.patch.object(hook, "RUN_LINE", self.home / "nowhere" / "run_line.py"), contextlib.redirect_stderr(err):
            hook.run_due(home=self.home)
        self.assertEqual(self.launched, [])
        self.assertEqual(self.facts(), [])
        self.assertEqual(hook.WAKES.read_bytes(), before)
        lines = err.getvalue().splitlines()
        self.assertEqual(len(lines), 1, lines)
        self.assertIn("run_line.py", lines[0])
        self.assertEqual(hook.wakes()[0]["id"], item["id"])

    def test_no_launch_falls_back_to_a_bare_claude(self):
        self.assertNotIn("exec claude", hook.WORK_ARGV[2])
        self.assertTrue(hook.run_line_prefix().startswith("python3 -I "))
        with mock.patch.object(hook, "RUN_LINE", self.home / "nowhere" / "run_line.py"):
            self.assertTrue(hook.run_line_prefix().startswith("python3 -I "))  # never silently empty

    def test_a_cwd_outside_home_is_refused_at_launch(self):
        item = self.item("resume")
        hook.wakes(lambda items: [{**items[0], "cwd": "/tmp"}])
        hook.run_due(home=self.home)
        self.assertEqual(self.launched, [])
        self.assertEqual(len(self.facts("enqueued")), 1)


class DerivedStates(unittest.TestCase):
    def fact(self, firing, kind, minutes, **kw):
        return {"firing": firing, "fact": kind, "at": (T0 + timedelta(minutes=minutes)).isoformat(), **kw}

    def states(self, facts, now=T0 + timedelta(minutes=30), table=(), alive=lambda pid, start: False, seen=None,
               outputs=None):
        return heartbeat.firing_states(facts, now, list(table), alive, seen if seen is not None else {}, outputs or {})

    def test_each_state(self):
        F = {k: sid() for k in ("young", "never", "running", "wrapper", "crash", "ok", "fail", "nz", "missing", "taken")}
        S = sid()
        facts = [self.fact(F["young"], "enqueued", 27, session=sid()), self.fact(F["never"], "enqueued", 0, session=sid())]
        for k in ("running", "wrapper", "crash", "ok", "fail", "nz", "missing"):
            facts += [self.fact(F[k], "enqueued", 0, session=S if k == "running" else sid()),
                      self.fact(F[k], "started", 1, pid=100 if k == "wrapper" else 200, start_time=5, session=S if k == "running" else sid())]
        facts += [self.fact(F["ok"], "finished", 2, exit=0, output="ok.json"),
                  self.fact(F["fail"], "finished", 2, exit=0, output="fail.json"),
                  self.fact(F["nz"], "finished", 2, exit=1, output="nz.json"),
                  self.fact(F["missing"], "finished", 2, exit=0, output="missing.json"),
                  self.fact(F["taken"], "taken", 2, session=sid())]
        outputs = {"ok.json": {"is_error": False}, "fail.json": {"is_error": True}, "nz.json": {"is_error": False},
                   "missing.json": None}
        seen = {F["crash"]: (T0 + timedelta(minutes=15)).isoformat()}
        got = self.states(facts, table=[["claude", "--resume", S]], alive=lambda pid, start: pid == 100 and start == 5,
                          seen=seen, outputs=outputs)
        want = {"young": None, "never": "never-started", "running": "running", "wrapper": "running", "crash": "crashed",
                "ok": "ok", "fail": "failed", "nz": "failed", "missing": "failed", "taken": "taken"}
        self.assertEqual({k: got.get(F[k]) for k in F}, want)

    def test_crashed_only_after_a_full_beat(self):
        F = sid()
        facts = [self.fact(F, "enqueued", 0, session=sid()), self.fact(F, "started", 1, pid=1, start_time=1, session=sid())]
        seen = {}
        self.assertIsNone(self.states(facts, seen=seen).get(F))  # first seen now: not yet
        self.assertIn(F, seen)
        self.assertIsNone(self.states(facts, now=T0 + timedelta(minutes=35), seen=seen).get(F))
        self.assertEqual(self.states(facts, now=T0 + timedelta(minutes=41), seen=seen).get(F), "crashed")

    def test_recycled_pid_with_another_start_time_is_not_alive(self):
        F = sid()
        facts = [self.fact(F, "enqueued", 0, session=sid()), self.fact(F, "started", 1, pid=100, start_time=5, session=sid())]
        seen = {F: (T0 - timedelta(minutes=20)).isoformat()}
        self.assertEqual(self.states(facts, seen=seen, alive=lambda pid, start: pid == 100 and start == 6).get(F), "crashed")


class WrapperAlive(unittest.TestCase):
    def test_same_pid_and_start_time_only(self):
        me = os.getpid()
        start = run_line.start_time(me)
        self.assertIsInstance(start, int)
        self.assertTrue(heartbeat.wrapper_alive(me, start))
        self.assertFalse(heartbeat.wrapper_alive(me, start + 1))  # a recycled pid has another start time
        self.assertFalse(heartbeat.wrapper_alive(None, start))
        dead = subprocess.Popen(["true"])
        dead.wait()
        self.assertFalse(heartbeat.wrapper_alive(dead.pid, start))


class LedgerInABeat(Base):
    def setUp(self):
        super().setUp()
        (self.home / ".claude" / "settings.json").write_text(json.dumps({"env": {"PERSISTENCE_HEARTBEAT": "true"}}))
        self.lines = []

    def beat(self):
        return heartbeat.beat(state=self.state, home=self.home, now=lambda: T0, sink=self.lines.append,
                              systemctl=lambda argv: (1, ""), user="nobody")

    def test_partial_tail_means_no_ledger_decision_this_beat(self):
        coordination.append(self.state, {"firing": sid(), "fact": "enqueued", "at": "x"})
        with open(self.state / "fired.jsonl", "ab") as f:
            f.write(b'{"firing": "half')
        with mock.patch.object(heartbeat.Beat, "ledger_decisions", side_effect=AssertionError("decided on a partial ledger")):
            self.beat()
        self.assertEqual(len(list(self.state.glob("fired.partial-*"))), 1)
        self.assertTrue((self.state / "fired.jsonl").read_bytes().endswith(b"\n"))
        self.assertEqual(self.lines, [])
        with mock.patch.object(heartbeat.Beat, "ledger_decisions") as decided:
            self.beat()
        decided.assert_called_once()

    def test_a_bad_line_that_is_not_the_tail_is_reported_once_and_nothing_decided(self):
        (self.state / "fired.jsonl").write_bytes(b'{"firing":"a","fact":"enqueued","at":"x"}\nnot json\n{"firing":"b","fact":"x","at":"y"}\n')
        with mock.patch.object(heartbeat.Beat, "ledger_decisions", side_effect=AssertionError("decided on a bad ledger")):
            self.beat()
            self.beat()
        self.assertEqual(self.lines, ["heartbeat ledger: unreadable, at 23:00Z key=ledger:42:unreadable"])


if __name__ == "__main__":
    unittest.main()
