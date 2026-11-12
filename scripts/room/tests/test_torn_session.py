"""session.json and the wake's end (Arden's review of the bridge-core audit ceaac74): a torn session.json must not
kill the wake before its turn ends, save_session must never leave a torn file, and failing to record a fresh session
must not cost the reply. Each test fails on b2eb6df."""
import asyncio
import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import roomcore as rc
import bridge as br
from test_http_self import FakeRoom, FakeUser, m
from test_roomcore import usage_log
from test_rollover_wake import run


class TornSessionFile(unittest.TestCase):
    def test_a_torn_session_json_skips_the_wake_and_still_ends_the_turn(self):
        d = Path(tempfile.mkdtemp())
        (d / "session.json").write_text('{"id": "abc", "crea', encoding="utf-8")
        b = br.Bridge({**rc.DEFAULTS, "wake_timeout": 5}, rc.RoomStore(d))
        b.client = type("C", (), {"user": FakeUser()})()
        b.room = FakeRoom()

        async def mark(turns, which):
            pass

        b.mark = mark
        br.STATE = d
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            asyncio.run(b.run_wake(rc.Wake(triggers=[m(7)], unread=[m(7)], channel_id="c1", speaker="x", to_mark=[m(7)])))
        self.assertTrue(b.room.finished)
        self.assertIsNone(b.wake_token)
        self.assertIn("wake could not start: session.json is unreadable", out.getvalue())
        self.assertIn("wake", [json.loads(l)["what"] for l in (d / "timings.jsonl").read_text().splitlines()])


class AtomicSave(unittest.TestCase):
    def test_a_write_that_fails_halfway_leaves_the_old_session_whole(self):
        store = rc.RoomStore(Path(tempfile.mkdtemp()))
        store.save_session({"id": "old", "created": True})
        real = Path.write_text

        def half(self, data, *a, **k):
            real(self, data[: len(data) // 2], *a, **k)
            raise OSError("disk full")

        with mock.patch.object(Path, "write_text", half), self.assertRaises(OSError):
            store.save_session({"id": "new", "created": True, "previous": "old"})
        self.assertEqual(store.session(), {"id": "old", "created": True})
        store.save_session({"id": "new", "created": True})
        self.assertEqual(store.session(), {"id": "new", "created": True})


class RecordingTheSessionFails(unittest.TestCase):
    def test_an_oserror_recording_a_fresh_session_still_posts_the_reply(self):
        def broken_disk(b, store):
            def save(d):
                raise OSError("disk full")
            store.save_session = save

        out = io.StringIO()
        _, _, posted, timings = run({"id": "old", "created": True, "rotate": True}, usage_log(1_000),
                                    reply_by_output=True, rig={"setup": broken_disk}, out=out)
        self.assertEqual([p["text"] for p in posted], ["the reply"])
        self.assertNotIn("wake could not start", out.getvalue())
        said = [t.get("said") for t in timings if t.get("what") == "rollover"]
        self.assertTrue(any(s and s.startswith("could not record room session") and "disk full" in s for s in said), said)


if __name__ == "__main__":
    unittest.main()
