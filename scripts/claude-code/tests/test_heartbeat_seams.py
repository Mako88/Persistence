"""The heartbeat, slice 4 (docs/design/heartbeat.md on main 2524fe7): the seams. Lock order on every multi-lock path,
alone and raced; the ledger under concurrent writers and a writer killed mid-append; the cross-document run (pwake ->
run-due -> the real cwd-exec and run line -> no_real_claude's fake claude -> the heartbeat -> run-due again); and no
text leaking into anything the heartbeat or run-due makes.

The fake claude records its arguments and exits 97; a test that reaches it says so with no_real_claude.expected()."""
import json
import os
import signal
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402  first: no test can start a real claude
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import claims  # noqa: E402
import coordination  # noqa: E402
import heartbeat  # noqa: E402
import hook  # noqa: E402

HERE = Path(__file__).resolve().parent.parent
ROOM = "1569586907955073130"
MARKER = "MARKERQ7"


def sid():
    return str(uuid.uuid4())


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(os.path.realpath(self.tmp.name))
        self.home = root / "home"
        self.work = self.home / "work"
        self.work.mkdir(parents=True)
        (self.home / ".claude").mkdir()
        self.state = self.home / ".claude" / "persistence" / "state"
        self.state.mkdir(parents=True)
        (self.home / ".claude" / "settings.json").write_text(json.dumps(
            {"env": {"PERSISTENCE_HEARTBEAT": "true", "PERSISTENCE_TASKCLAIMS": "1"}}), encoding="utf-8")
        self.room = sid()
        room_file = root / "room-session.json"
        room_file.write_text(json.dumps({"id": self.room}), encoding="utf-8")
        self.lines = []
        self.patches = [mock.patch.object(hook, "STATE", self.state),
                        mock.patch.object(hook, "WAKES", self.state.parent / "wakes.json"),
                        mock.patch.object(hook, "ROOM_SESSION_FILE", room_file),
                        mock.patch.object(hook, "store", side_effect=AssertionError("the store was called")),
                        mock.patch.object(hook, "channel_history", return_value=None),
                        mock.patch.object(hook, "server_environ", side_effect=lambda e: dict(e)),
                        mock.patch.object(hook.Path, "home", return_value=self.home),
                        mock.patch.dict(os.environ, {k: v for k, v in os.environ.items()
                                                     if not k.startswith("PERSISTENCE_") and k != "CLAUDE_CODE_SESSION_ID"},
                                        clear=True)]
        for p in self.patches:
            p.start()
        self.c = claims.Claims(self.state, home=self.home)

    def tearDown(self):
        for p in reversed(self.patches):
            p.stop()
        self.tmp.cleanup()

    def beat(self, now):
        return heartbeat.beat(state=self.state, home=self.home, now=lambda: now, sink=self.sink,
                              systemctl=lambda argv: (1, ""), user="nobody")

    def sink(self, line):
        self.assertEqual(coordination.held_locks(), [], "a report was written holding a lock")
        self.lines.append(line)

    def pwake(self, task="the task"):
        with mock.patch.dict(os.environ, {"PERSISTENCE_ROOM_CHANNEL": ROOM, "CLAUDE_CODE_SESSION_ID": self.room}):
            wake = hook.wake_request(["0", *task.split(" ")])
        return hook.set_handoff(wake, home=self.home)

    def facts(self, kind=None):
        return [f for f in coordination.snapshot(self.state)[0] if kind is None or f["fact"] == kind]


class FakeLaunch:
    """claims.launch's place: records argv, env, the prompt and the locks held; closes the fd; starts nothing."""

    def __init__(self):
        self.calls = []

    def __call__(self, fd, argv, **popen):
        os.close(fd)
        stdin = popen.get("stdin")
        self.calls.append({"argv": list(argv), "env": dict(popen.get("env") or {}), "held": coordination.held_locks(),
                           "prompt": Path(stdin.name).read_text() if hasattr(stdin, "name") else ""})
        return mock.Mock(pid=0)


