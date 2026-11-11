"""Store commands from a room wake (scripts/codex/room_store.py; Ember's five changes, 2026-11-11): an exact list of
commands and fields, requests parsed as data and run as an argv list with no shell, an audit log with no content, a
600 results queue deleted only after delivery. Each refusal here is a test that fails if the refusal is taken out."""
import json
import os
import stat
import socket
import threading
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import room_store as rs  # noqa: E402

SECRET = "a private thought about the bees"


class Argv(unittest.TestCase):
    def ok(self, request, argv):
        self.assertEqual(rs.to_argv(request), argv)

    def refused(self, request, why=""):
        with self.assertRaises(rs.Refused) as e:
            rs.to_argv(request)
        self.assertIn(why, str(e.exception))

    def test_the_allowed_forms(self):
        self.ok({"cmd": "note", "content": "here"}, ["note", "--content", "here"])
        self.ok({"cmd": "add", "type": "Personal", "tag": ["not-mine", "peers/john"], "content": "x", "importance": 0.6},
                ["add", "--type", "Personal", "--content", "x", "--importance", "0.6", "--tag", "not-mine", "--tag", "peers/john"])
        self.ok({"cmd": "show", "id": 12}, ["show", "12"])
        self.ok({"cmd": "supersede", "ids": [3, 4], "content": "one view", "reason": "merged"},
                ["supersede", "3,4", "--content", "one view", "--reason", "merged"])
        self.ok({"cmd": "split", "id": 5, "parts": ["a", "b"]}, ["split", "5", "--content", "a", "--content", "b"])
        self.ok({"cmd": "recall", "text": "bees", "limit": 3}, ["recall", "--text", "bees", "--limit", "3"])
        self.ok({"cmd": "context show", "path": "project/x"}, ["context", "show", "project/x"])
        self.ok({"cmd": "cue set", "name": "prompt/bees", "when": "bee"}, ["cue", "set", "prompt/bees", "--when", "bee"])
        self.ok({"cmd": "forget", "id": 9, "reason": "slip"}, ["forget", "9", "--reason", "slip"])
        self.ok({"cmd": "list", "archived": True}, ["list", "--archived"])

    def test_text_that_looks_like_an_option_stays_the_value(self):
        # The CLI takes the token after --content as its value whatever it starts with.
        self.ok({"cmd": "note", "content": "--db /etc/passwd"}, ["note", "--content", "--db /etc/passwd"])

    def test_commands_not_on_the_list(self):
        for cmd in ("serve", "sleep-begin", "sleep-end", "embed-conversation", "send", "keep-conversation", "core", "whoami",
                    "concept add", "concept kind", "context", "cue", "rm", "note; rm -rf ~", None, 3, ["note"]):
            self.refused({"cmd": cmd, "content": "x"}, "not on the list")

    def test_fields_not_on_the_list(self):
        self.refused({"cmd": "note", "content": "x", "db": "/tmp/other.db"}, "unknown field")
        self.refused({"cmd": "add", "type": "Personal", "content": "x", "content-file": "/etc/passwd"}, "unknown field")
        self.refused({"cmd": "add", "type": "Personal", "content": "x", "content_file": "/etc/passwd"}, "unknown field")
        self.refused({"cmd": "show", "id": 1, "socket": "/home/claude.sock"}, "unknown field")
        self.refused({"cmd": "show", "id": 1, "--db": "x"}, "unknown field")
        self.refused({"cmd": "show", "id": 1, "extra": [2]}, "unknown field")
        self.refused({"cmd": "recall", "text": "x", "before": "now"}, "unknown field")

    def test_values(self):
        self.refused({"cmd": "show", "id": "/etc/passwd"}, "positive whole number")
        self.refused({"cmd": "show", "id": "12"}, "positive whole number")
        self.refused({"cmd": "show", "id": True}, "positive whole number")
        self.refused({"cmd": "show", "id": -1}, "positive whole number")
        self.refused({"cmd": "note", "content": "-"}, "standard input")
        self.refused({"cmd": "note", "content": ""}, "non-empty")
        self.refused({"cmd": "note", "content": "x" * (rs.MAX_TEXT + 1)}, "over")
        self.refused({"cmd": "add", "type": "Secret", "content": "x"}, "one of")
        self.refused({"cmd": "add", "type": "Personal", "content": "x", "tag": ["--db"]}, "tag path")
        self.refused({"cmd": "add", "type": "Personal", "content": "x", "tag": ["../../etc"]}, "tag path")
        self.refused({"cmd": "add", "type": "Personal", "content": "x", "tag": "one"}, "list")
        self.refused({"cmd": "add", "type": "Personal", "content": "x", "importance": 2}, "0 to 1")
        self.refused({"cmd": "add", "type": "Personal", "content": "x", "importance": float("nan")}, "0 to 1")
        self.refused({"cmd": "add", "type": "Personal", "content": "x", "project": "p", "everywhere": True}, "together")
        self.refused({"cmd": "search", "words": "--db /x"}, "start with '-'")
        self.refused({"cmd": "concept show", "name": "-x"}, "start with '-'")
        self.refused({"cmd": "cue set", "name": "boot/x", "when": "y"}, "prompt|tool|stop")
        self.refused({"cmd": "list", "limit": 10_000}, "0 to 50")
        self.refused({"cmd": "list", "archived": "yes"}, "true")
        self.refused({"cmd": "edit", "id": 3}, "nothing to change")
        self.refused({"cmd": "note"}, "needs content")
        self.refused({"cmd": "split", "id": 1, "parts": ["only one"]}, "2 to 20")
        self.refused({"cmd": "concept reject", "fact": 4}, "needs reason")
        self.refused(["note", "--content", "x"], "JSON object")


