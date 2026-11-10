"""A room session must not take the work inbox (2026-11-10): it runs in ~/work, was shown the messages sent to `work`
and archived them, and it can only speak, read and keep, so no working session ever saw them (#15014, #15018)."""
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import hook

LISTED = "[#1 | Personal | i:0.3 c:1 | tags: inbox/work]\nFrom session aaaaaaaa (work), 2026-11-10 14:11Z, for work:\ndrop 8095\n"


class RoomInbox(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.state = mock.patch.object(hook, "STATE", Path(self.tmp.name))
        self.state.start()
        self.calls = []
        def store(*args, **kwargs):
            self.calls.append(args)
            return LISTED if args[0] == "list" else ""
        self.store = mock.patch.object(hook, "store", side_effect=store)
        self.store.start()
        self.project = mock.patch.object(hook, "project", return_value="work")
        self.project.start()
        self.remote = mock.patch.object(hook, "remote", return_value=None)  # no machine topic, whatever this box has
        self.remote.start()
        env = {k: v for k, v in os.environ.items() if k != "PERSISTENCE_ROOM_CHANNEL"}
        self.env = mock.patch.dict(os.environ, env, clear=True)
        self.env.start()

    def tearDown(self):
        for p in (self.env, self.remote, self.project, self.store, self.state):
            p.stop()
        self.tmp.cleanup()

    def archived(self):
        return [a for a in self.calls if a[0] == "archive"]

    def test_a_working_session_is_shown_the_message_and_archives_it(self):
        self.assertIn("drop 8095", hook.inbox({"session_id": "bbbbbbbb-1"}))
        self.assertEqual(self.archived(), [("archive", "1")])

    def test_a_room_session_marked_at_its_start_leaves_it_for_the_hook_server_too(self):
        event = {"session_id": "cccccccc-1", "source": "resume"}
        with mock.patch.dict(os.environ, {"PERSISTENCE_ROOM_CHANNEL": "1569586907955073130"}):
            hook.note_room_session(event)
            self.assertEqual(hook.inbox(event), "")
        # the hook server, which answers UserPromptSubmit, has no session environment: the mark must carry it
        self.assertNotIn("PERSISTENCE_ROOM_CHANNEL", os.environ)
        self.assertEqual(hook.inbox(event), "")
        self.assertEqual(self.archived(), [])


if __name__ == "__main__":
    unittest.main()
