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
        with no_real_claude.expected(), self.assertRaises(AssertionError):  # on purpose: the refusal is the test
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


class LaunchThatNeverReachesTheBinary(unittest.TestCase):
    """The de220054 incident (2026-11-13, session 4a4f6f23): a test called hook.run_due on a due item and swallowed what
    it raised. _resume_or_start ran `bash -lc "cd ... && python3 -I run_line.py claude -p --resume ..."`; Popen's check
    saw only `bash`, and the shell then failed on its stdin redirect, because tearDown had already removed the test's
    state dir. The fake was never reached, so the test passed green on the real launch path. Each case runs a test
    inside this one and asserts the guard failed it."""

    def nested(self, body, base=unittest.TestCase):
        case = type("Leaky", (base,), {"test_it": body})("test_it")
        result = unittest.TestResult()
        case.run(result)
        return result

    def test_a_shell_launch_that_dies_before_claude_fails_its_test(self):
        def body(_):
            try:
                subprocess.Popen(["bash", "-lc", "cd /nonexistent-de220054 && claude -p --resume x"],
                                 stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            except Exception:  # noqa: BLE001  the code under test swallowing it, as run_due's caller did
                pass
        result = self.nested(body)
        self.assertEqual(len(result.failures), 1, result.failures)
        self.assertIn("claude -p --resume x", result.failures[0][1])

    def test_the_incident_test_itself_fails(self):
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import test_heartbeat_stalled as S

        def body(case):  # the leaked test's body (the state dir goes at tearDown, before the shell reads it)
            w = {"id": S.sid(), "session": S.sid(), "at": S.T0.timestamp() - 600, "reason": "x", "hb": "quiet:1",
                 "set": S.T0.isoformat()}
            S.hook.wakes(lambda items: items + [w])
            try:
                S.hook.run_due(grace=0, home=case.home)
            except Exception:  # noqa: BLE001  as the leaked test did
                pass
        result = self.nested(body, S.Base)
        self.assertEqual(len(result.failures), 1, result.failures)
        self.assertIn("run_line.py claude -p --resume", result.failures[0][1])

    def test_every_launch_route_names_claude_and_ordinary_paths_do_not(self):
        sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
        import hook

        self.assertTrue(no_real_claude.names_claude(hook.WORK_ARGV))
        self.assertTrue(no_real_claude.names_claude([sys.executable, "-I", "cwd_exec.py", "3", *hook.WORK_ARGV]))
        self.assertTrue(no_real_claude.names_claude(["bash", "-lc", "cd /x && claude -p"]))
        self.assertTrue(no_real_claude.names_claude(["/usr/local/bin/claude", "-p"]))
        self.assertTrue(no_real_claude.names_claude(["bash", "-lc", "x;claude"]))
        for argv in ([sys.executable, str(Path(hook.__file__).resolve()), "serve"], ["ls", "/home/x/.claude/projects"],
                     ["bash", "-lc", "echo claudette"], ["git", "log", "--author=claude-bot"]):
            self.assertFalse(no_real_claude.names_claude(argv), argv)

    def test_inside_expected_a_shell_launch_still_reaches_the_fake(self):
        with no_real_claude.expected() as made:
            subprocess.run(["bash", "-lc", "claude -p ok"], stdin=subprocess.DEVNULL, capture_output=True)
        self.assertEqual(len(made), 1)


if __name__ == "__main__":
    unittest.main()