class LockOrder(Base):
    """Every path that takes more than one lock, run alone under a recorder, then raced. coordination.lock refuses an
    out-of-order acquisition and anything after fired.lock; the recorder also fails a launch or a journal write made
    while any of the four is held."""

    def setUp(self):
        super().setUp()
        self.events = []
        self.launch = FakeLaunch()
        self.more = [mock.patch.object(coordination, "LOCK_OBSERVER", self.events.append),
                     mock.patch.object(claims, "launch", self.launch),
                     mock.patch.object(claims, "process_table", return_value=[]),
                     mock.patch.object(hook.subprocess, "Popen", side_effect=AssertionError("a launch outside cwd-exec"))]
        for p in self.more:
            p.start()

    def tearDown(self):
        for p in reversed(self.more):
            p.stop()
        super().tearDown()

    def acquired(self):
        return [e[1] for e in self.events if e[0] == "acquire"]

    def test_every_path_alone(self):
        t0 = datetime.now(timezone.utc)
        # run-due's transition with the spawn claim: wakes -> tasks -> fired
        record, item = self.pwake()
        hook.run_due(home=self.home)
        self.assertIn(["wakes", "tasks", "fired"], [self.acquired()[i:i + 3] for i in range(len(self.acquired()))])
        self.assertEqual([c["held"] for c in self.launch.calls], [[]])
        # the beat: snapshot, a nudge (wakes -> tasks), a retry (wakes -> fired), and the mark (heartbeat -> tasks)
        owner = sid()
        self.c.claim("x", owner, str(self.work))
        f0 = sid()
        coordination.append(self.state, coordination.enqueued_fact({"id": f0, "session": sid(), "cwd": str(self.work)},
                                                                   (t0 - timedelta(minutes=30)).isoformat()))
        self.events.clear()
        self.beat(t0)
        got = self.acquired()
        self.assertEqual(got[0], "heartbeat")
        self.assertIn("wakes", got)
        # the Stop waiter's take of a heartbeat item: wakes -> fired
        w = next(w for w in hook.wakes() if w.get("by") == "heartbeat")
        self.events.clear()
        with mock.patch.object(hook, "alive", return_value=True):
            hook.wait_for_wake({"session_id": w["session"], "seconds": 1, "pid": None}, poll=0.01)
        self.assertEqual(self.acquired(), ["wakes", "fired"])
        # the run line's appends: fired alone
        self.events.clear()
        coordination.append(self.state, {"firing": sid(), "fact": "started", "at": "x"})
        self.assertEqual(self.acquired(), ["fired"])

    def test_wrong_orders_are_refused(self):
        for first, second in (("tasks", "wakes"), ("fired", "tasks"), ("wakes", "heartbeat"), ("fired", "fired")):
            with self.assertRaises(coordination.LockOrderError, msg=(first, second)):
                with coordination.lock(self.state, first), coordination.lock(self.state, second):
                    pass

    def test_raced_paths_finish_and_fire_each_item_once(self):
        """Beats, run-due, peers' add_wake, the waiter and run-line appends at once, under a timeout."""
        now = datetime.now(timezone.utc)
        for _ in range(4):
            self.pwake()
        errors, stop = [], time.monotonic() + 3

        def loop(fn):
            try:
                while time.monotonic() < stop:
                    fn()
            except Exception as e:  # noqa: BLE001
                errors.append(e)

        jobs = [lambda: self.beat(now), lambda: hook.run_due(home=self.home),
                lambda: hook.add_wake({"in": 0.001, "reason": "r", "session": sid()}),
                lambda: coordination.append(self.state, {"firing": sid(), "fact": "started", "at": "x"}),
                lambda: coordination.snapshot(self.state)]
        threads = [threading.Thread(target=loop, args=(j,)) for j in jobs]
        for t in threads:
            t.start()
        for t in threads:
            t.join(15)
            self.assertFalse(t.is_alive(), "a path deadlocked")
        self.assertEqual(errors, [])
        enq = [f["firing"] for f in self.facts("enqueued")]
        self.assertEqual(len(enq), len(set(enq)), "an item fired twice")
        self.assertTrue(all(c["held"] == [] for c in self.launch.calls))


