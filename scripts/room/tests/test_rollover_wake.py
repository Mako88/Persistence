"""The room session's rollover as the bridge wires it (Arden's re-check, 1570124167423070391): a wake run end to end
with a fake command in place of claude, so a bridge that never prepends the notes, never records the session replaced,
or drops the reply on a malformed log fails here, not only the roomcore functions it calls."""
import asyncio
import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import roomcore as rc
import bridge as br
from test_http_self import FakeRoom, FakeUser, m
from test_roomcore import usage_log

# The fake wake: keeps its prompt, writes a reply, and prints a usage line as `claude -p --output-format json` does.
FAKE = """import sys, json
prompt = sys.stdin.read()
open(sys.argv[1], "w").write(prompt)
open(sys.argv[2], "w").write("the reply")
print(sys.argv[3])
"""


def run(session, log_line, reply_by_output=False, rig=None, out=None):
    """One wake through Bridge.run_wake with the default claude session's argv swapped for the fake; the store's
    session, wake_argv's own choice of resume or fresh id, and everything around it are the real ones."""
    d = Path(tempfile.mkdtemp())
    (d / "fake.py").write_text(FAKE)
    store = rc.RoomStore(d)
    store.save_session(session)
    config = {**rc.DEFAULTS, "fresh_session_above_tokens": 150_000, "reply_by_output": reply_by_output, "wake_timeout": 20}
    b = br.Bridge(config, store)
    b.client = type("C", (), {"user": FakeUser()})()
    b.room = FakeRoom()
    posted = []

    async def say(req):
        posted.append(req)
        if rig:
            rig["events"].append("post")
        return {"ok": True}

    async def mark(turns, which):
        pass

    b.say, b.mark = say, mark
    br.STATE = d
    real = rc.wake_argv

    def fake_argv(cfg, st, session_id=None, reply_file=None):
        argv, new = real(cfg, st, session_id, reply_file)
        return [sys.executable, str(d / "fake.py"), str(d / "prompt.txt"), str(reply_file), log_line], new

    rc.wake_argv = fake_argv
    real_after = rc.after_wake
    if rig:
        rig["events"] = []
        rig.get("setup", lambda b, store: None)(b, store)

        def after(*a):
            rig["events"].append("after_wake")
            return rig.get("after_wake", real_after)(*a)

        rc.after_wake = after
    try:
        with contextlib.redirect_stdout(out if out is not None else io.StringIO()):
            asyncio.run(b.run_wake(rc.Wake(triggers=[m(7)], unread=[m(7)], channel_id="c1", speaker="x", to_mark=[m(7)])))
    finally:
        rc.wake_argv = real
        rc.after_wake = real_after
    timings = [json.loads(l) for l in (d / "timings.jsonl").read_text().splitlines()] if (d / "timings.jsonl").exists() else []
    return (d / "prompt.txt").read_text(), store.session(), posted, timings


class RolloverWiring(unittest.TestCase):
    def test_the_last_wake_is_told_so_in_its_prompt(self):
        prompt, saved, _, _ = run({"id": "old", "created": True, "last": True, "context": 151_000}, usage_log(152_000))
        self.assertTrue(prompt.startswith("This is the last wake in this session"))
        self.assertTrue(saved.get("rotate"))

    def test_the_fresh_session_is_told_what_it_replaced_and_records_it(self):
        prompt, saved, _, timings = run({"id": "old", "created": True, "rotate": True, "context": 152_000}, usage_log(1_000))
        self.assertIn(f"rollover: fresh room session {saved['id']} replaced old",
                      [t.get("said") for t in timings if t.get("what") == "rollover"])
        self.assertTrue(prompt.startswith("This is a fresh room session"))
        self.assertIn("old", prompt)
        self.assertEqual(saved.get("previous"), "old")
        self.assertNotEqual(saved["id"], "old")

    def test_a_malformed_log_still_posts_the_reply(self):
        _, saved, posted, timings = run({"id": "old", "created": True}, json.dumps({"usage": {"iterations": {"x": 1}}}),
                                        reply_by_output=True)
        self.assertEqual([p["text"] for p in posted], ["the reply"])
        self.assertEqual(saved, {"id": "old", "created": True})
        self.assertIn("size unreadable: no rollover", [t.get("said") for t in timings if t.get("what") == "rollover"])

    def test_each_rollover_step_is_logged(self):
        _, _, _, timings = run({"id": "old", "created": True}, usage_log(151_000))
        steps = [t for t in timings if t.get("what") == "rollover"]
        self.assertEqual(len(steps), 1); self.assertIn("last wake", steps[0]["said"])
        _, _, _, timings = run({"id": "old", "created": True, "last": True, "context": 151_000}, usage_log(152_000))
        self.assertIn("fresh session", [t for t in timings if t.get("what") == "rollover"][0]["said"])

    def test_a_raising_after_wake_runs_after_the_reply_and_costs_nothing(self):
        """Arden's review of 2e9676f: after_wake is pinned after post_reply, and its failing loses neither the reply nor
        the wake's end."""
        def boom(*a):
            raise RuntimeError("boom")

        rig = {"after_wake": boom}
        _, _, posted, timings = run({"id": "old", "created": True}, usage_log(1_000), reply_by_output=True, rig=rig)
        self.assertEqual(rig["events"], ["post", "after_wake"])
        self.assertEqual([p["text"] for p in posted], ["the reply"])
        self.assertIn("wake", [t.get("what") for t in timings])
        self.assertIn("rollover check failed: boom", [t.get("said") for t in timings if t.get("what") == "rollover"])

    def test_an_oserror_saving_the_session_is_not_called_a_failed_start(self):
        """Arden's review of 2e9676f: an OSError from save_session inside after_wake is the rollover failing, not the
        wake failing to start."""
        def broken_disk(b, store):
            def save(d):
                raise OSError("disk full")
            store.save_session = save

        out = io.StringIO()
        _, _, posted, timings = run({"id": "old", "created": True}, usage_log(151_000), reply_by_output=True,
                                    rig={"setup": broken_disk}, out=out)
        self.assertEqual([p["text"] for p in posted], ["the reply"])
        self.assertNotIn("wake could not start", out.getvalue())
        self.assertIn("rollover check failed: disk full", [t.get("said") for t in timings if t.get("what") == "rollover"])


if __name__ == "__main__":
    unittest.main()
