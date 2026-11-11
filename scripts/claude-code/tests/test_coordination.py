"""The coordination foundation the spawn claim needs, tested against docs/design/heartbeat.md as merged at 2524fe7:
wakes.lock in the one lock order, write-then-rename and no `[]` on bad JSON under an update ("wakes.json: one lock,
three rewriters"), firing ids ("Firing id"), and the ledger's append and snapshot protocol for the `enqueued` fact
("The ledger of fired wakes"). The heartbeat's own tests (states, retries, reports) are the heartbeat PR's."""
import json
import os
import subprocess
import sys
import tempfile
import threading
import unittest
import uuid
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import hook
import coordination


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.state = Path(self.tmp.name) / "persistence" / "state"
        self.state.mkdir(parents=True)
        self.wakes_path = self.state.parent / "wakes.json"
        self.patches = [mock.patch.object(hook, "STATE", self.state), mock.patch.object(hook, "WAKES", self.wakes_path)]
        for p in self.patches:
            p.start()

    def tearDown(self):
        for p in reversed(self.patches):
            p.stop()
        self.tmp.cleanup()

    def ledger(self):
        return (self.state / "fired.jsonl").read_bytes()


class WakesLock(Base):
    def test_every_rewrite_takes_wakes_lock_and_writes_by_rename(self):
        seen = []
        with mock.patch.object(coordination, "LOCK_OBSERVER", seen.append), \
                mock.patch.object(hook.os, "replace", wraps=os.replace) as replace:
            hook.add_wake({"reason": "r", "in": 1})
        self.assertIn(("acquire", "wakes"), seen)
        self.assertLess(seen.index(("acquire", "wakes")), seen.index(("write", "wakes.json")))
        self.assertLess(seen.index(("write", "wakes.json")), seen.index(("release", "wakes")))
        self.assertTrue(any(str(c.args[1]) == str(self.wakes_path) for c in replace.call_args_list))

    def test_unreadable_json_under_an_update_is_refused_and_left_byte_for_byte(self):
        self.wakes_path.write_text("[{\"reason\": \"half", encoding="utf-8")
        before = self.wakes_path.read_bytes()
        with self.assertRaises(hook.WakesUnreadable):
            hook.wakes(lambda items: items + [{"reason": "new"}])
        self.assertEqual(self.wakes_path.read_bytes(), before)
        reply = hook.add_wake({"reason": "r", "in": 1})
        self.assertNotIn("set", reply)
        self.assertEqual(self.wakes_path.read_bytes(), before)

    def test_a_plain_read_of_a_missing_file_is_empty(self):
        self.assertEqual(hook.wakes(), [])

    def test_concurrent_adders_lose_no_item(self):
        def add(n):
            for i in range(20):
                hook.add_wake({"reason": f"{n}-{i}", "in": 1})
        threads = [threading.Thread(target=add, args=(n,)) for n in range(4)]
        for t in threads:
            t.start()
        for t in threads:
            t.join(30)
        self.assertEqual(len(hook.wakes()), 80)


class FiringIds(Base):
    def test_add_wake_gives_every_new_item_a_fresh_uuid4(self):
        a = hook.add_wake({"reason": "same", "in": 1})["set"]
        b = hook.add_wake({"reason": "same", "in": 1})["set"]
        self.assertEqual(uuid.UUID(a["id"]).version, 4)
        self.assertNotEqual(a["id"], b["id"])
        self.assertEqual([w["id"] for w in hook.wakes()], [a["id"], b["id"]])

    def test_an_item_without_one_gets_one_at_its_first_locked_rewrite_and_keeps_it(self):
        self.wakes_path.write_text(json.dumps([{"reason": "old", "at": 0}]), encoding="utf-8")
        hook.wakes(lambda items: items)
        first = hook.wakes()[0]["id"]
        hook.wakes(lambda items: items)
        self.assertEqual(hook.wakes()[0]["id"], first)
        self.assertEqual(uuid.UUID(first).version, 4)

    def test_a_peer_cannot_write_the_heartbeat_or_claims_keys(self):
        item = hook.add_wake({"reason": "r", "in": 1, "id": "11111111-1111-4111-8111-111111111111", "by": "heartbeat",
                              "claim": str(uuid.uuid4()), "launch": "resume", "retry_of": str(uuid.uuid4()),
                              "follows": str(uuid.uuid4()), "attempt": "retry", "deferral": 1})["set"]
        for key in ("by", "claim", "launch", "retry_of", "follows", "attempt", "deferral"):
            self.assertNotIn(key, item)
        self.assertNotEqual(item["id"], "11111111-1111-4111-8111-111111111111")