class Blocks(unittest.TestCase):
    def test_blocks_come_out_of_the_reply(self):
        reply = "Hello.\n\n```store\n{\"cmd\": \"note\", \"content\": \"n\"}\n\n```\nAfter.\n```store\n{\"cmd\": \"show\", \"id\": 1}\n```"
        kept, lines = rs.take_blocks(reply)
        self.assertEqual(kept, "Hello.\n\nAfter.")
        self.assertEqual(lines, ['{"cmd": "note", "content": "n"}', '{"cmd": "show", "id": 1}'])

    def test_an_unclosed_block_is_never_posted(self):
        kept, lines = rs.take_blocks("Hi.\n```store\n{\"cmd\": \"note\", \"content\": \"" + SECRET + "\"}")
        self.assertEqual(kept, "Hi.")
        self.assertEqual(len(lines), 1)

    def test_other_fences_stay(self):
        reply = "```text\n{\"cmd\": \"note\"}\n```"
        self.assertEqual(rs.take_blocks(reply), (reply, []))


class Queue(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="room-store-test-"))
        self.patch = mock.patch.object(rs, "state_dir", lambda: self.dir)
        self.patch.start()
        self.calls = []

        def fake(argv):
            self.calls.append(argv)
            return 0, f"#{len(self.calls)} {SECRET}", ""
        self.run_patch = mock.patch.object(rs, "run_store", fake)
        self.run_patch.start()

    def tearDown(self):
        mock.patch.stopall()

    def mode(self, p):
        return stat.S_IMODE(os.stat(p).st_mode)

    def reply(self, text):
        f = self.dir / "reply.txt"
        f.write_text(text)
        return f

    def test_run_strips_runs_queues_and_logs_no_content(self):
        lines = [json.dumps({"cmd": "note", "content": SECRET}), json.dumps({"cmd": "recall", "text": SECRET}),
                 json.dumps({"cmd": "serve", "socket": "/x"}), "not json", json.dumps({"cmd": "show", "id": 7})]
        f = self.reply("Visible words.\n```store\n" + "\n".join(lines) + "\n```\n")
        rs.cmd_run(str(f))
        self.assertEqual(f.read_text(), "Visible words.\n")
        self.assertEqual([c[0] for c in self.calls], ["note", "recall", "show"])
        q = self.dir / "store-results"
        self.assertEqual(self.mode(q), 0o700)
        results = sorted(q.glob("*.json"))
        self.assertEqual(len(results), 5)
        for r in results:
            self.assertEqual(self.mode(r), 0o600)
        shown = [json.loads(r.read_text()) for r in results]
        self.assertEqual([s["ok"] for s in shown], [True, True, False, False, True])
        self.assertIn("refused", shown[2]["result"])
        log = self.dir / "store-commands.log"
        self.assertEqual(self.mode(log), 0o600)
        text = log.read_text()
        self.assertNotIn(SECRET, text)
        self.assertNotIn("/x", text)
        self.assertNotIn("not json", text)
        entries = [json.loads(x) for x in text.splitlines()]
        self.assertEqual([e["cmd"] for e in entries], ["note", "recall", "refused", "refused", "show"])
        self.assertEqual(entries[0]["text_len"], len(SECRET))
        self.assertEqual(entries[4]["ids"], [7])
        self.assertTrue(set(entries[0]) <= {"ts", "cmd", "ok", "ids", "text_len", "why"})

    def test_cap(self):
        lines = [json.dumps({"cmd": "show", "id": i}) for i in range(1, rs.MAX_COMMANDS + 3)]
        rs.cmd_run(str(self.reply("```store\n" + "\n".join(lines) + "\n```")))
        self.assertEqual(len(self.calls), rs.MAX_COMMANDS)
        over = [json.loads(r.read_text()) for r in sorted((self.dir / "store-results").glob("*.json"))][-2:]
        self.assertTrue(all("cap" in o["result"] and not o["ok"] for o in over))

    def prompt(self, manifest, text="the room\n"):
        with mock.patch.object(sys, "stdin", __import__("io").StringIO(text)), \
             mock.patch.object(sys, "stdout", __import__("io").StringIO()) as out:
            rs.cmd_prompt(str(manifest))
        return out.getvalue()

    def test_results_go_in_front_of_the_next_wake_and_are_deleted_only_after_it(self):
        rs.cmd_run(str(self.reply("```store\n" + json.dumps({"cmd": "show", "id": 3}) + "\n```")))
        manifest = self.dir / "manifest"
        first = self.prompt(manifest)
        self.assertIn(SECRET, first)
        self.assertTrue(first.endswith("the room\n"))
        self.assertEqual(self.mode(manifest), 0o600)
        # The wake failed (no `delivered`): the next one gets them again.
        self.assertIn(SECRET, self.prompt(manifest))
        # A result queued during the wake isn't in its manifest, so it survives the delete.
        rs.cmd_run(str(self.reply("```store\n" + json.dumps({"cmd": "show", "id": 4}) + "\n```")))
        rs.cmd_delivered(str(manifest))
        left = [json.loads(r.read_text()) for r in (self.dir / "store-results").glob("*.json")]
        self.assertEqual(len(left), 1)
        self.assertIn("#2", left[0]["result"])
        self.assertEqual(self.prompt(self.dir / "m2", "x").count(SECRET), 1)

    def test_no_results_no_preamble(self):
        self.assertEqual(self.prompt(self.dir / "m", "just the room"), "just the room")

    def test_delivered_deletes_only_queue_names(self):
        outside = self.dir / "keep.json"
        outside.write_text("{}")
        m = self.dir / "m"
        m.write_text("../keep.json\nkeep.json\n")
        rs.cmd_delivered(str(m))
        self.assertTrue(outside.exists())