class LedgerConcurrency(Base):
    def test_eight_writers_of_500_facts_while_snapshots_read(self):
        writer = ("import sys; sys.path.insert(0, sys.argv[1]); import coordination\n"
                  "for i in range(500):\n"
                  "    coordination.append(sys.argv[2], {'firing': sys.argv[3], 'fact': 'started' if i % 2 else 'finished', 'at': str(i)})")
        procs = [subprocess.Popen([sys.executable, "-c", writer, str(HERE), str(self.state), sid()]) for _ in range(8)]
        snaps = 0
        while any(p.poll() is None for p in procs):
            facts, complete = coordination.snapshot(self.state)
            self.assertTrue(complete)
            snaps += 1
        for p in procs:
            self.assertEqual(p.wait(30), 0)
        lines = (self.state / "fired.jsonl").read_bytes().split(b"\n")
        self.assertEqual(lines[-1], b"")
        self.assertEqual(len(lines) - 1, 4000)
        for line in lines[:-1]:
            json.loads(line)
        self.assertGreater(snaps, 0)

    def test_a_writer_killed_mid_append_costs_one_beat_and_nothing_else(self):
        coordination.append(self.state, coordination.enqueued_fact({"id": sid(), "session": sid(), "cwd": str(self.work)},
                                                                   "2026-01-01T00:00:00+00:00"))
        killer = ("import os, signal, sys; sys.path.insert(0, sys.argv[1]); import coordination\n"
                  "def half(fd, data):\n"
                  "    os.write(fd, data[:len(data) // 2]); os.fsync(fd); os.kill(os.getpid(), signal.SIGKILL)\n"
                  "coordination._write = half\n"
                  "coordination.append(sys.argv[2], {'firing': 'f', 'fact': 'started', 'at': 'x'})")
        p = subprocess.run([sys.executable, "-c", killer, str(HERE), str(self.state)])
        self.assertEqual(p.returncode, -signal.SIGKILL)
        tail = (self.state / "fired.jsonl").read_bytes()
        fragment = tail[tail.rfind(b"\n") + 1:]
        self.assertTrue(fragment)
        now = datetime.now(timezone.utc)
        with mock.patch.object(heartbeat.Beat, "ledger_decisions", side_effect=AssertionError("decided on a partial")):
            self.beat(now)
        aside, = self.state.glob("fired.partial-*")
        self.assertEqual(aside.read_bytes(), fragment)
        self.assertTrue((self.state / "fired.jsonl").read_bytes().endswith(b"\n"))
        self.assertEqual(self.lines, [])
        self.assertEqual(hook.wakes(), [])
        with mock.patch.object(heartbeat.Beat, "ledger_decisions") as decided:
            self.beat(now)
        decided.assert_called_once()