class Ledger(Base):
    def fact(self, n=0):
        return {"firing": str(uuid.uuid4()), "fact": "enqueued", "at": f"2026-11-11T21:{n:02d}:00+00:00"}

    def test_append_writes_one_whole_line_under_fired_lock(self):
        seen = []
        with mock.patch.object(coordination, "LOCK_OBSERVER", seen.append):
            coordination.append(self.state, self.fact())
        self.assertEqual([e for e in seen if e[0] != "write"], [("acquire", "fired"), ("release", "fired")])
        data = self.ledger()
        self.assertTrue(data.endswith(b"\n"))
        self.assertEqual(data.count(b"\n"), 1)
        json.loads(data)

    def test_fired_lock_is_a_leaf_and_last(self):
        with coordination.lock(self.state, "fired"):
            for other in ("heartbeat", "wakes", "tasks", "fired"):
                with self.subTest(other=other), self.assertRaises(coordination.LockOrderError):
                    with coordination.lock(self.state, other):
                        pass
        with coordination.lock(self.state, "wakes"), coordination.lock(self.state, "tasks"), coordination.lock(self.state, "fired"):
            pass

    def test_fired_lock_is_a_leaf_whatever_the_order_puts_after_it(self):
        """Arden's mutation M (1570199516513046549): the leaf rule is its own rule, not a consequence of fired being
        last in today's order. With a lock ordered after it, taking that lock while holding fired.lock is still refused."""
        with mock.patch.object(coordination, "LOCK_ORDER", coordination.LOCK_ORDER + ("later",)), \
                mock.patch.dict(coordination.LOCK_FILES, {"later": "later.lock"}):
            with coordination.lock(self.state, "tasks"), coordination.lock(self.state, "later"):
                pass  # the order alone allows it
            with coordination.lock(self.state, "fired"):
                with self.assertRaisesRegex(coordination.LockOrderError, "leaf"):
                    with coordination.lock(self.state, "later"):
                        pass
            self.assertEqual(coordination.held_locks(), [])

    def test_a_short_write_is_cut_back_and_fails_loudly(self):
        coordination.append(self.state, self.fact(1))
        before = self.ledger()
        with mock.patch.object(coordination, "_write", side_effect=lambda fd, data: os.write(fd, data[:7])):
            with self.assertRaises(coordination.LedgerError):
                coordination.append(self.state, self.fact(2))
        self.assertEqual(self.ledger(), before)

    def test_a_partial_tail_is_moved_aside_verbatim_and_the_file_cut_to_its_last_newline(self):
        coordination.append(self.state, self.fact(1))
        good = self.ledger()
        with open(self.state / "fired.jsonl", "ab") as f:
            f.write(b'{"firing": "half')
        facts, complete = coordination.snapshot(self.state)
        self.assertFalse(complete)
        self.assertEqual(len(facts), 1)
        self.assertEqual(self.ledger(), good)
        self.assertEqual((self.state / f"fired.partial-{len(good)}").read_bytes(), b'{"firing": "half')
        self.assertEqual(coordination.snapshot(self.state), (facts, True))

    def test_an_appender_repairs_the_same_tail_before_appending(self):
        coordination.append(self.state, self.fact(1))
        good = self.ledger()
        with open(self.state / "fired.jsonl", "ab") as f:
            f.write(b'{"firing": "half')
        coordination.append(self.state, self.fact(2))
        lines = self.ledger().split(b"\n")
        self.assertEqual(len(lines), 3)
        for line in lines[:2]:
            json.loads(line)
        self.assertTrue((self.state / f"fired.partial-{len(good)}").exists())

    def test_a_bad_line_that_is_not_the_tail_is_corruption(self):
        coordination.append(self.state, self.fact(1))
        with open(self.state / "fired.jsonl", "ab") as f:
            f.write(b"not json\n")
        coordination.append(self.state, self.fact(2))
        with self.assertRaises(coordination.LedgerError):
            coordination.snapshot(self.state)

    def test_concurrent_writer_processes_leave_only_whole_lines(self):
        code = ("import sys, uuid; sys.path.insert(0, sys.argv[2]); import coordination\n"
                "for i in range(100): coordination.append(sys.argv[1], {'firing': str(uuid.uuid4()), 'fact': 'started', 'at': str(i)})")
        here = str(Path(coordination.__file__).resolve().parent)
        procs = [subprocess.Popen([sys.executable, "-c", code, str(self.state), here]) for _ in range(6)]
        snaps = []
        while any(p.poll() is None for p in procs):
            snaps.append(coordination.snapshot(self.state)[1])
        for p in procs:
            self.assertEqual(p.wait(60), 0)
        self.assertTrue(all(snaps))
        lines = self.ledger().split(b"\n")
        self.assertEqual(lines[-1], b"")
        self.assertEqual(len(lines) - 1, 600)
        for line in lines[:-1]:
            json.loads(line)

    def test_the_enqueued_fact_carries_structure_and_never_the_reason(self):
        item = {"id": str(uuid.uuid4()), "reason": "MARKER delete everything", "session": str(uuid.uuid4()), "at": 5.0,
                "cwd": "/home/x/work", "work": True, "claim": str(uuid.uuid4()), "set": "2026-11-11T21:00:00+00:00"}
        fact = coordination.enqueued_fact(item, "2026-11-11T21:01:00+00:00")
        self.assertEqual(fact["firing"], item["id"])
        self.assertEqual(fact["fact"], "enqueued")
        self.assertEqual(fact["at"], "2026-11-11T21:01:00+00:00")
        self.assertEqual(fact["item_at"], 5.0)
        self.assertEqual((fact["session"], fact["cwd"], fact["claim"], fact["work"]), (item["session"], item["cwd"], item["claim"], True))
        self.assertEqual(set(fact), {"firing", "fact", "at"} | set(coordination.ENQUEUED_FIELDS))
        self.assertNotIn("MARKER", json.dumps(fact))
        self.assertEqual(len(fact["reason_sha256"]), 64)



class CopyOnlyInstall(unittest.TestCase):
    """Arden 1570199516513046549 point 3: hook.py copied alone (a Codex install made before coordination.py existed,
    or John's PC's) still imports, so session-start and recall still run; only what takes a lock needs coordination.py."""

    def test_hook_py_alone_imports_and_reads_the_queue(self):
        import shutil
        import subprocess

        with tempfile.TemporaryDirectory() as d:
            shutil.copy(Path(hook.__file__), d)
            code = ("import sys; sys.path.insert(0, sys.argv[1]); import hook; "
                    "assert 'coordination' not in sys.modules, 'imported at module level'; "
                    "assert hook.wakes() == []; print('ok')")
            done = subprocess.run([sys.executable, "-I", "-c", code, d], capture_output=True, text=True, timeout=60,
                                  env={**os.environ, "PERSISTENCE_STATE_DIR": str(Path(d) / "state")})
        self.assertEqual((done.returncode, done.stdout.strip()), (0, "ok"), done.stderr)


if __name__ == "__main__":
    unittest.main()
