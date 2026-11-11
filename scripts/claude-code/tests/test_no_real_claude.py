"""The guard against a test starting a real claude (Arden 1570199499278909491): every module here imports it first, and
it catches a launch the code under test makes on a path nobody faked."""
import subprocess
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)


class Guard(unittest.TestCase):
    def test_every_module_imports_the_guard_first_and_claude_is_the_fake(self):
        no_real_claude.check_suite(self, Path(__file__).resolve().parent)

    def test_a_login_shell_running_claude_reaches_the_fake(self):
        with no_real_claude.expected() as made:
            done = subprocess.run(["bash", "-lc", "cd /tmp && claude -p --session-id x --permission-mode bypassPermissions"],
                                  stdin=subprocess.DEVNULL, capture_output=True)
        self.assertEqual(done.returncode, 97)
        self.assertEqual(len(made), 1)
        self.assertIn("--permission-mode bypassPermissions", made[0])

    def test_the_real_binary_by_path_is_refused_before_it_starts(self):
        if not no_real_claude.REAL:
            self.skipTest("no real claude on this machine")
        with self.assertRaises(AssertionError):
            subprocess.Popen([no_real_claude.REAL, "-p"], stdin=subprocess.DEVNULL)



class MutationRPath(unittest.TestCase):
    """Mutation R's path itself, with nothing faked: a hand-off item that carries no claim goes run_due ->
    _resume_or_start -> start_work_session's plain `bash -lc ... claude -p`. It reaches the fake, never a real session."""

    def test_the_plain_launch_reaches_only_the_fake(self):
        sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
        import hook

        self.assertTrue(str(hook.STATE).startswith(str(no_real_claude.HOME)), hook.STATE)
        work = no_real_claude.HOME / "work"
        work.mkdir(exist_ok=True)
        with no_real_claude.expected() as made:
            hook.start_work_session(str(work), "Build the spawn claim", "room")
        self.assertEqual(len(made), 1)
        where, argv = made[0].split("\n", 1)
        self.assertEqual(where, str(work))
        self.assertIn("--permission-mode bypassPermissions", argv)


if __name__ == "__main__":
    unittest.main()
