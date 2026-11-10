"""The Codex adapter (Ember's trial, step 3, 2026-11-10): a Codex session log is not Claude Code's transcript, so without
the adapter the store keeps nothing of a Codex conversation. The log lines below are shaped as Codex 0.144.1 wrote them
on home (a probe session's rollout), with made-up words."""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

TMP = tempfile.mkdtemp(prefix="codex-hook-test-")
os.environ["PERSISTENCE_CODEX_CONFIG"] = str(Path(TMP) / "none.json")  # never the running user's own config
os.environ["PERSISTENCE_STATE_DIR"] = str(Path(TMP) / "state")
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import codex_hook  # noqa: E402
hook = codex_hook.hook

SESSION = "01a1c74c-4476-7a52-b702-f82b512be7fb"
TURN = "01a1c74c-4523-7233-8aef-9c16ea235bf8"


def rollout(reply=True):
    lines = [
        {"timestamp": "2026-11-10T21:10:26.855Z", "type": "session_meta", "payload": {"id": SESSION, "cwd": "/tmp"}},
        {"timestamp": "2026-11-10T21:10:26.856Z", "type": "event_msg", "payload": {"type": "task_started", "turn_id": TURN}},
        {"timestamp": "2026-11-10T21:10:27.991Z", "type": "response_item", "payload": {"type": "message", "role": "developer",
         "content": [{"type": "input_text", "text": "<permissions instructions>sandbox</permissions instructions>"}]}},
        {"timestamp": "2026-11-10T21:10:27.992Z", "type": "response_item", "payload": {"type": "message", "role": "user",
         "content": [{"type": "input_text", "text": "<recommended_plugins>a list</recommended_plugins>"}]}},
        {"timestamp": "2026-11-10T21:10:27.992Z", "type": "turn_context", "payload": {"turn_id": TURN, "cwd": "/tmp"}},
        {"timestamp": "2026-11-10T21:10:28.019Z", "type": "response_item", "payload": {"type": "message", "role": "developer",
         "content": [{"type": "input_text", "text": "# Who you are\n\nthe core"}]}},
        {"timestamp": "2026-11-10T21:10:28.042Z", "type": "response_item", "payload": {"type": "message", "role": "user",
         "content": [{"type": "input_text", "text": "Who keeps the bees?\n"}]}},
        {"timestamp": "2026-11-10T21:10:28.042Z", "type": "event_msg", "payload": {"type": "user_message", "message": "Who keeps the bees?\n", "images": []}},
    ]
    if reply:
        lines += [
            {"timestamp": "2026-11-10T21:10:30.142Z", "type": "event_msg", "payload": {"type": "agent_message", "message": "Marlo does.", "phase": "final_answer"}},
            {"timestamp": "2026-11-10T21:10:30.142Z", "type": "response_item", "payload": {"type": "message", "role": "assistant",
             "id": "msg_1", "content": [{"type": "output_text", "text": "Marlo does."}]}},
        ]
    lines.append({"timestamp": "2026-11-10T21:10:30.168Z", "type": "event_msg", "payload": {"type": "task_complete", "turn_id": TURN}})
    return "\n".join(json.dumps(x) for x in lines) + "\n"


class Conversation(unittest.TestCase):
    def test_keeps_the_words_said_and_nothing_the_harness_injected(self):
        out = [json.loads(x) for x in codex_hook.conversation(rollout(), SESSION)]
        self.assertEqual([(x["type"], x["message"]["content"]) for x in out],
                         [("user", "Who keeps the bees?"), ("assistant", "Marlo does.")])
        self.assertEqual(out[0]["timestamp"], "2026-11-10T21:10:28.042Z")

    def test_a_reply_the_log_lacks_yet_comes_from_the_stop_payload_once(self):
        lagging = [json.loads(x) for x in codex_hook.conversation(rollout(reply=False), SESSION, (TURN, "Marlo does."))]
        caught_up = [json.loads(x) for x in codex_hook.conversation(rollout(), SESSION, (TURN, "Marlo does."))]
        self.assertEqual([x["message"]["content"] for x in lagging], ["Who keeps the bees?", "Marlo does."])
        self.assertEqual(len(caught_up), 2)  # not added again when the log has it
        self.assertEqual(lagging[1]["uuid"], caught_up[1]["uuid"])  # the same id either way: the store keeps it once

    def test_the_session_is_the_one_the_log_is_named_for(self):
        self.assertEqual(codex_hook.session_of(f"/x/rollout-2026-11-10T16-10-26-{SESSION}.jsonl.zst"), SESSION)

    def test_a_compressed_log_reads_the_same(self):
        if not shutil.which("zstd"):
            self.skipTest("no zstd")
        plain = Path(TMP) / f"rollout-t-{SESSION}.jsonl"
        plain.write_text(rollout(), encoding="utf-8")
        subprocess.run(["zstd", "-qf", str(plain)], check=True)
        self.assertEqual(codex_hook.read_log(str(plain) + ".zst"), rollout())


