"""The served store's 12 s cap is the hook's budget (the harness gives an HTTP hook 20 s), not every caller's. sleep_pass
asks `sleep-list` with timeout=300 from a nightly timer, and on my store it takes ~18 s: capped at 12, it was skipped,
and the worklist was empty on 2026-11-14, -15 and -16 (Arden found it on theirs, issue #67 comment 6912449945).
A timeout the caller names is kept; a call that names none still gets the hook's cap."""
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import hook

REMOTE = {"host": "home", "port": 8095}


class StoreTimeout(unittest.TestCase):
    def given(self, where):
        """call_store with the store on `where` (None: served here); returns the timeouts the server was asked with."""
        asked = []
        patches = [mock.patch.object(hook, "remote", return_value=where),
                   mock.patch.object(hook, "served_remote", side_effect=lambda w, a, s, t: asked.append(t) or (0, "ok", "")),
                   mock.patch.object(hook, "served", side_effect=lambda a, s, t: asked.append(t) or (0, "ok", ""))]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)
        return asked

    def test_a_named_timeout_reaches_the_remote_store(self):
        asked = self.given(REMOTE)
        hook.call_store("sleep-list", timeout=300)
        self.assertEqual(asked, [300])

    def test_a_named_timeout_reaches_the_local_served_store(self):
        asked = self.given(None)
        hook.call_store("sleep-list", timeout=300)
        self.assertEqual(asked, [300])

    def test_sleep_list_keeps_its_timeout_through_store(self):
        asked = self.given(REMOTE)
        self.assertEqual(hook.store("sleep-list", timeout=300), "ok")
        self.assertEqual(asked, [300])

    def test_an_unnamed_timeout_keeps_the_hook_cap_on_both_paths(self):
        asked = self.given(REMOTE)
        hook.call_store("recall", "x")
        remote_asked = list(asked)
        asked.clear()
        hook.remote.return_value = None
        hook.call_store("recall", "x")
        self.assertEqual((remote_asked, asked), ([12], [12]))

    def test_keep_conversation_still_uncapped(self):
        asked = self.given(REMOTE)
        hook.call_store("keep-conversation", "t.jsonl")
        self.assertEqual(asked, [60])


if __name__ == "__main__":
    unittest.main()