class NoShell(unittest.TestCase):
    def test_the_request_goes_as_data_to_the_users_own_socket(self):
        path = Path(tempfile.mkdtemp(prefix="rs-sock-")) / "s.sock"
        got = {}
        server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        server.bind(str(path))
        server.listen(1)

        def answer():
            conn, _ = server.accept()
            with conn:
                data = b""
                while not data.endswith(b"\n"):
                    data += conn.recv(4096)
                got.update(json.loads(data))
                conn.sendall(b'{"code": 0, "out": "shown", "err": ""}\n')
        t = threading.Thread(target=answer)
        t.start()
        with mock.patch.object(rs, "socket_path", lambda: str(path)):
            self.assertEqual(rs.run_store(["note", "--content", "--db x"]), (0, "shown", ""))
        t.join(5)
        server.close()
        self.assertEqual(got, {"args": ["note", "--content", "--db x"], "stdin": ""})

    def test_socket_is_the_running_users_own(self):
        import pwd
        me = pwd.getpwuid(os.getuid())
        with mock.patch.dict(os.environ, {"PERSISTENCE_STORE_SOCKET": "/elsewhere.sock", "HOME": "/tmp"}):
            self.assertEqual(rs.socket_path(), f"{me.pw_dir}/store/{me.pw_name}.sock")

    def test_no_shell_or_eval_in_the_source(self):
        src = (Path(rs.__file__)).read_text()
        for bad in ("subprocess", "shell=True", "eval(", "exec(", "os.system", "os.popen", "sh -c", "shlex"):
            self.assertNotIn(bad, src)


if __name__ == "__main__":
    unittest.main()