class Hooks(unittest.TestCase):
    def test_session_start_puts_the_core_in_front_as_codex_reads_it(self):
        with mock.patch.object(hook, "core", return_value="[#1 | Identity | i:0.9 | 2026-11-10]\nI am Tessel.\n"), \
             mock.patch.object(hook, "record_speaker"):
            out = codex_hook.session_start({"session_id": "s1", "cwd": TMP, "source": "startup"})
        self.assertEqual(out["hookSpecificOutput"]["hookEventName"], "SessionStart")
        self.assertIn("I am Tessel.", out["hookSpecificOutput"]["additionalContext"])

    def test_a_failing_hook_never_breaks_the_session(self):
        import io
        with mock.patch.object(codex_hook, "session_start", side_effect=RuntimeError("store gone")):
            self.assertEqual(codex_hook.main(["session-start"], io.StringIO("{}")), 0)


STORE = os.environ.get("PERSISTENCE_STORE_EXE") or "/opt/persistence/store/persistence-store"


@unittest.skipUnless(Path(STORE).exists(), "needs the published persistence-store")
class AgainstTheRealStore(unittest.TestCase):
    """The store's own transcript reader, on a throwaway database: the Codex log as it is keeps nothing; through the
    adapter, both sides of the turn are kept, under the session's id, once."""

    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="codex-store-", dir=TMP))
        self.log = self.dir / f"rollout-2026-11-10T16-10-26-{SESSION}.jsonl"
        self.log.write_text(rollout(), encoding="utf-8")
        self.env = {**os.environ, "PERSISTENCE_STORE_DB": str(self.dir / "t.db"), "PERSISTENCE_PEERNAME": "Tessel",
                    "PERSISTENCE_EMBEDDING_ENABLED": "false"}
        for name in ("PERSISTENCE_STORE_SOCKET", "PERSISTENCE_SPEAKER", "PERSISTENCE_SESSION", "CLAUDE_CODE_SESSION_ID"):
            self.env.pop(name, None)  # the running session's, not the test's
        self.env["PERSISTENCE_SELECTEDLOCALPEER"] = "John"
        subprocess.run([STORE, "add", "--type", "Identity", "--everywhere", "--content", "I am Tessel, made up."],
                       env=self.env, capture_output=True, check=True, cwd=self.dir)

    def test_without_the_adapter_nothing_is_kept(self):
        out = subprocess.run([STORE, "keep-conversation", str(self.log)], env=self.env, capture_output=True, text=True, cwd=self.dir)
        self.assertIn("kept 0 new", out.stdout)

    def test_through_the_adapter_the_turn_is_kept_once(self):
        with mock.patch.dict(os.environ, self.env, clear=True), mock.patch.object(hook, "remote", return_value=None), \
             mock.patch.object(hook, "served", return_value=None), mock.patch.object(hook, "start_server"), \
             mock.patch.object(hook, "SOCKET", None):
            first = codex_hook.keep({"transcript_path": str(self.log), "turn_id": TURN, "last_assistant_message": "Marlo does."})
            again = codex_hook.keep({"transcript_path": str(self.log)})
        self.assertIn("kept 2 new", first)
        self.assertIn("kept 0 new", again)
        def said(words):
            return subprocess.run([STORE, "search", words], env=self.env, capture_output=True, text=True, cwd=self.dir).stdout
        self.assertRegex(said("bees"), r"\] John, .*\nWho keeps the bees\?")
        self.assertRegex(said("Marlo"), r"\] Tessel, .*\nMarlo does\.")
        import sqlite3
        with sqlite3.connect(self.dir / "t.db") as db:
            sessions = {s for (s,) in db.execute("select SessionId from ContextFragments where MessageId is not null")}
        self.assertEqual(sessions, {SESSION})


if __name__ == "__main__":
    unittest.main()
