"""A hook server must not carry the environment of the session or wake that happened to start it (Arden #1109,
2026-11-10): its PERSISTENCE_SPEAKER became every later session's speaker."""
import os
import subprocess
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import hook

WAKE = {"PERSISTENCE_SPEAKER": "Arden (a wake I set from the room)", "PERSISTENCE_ROOM_CHANNEL": "c9",
        "PERSISTENCE_ROOM_SOCKET": "/tmp/x.sock", "PERSISTENCE_ROOM_WAKE": "9f3c", "CLAUDE_CODE_SESSION_ID": "5a0b681f", "PERSISTENCE_SESSION": "5a0b681f"}


class ServerEnvironment(unittest.TestCase):
    def test_ensure_starts_the_server_without_the_sessions_variables(self):
        with mock.patch.dict(os.environ, {**WAKE, "PERSISTENCE_HOOK_PORT": "1", "KEEP_ME": "yes"}), \
                mock.patch("socket.create_connection", side_effect=OSError), \
                mock.patch.object(subprocess, "Popen") as popen:
            hook.ensure()
        env = popen.call_args.kwargs.get("env")
        self.assertIsNotNone(env, "ensure() passed no env: the server inherits the starting session's")
        for key in WAKE:
            self.assertNotIn(key, env)
        self.assertEqual(env.get("KEEP_ME"), "yes")

    def test_serve_drops_them_before_anything_else_so_a_reexec_cannot_hand_them_on(self):
        class Taken:
            def __init__(self, *a, **k):
                raise OSError("port taken")
        with mock.patch.dict(os.environ, WAKE), mock.patch("http.server.ThreadingHTTPServer.__init__", Taken.__init__), \
                mock.patch.object(sys, "stdout", sys.stdout):
            hook.serve()
            for key in WAKE:
                self.assertNotIn(key, os.environ)


if __name__ == "__main__":
    unittest.main()
