"""heartbeat_fixture.sh (check 5 of docs/design/heartbeat-enable-claude.md), the shell harness itself: it removes its
throwaway dir on every exit, including a forced early failure, and takes the code dir as data, so a path with a quote
works. Stub hook/heartbeat/claims modules in a fake code dir, a fake HOME, and TMPDIR pointed at an empty dir that must
be empty again afterwards. No real systemd, store, wakes.json or model is touched."""
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401

FIXTURE = Path(__file__).resolve().parent.parent / "heartbeat_fixture.sh"

CLAIMS = """
TASK_FIELDS = ("task", "owner", "id", "dispatch", "status", "cwd", "created", "updated", "pause_until", "nudged",
               "reported", "outcome", "history")
KEYS = set(TASK_FIELDS)
def valid_record(r):
    return set(r) == KEYS
"""
HOOK = """
import os
from pathlib import Path
STATE = Path(os.environ["PERSISTENCE_STATE_DIR"])
WAKES = STATE.parent / "wakes.json"
"""
# Reports each claim once, as the real beat does for "no valid cwd"; BREAK selects a forced failure.
HEARTBEAT = """
import json, os
from pathlib import Path
import hook
STATE = hook.STATE / "heartbeat.json"
BREAK = {brk!r}
def main(argv):
    seen = json.loads(STATE.read_text()) if STATE.exists() else []
    if BREAK == "beat2" and seen:
        raise RuntimeError("forced failure on beat 2")
    if BREAK == "assert":
        assert False, "forced assertion"
    for p in sorted((hook.STATE / "tasks").glob("*.json")):
        r = json.loads(p.read_text())
        if r["id"] not in seen and not os.path.isdir(r["cwd"]):
            print(f"heartbeat claim {{r['id']}}: no valid cwd")
            seen.append(r["id"])
    STATE.write_text(json.dumps(seen))
    return 0
"""


class FixtureHarness(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="hbfx-test-"))
        self.addCleanup(subprocess.run, ["rm", "-rf", "--", str(self.root)])
        self.tmp = self.root / "tmp"
        self.tmp.mkdir()
        self.home = self.root / "home"
        (self.home / ".claude" / "persistence").mkdir(parents=True)
        self.real_wakes = self.home / ".claude" / "persistence" / "wakes.json"
        self.real_wakes.write_text('[{"keep": "me"}]\n')

    def code(self, brk=None, claims=CLAIMS, name="it's \"code\" $dir"):
        d = self.root / name
        d.mkdir()
        (d / "claims.py").write_text(textwrap.dedent(claims))
        (d / "hook.py").write_text(textwrap.dedent(HOOK))
        (d / "heartbeat.py").write_text(textwrap.dedent(HEARTBEAT.format(brk=brk)))
        return d

    def run_fixture(self, code):
        env = {**os.environ, "HOME": str(self.home), "TMPDIR": str(self.tmp)}
        return subprocess.run(["bash", str(FIXTURE), str(code)], env=env, capture_output=True, text=True, timeout=60)

    def assert_clean(self, r):
        self.assertEqual(sorted(p.name for p in self.tmp.iterdir()), [], r.stdout + r.stderr)
        self.assertEqual(self.real_wakes.read_text(), '[{"keep": "me"}]\n')

    def test_pass_with_a_quoted_code_path_and_cleans_up(self):
        r = self.run_fixture(self.code())
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("PASS: fixture claim", r.stdout)
        self.assert_clean(r)

    def test_beat_that_repeats_fails_and_cleans_up(self):
        # A beat that reports again on beat 2 (no dedup): the harness's own FAIL path.
        d = self.code(name="plain")
        (d / "heartbeat.py").write_text((d / "heartbeat.py").read_text().replace(
            "STATE.write_text(json.dumps(seen))", "pass"))
        r = self.run_fixture(d)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("FAIL; beat 2 repeated it", r.stdout)
        self.assert_clean(r)

    def test_forced_early_failures_still_clean_up(self):
        cases = {
            "import": dict(claims="raise ImportError('forced import failure')\n"),
            "record": dict(claims=CLAIMS.replace("return set(r) == KEYS", "return False")),
            "assert": dict(brk="assert"),
            "beat2": dict(brk="beat2"),
        }
        for name, kw in cases.items():
            with self.subTest(name):
                r = self.run_fixture(self.code(name=f"code-{name}", **kw))
                self.assertNotEqual(r.returncode, 0, r.stdout + r.stderr)
                self.assertNotIn("PASS", r.stdout)
                self.assert_clean(r)

    def test_trap_is_what_cleans_up(self):
        # Mutation check: without the trap, a forced early failure leaves the throwaway dir behind.
        src = FIXTURE.read_text()
        self.assertEqual(src.count("trap 'rm -rf -- \"$fx\"' EXIT\n"), 1)
        mutant = self.root / "mutant.sh"
        mutant.write_text(src.replace("trap 'rm -rf -- \"$fx\"' EXIT\n", ""))
        env = {**os.environ, "HOME": str(self.home), "TMPDIR": str(self.tmp)}
        r = subprocess.run(["bash", str(mutant), str(self.code(brk="assert", name="m"))], env=env,
                           capture_output=True, text=True, timeout=60)
        self.assertNotEqual(r.returncode, 0)
        self.assertEqual(len(list(self.tmp.iterdir())), 1)


if __name__ == "__main__":
    unittest.main()