class CrossDocument(Base):
    """pwake -> run-due -> the real cwd-exec, run line and fake claude -> the heartbeat -> run-due again."""

    def setUp(self):
        super().setUp()
        self.more = [mock.patch.object(claims, "process_table", return_value=[])]
        for p in self.more:
            p.start()

    def tearDown(self):
        for p in reversed(self.more):
            p.stop()
        super().tearDown()

    def run_due_for_real(self):
        with no_real_claude.expected() as made:
            hook.run_due(home=self.home)
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline and len(self.facts("finished")) < len(self.facts("started")) or \
                    len(self.facts("started")) < self.launchable():
                time.sleep(0.05)
                if time.monotonic() > deadline:
                    break
        return made

    def launchable(self):
        return len([f for f in self.facts("enqueued")]) - self.refused

    refused = 0

    def test_route_2_failure_after_the_spawn_claim(self):
        record, item = self.pwake()
        made = self.run_due_for_real()
        self.assertEqual(len(made), 1)
        self.assertIn(f"--session-id {item['session']}", made[0])
        fin, = self.facts("finished")
        self.assertEqual(fin["exit"], 97)
        self.assertEqual(self.c.get(record["id"])["status"], "claimed")
        self.assertEqual(self.c.get(record["id"])["owner"], item["session"])
        later = datetime.now(timezone.utc) + timedelta(minutes=11)
        self.beat(later)
        nudge, = hook.wakes()
        self.assertEqual((nudge["launch"], nudge["session"], nudge["claim"]), ("session-id", item["session"], record["id"]))
        hook.wakes(lambda items: [dict(w, at=0) for w in items])
        made = self.run_due_for_real()
        self.assertEqual(len(made), 1)
        self.assertIn(f"--session-id {item['session']}", made[0])
        self.assertNotIn("--resume", made[0])
        self.assertEqual(self.c.get(record["id"])["owner"], item["session"])  # no second owner
        self.assertEqual(len(self.c.all()), 1)  # no second claim
        for minutes in (22, 33, 44):
            self.beat(datetime.now(timezone.utc) + timedelta(minutes=minutes))
        self.assertEqual(hook.wakes(), [])
        self.assertEqual(len(self.lines), 1)
        self.assertIn(f"heartbeat claim {record['id']}: start failed", self.lines[0])
        self.assertFalse(any(l.startswith("heartbeat wake") for l in self.lines), "C duplicated A")

    def test_route_1_failure_before_the_spawn_claim(self):
        record, item = self.pwake()
        with mock.patch.object(claims.Claims, "spawn_locked", side_effect=claims.Refused("fault before the spawn claim")):
            hook.run_due(home=self.home)
        self.assertEqual(self.c.get(record["id"])["status"], "open")
        self.assertEqual(self.facts("started"), [])
        self.beat(datetime.now(timezone.utc) + timedelta(minutes=6))
        retry, = hook.wakes()
        self.assertEqual((retry["launch"], retry["session"], retry["claim"]), ("session-id", item["session"], record["id"]))
        hook.wakes(lambda items: [dict(w, at=0) for w in items])
        made = self.run_due_for_real()
        self.assertEqual(len(made), 1)
        self.assertIn(f"--session-id {item['session']}", made[0])
        self.assertNotIn("--resume", made[0])
        self.assertEqual(self.c.get(record["id"])["owner"], item["session"])
        self.assertFalse(any(l.startswith("heartbeat wake") for l in self.lines), "C duplicated A")
        # Q4: that retry took the spawn claim and failed with no transcript; it was the one recovery, so no nudge.
        for minutes in (22, 33, 44):
            self.beat(datetime.now(timezone.utc) + timedelta(minutes=minutes))
        self.assertEqual(hook.wakes(), [])
        self.assertIsNone(self.c.get(record["id"])["nudged"])
        self.assertEqual(len(self.lines), 1)
        self.assertIn(f"heartbeat claim {record['id']}: start failed", self.lines[0])


class NoTextLeaks(Base):
    def test_the_marker_reaches_no_item_line_argv_env_or_file_name(self):
        launch = FakeLaunch()
        with mock.patch.object(claims, "launch", launch), mock.patch.object(claims, "process_table", return_value=[]), \
                mock.patch.object(hook.subprocess, "Popen", side_effect=AssertionError("a launch outside cwd-exec")):
            owner = sid()
            self.c.claim(f"task {MARKER}", owner, str(self.work))
            f0 = sid()
            coordination.append(self.state, coordination.enqueued_fact(
                {"id": f0, "session": sid(), "cwd": str(self.work), "reason": MARKER},
                (datetime.now(timezone.utc) - timedelta(minutes=30)).isoformat()))
            coordination.append(self.state, {"firing": f0, "fact": "finished", "at": "2026-01-01T00:00:00+00:00",
                                             "exit": 1, "output": f"wake-{f0}.json"})
            (self.state / f"wake-{f0}.json").write_text(json.dumps({"is_error": True, "result": MARKER}))
            later = datetime.now(timezone.utc)
            self.beat(later)
            items = hook.wakes()
            self.assertEqual(len(items), 2)  # a nudge and a retry
            hook.wakes(lambda its: [dict(w, at=0) for w in its])
            hook.run_due(home=self.home)
        self.assertNotIn(MARKER, json.dumps(items))
        self.assertNotIn(MARKER, "".join(self.lines))
        self.assertEqual(len(launch.calls), 2)
        for call in launch.calls:
            self.assertEqual(call["held"], [], "a heartbeat launch held a lock")
            self.assertNotIn(MARKER, " ".join(call["argv"]))
            self.assertNotIn(MARKER, json.dumps(call["env"]))
            self.assertNotIn(MARKER, call["prompt"])
        for path in self.state.rglob("*"):
            self.assertNotIn(MARKER, path.name)


if __name__ == "__main__":
    unittest.main()
