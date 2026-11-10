"""The link (2026-11-10): home cannot reach John's PC, so the PC's hook server watches the store for messages sent to its
machine topic and starts a working session for each new one, once."""
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import hook

WHERE = {"host": "home", "machine": "pc", "watch": ["minecraft-mods"]}


class Link(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.listed = {"pc": "[#7 | Personal | tags: inbox/pc]\nFrom session aaaaaaaa (work), for pc:\ndo it\n", "minecraft-mods": ""}
        self.started = []
        patches = [
            mock.patch.object(hook, "STATE", Path(self.tmp.name)),
            mock.patch.object(hook, "LINK_SEEN", Path(self.tmp.name) / "seen.json"),
            mock.patch.object(hook, "remote", return_value=WHERE),
            mock.patch.object(hook, "call_store", side_effect=lambda *a, **k: (0, self.listed[a[2].split("/", 1)[1]], "")),
            mock.patch.object(hook, "start_work_session", side_effect=lambda cwd, reasons, origin: self.started.append(reasons) or reasons),
        ]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)

    def test_a_new_message_starts_one_session_and_only_once(self):
        hook.link_check(WHERE)
        hook.link_check(WHERE)
        self.assertEqual(self.started, ["for pc: #7"])

    def test_a_watched_project_counts_too(self):
        self.listed["minecraft-mods"] = "[#9 | Personal | tags: inbox/minecraft-mods]\nfix the knife\n"
        hook.link_check(WHERE)
        self.assertEqual(self.started, ["for pc: #7", "for minecraft-mods: #9"])

    def test_seeding_starts_nothing(self):
        hook.link_check(WHERE, start=False)
        hook.link_check(WHERE)
        self.assertEqual(self.started, [])


if __name__ == "__main__":
    unittest.main()
