"""session.json and the wake's end (Arden's review of the bridge-core audit ceaac74): a torn session.json must not
kill the wake before its turn ends, save_session must never leave a torn file, and failing to record a fresh session
must not cost the reply. Each test fails on b2eb6df.

Self-heal (Arden's review of 06b4ba6): an unreadable session.json, torn or holding anything but an object, is moved
aside and the wake starts a fresh session, said loudly and in the prompt; save_session flushes to disk before the rename.
Each of those tests fails on 06b4ba6."""
import asyncio
import contextlib
import io
import json
import os
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


class RestoresState(unittest.TestCase):
    """br.STATE is module state the wake writes timings under; each test here puts it back (Arden's minor 3)."""
    def setUp(self):
        saved = br.STATE
        self.addCleanup(setattr, br, "STATE", saved)


UNREADABLE = {"torn": '{"id": "abc", "crea', "null": "null", "a list": "[]", "a number": "42"}


class SelfHeal(RestoresState):
    def test_an_unreadable_session_json_is_moved_aside_and_the_wake_starts_fresh(self):
        for what, text in UNREADABLE.items():
            with self.subTest(what):
                seen = {}

                def corrupt(b, store):
                    (store.dir / "session.json").write_text(text, encoding="utf-8")
                    seen["dir"] = store.dir

                out = io.StringIO()
                prompt, session, posted, timings = run({"id": "old", "created": True}, usage_log(1_000),
                                                       reply_by_output=True, rig={"setup": corrupt}, out=out)
                aside = list(seen["dir"].glob("session.json.corrupt-*"))
                self.assertEqual(len(aside), 1, aside)
                self.assertEqual(aside[0].read_text(encoding="utf-8"), text)        # kept for inspection, as it was
                self.assertTrue(session.get("created") and session["id"] != "old", session)   # a fresh session, recorded
                self.assertNotIn("previous", session)
                self.assertIn(f"was unreadable, so it was moved aside to {aside[0].name}", prompt)
                self.assertEqual([p["text"] for p in posted], ["the reply"])
                self.assertIn("SESSION.JSON UNREADABLE", out.getvalue())
                self.assertNotIn("wake could not start", out.getvalue())
                said = [t.get("said") or "" for t in timings if t.get("what") == "rollover"]
                self.assertTrue(any(x.startswith("SESSION.JSON UNREADABLE") for x in said), said)

    def test_a_readable_session_is_not_moved_aside(self):
        seen = {}
        prompt, session, _, _ = run({"id": "old", "created": True}, usage_log(1_000),
                                    rig={"setup": lambda b, store: seen.update(dir=store.dir)})
        self.assertEqual(list(seen["dir"].glob("session.json.corrupt-*")), [])
        self.assertEqual(session, {"id": "old", "created": True})
        self.assertNotIn("moved aside", prompt)

    def test_a_non_object_session_json_reads_as_corrupt(self):
        d = Path(tempfile.mkdtemp())
        for text in ("null", "[]", "42", '"id"'):
            (d / "session.json").write_text(text, encoding="utf-8")
            with self.subTest(text), self.assertRaises(ValueError):
                rc.RoomStore(d).session()


class TornSessionFile(RestoresState):
    def test_a_session_json_that_cannot_be_moved_aside_skips_the_wake_and_still_ends_the_turn(self):
        d = Path(tempfile.mkdtemp())
        (d / "session.json").write_text('{"id": "abc", "crea', encoding="utf-8")
        store = rc.RoomStore(d)

        def stuck():
            raise OSError("read-only file system")

        store.set_aside_session = stuck
        b = br.Bridge({**rc.DEFAULTS, "wake_timeout": 5}, store)
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
        self.assertIn("could not be moved aside (read-only file system)", out.getvalue())
        self.assertIn("wake", [json.loads(l)["what"] for l in (d / "timings.jsonl").read_text().splitlines()])


class AtomicSave(RestoresState):
    def test_the_new_session_is_flushed_to_disk_before_it_is_renamed_over_the_old(self):
        store = rc.RoomStore(Path(tempfile.mkdtemp()))
        events, real_fsync, real_replace = [], os.fsync, os.replace

        def fsync(fd):
            events.append(("fsync", os.readlink(f"/proc/self/fd/{fd}")))
            real_fsync(fd)

        def replace(a, b):
            events.append(("replace", str(a)))
            real_replace(a, b)

        with mock.patch.object(rc.os, "fsync", fsync), mock.patch.object(rc.os, "replace", replace):
            store.save_session({"id": "new", "created": True})
        tmp = next(p for what, p in events if what == "replace")
        self.assertIn(("fsync", tmp), events)
        self.assertLess(events.index(("fsync", tmp)), events.index(("replace", tmp)))
        self.assertEqual(store.session(), {"id": "new", "created": True})

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


class RecordingTheSessionFails(RestoresState):
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
