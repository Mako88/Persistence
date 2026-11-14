"""The mirror of main that Arden pulls from (persistence-mirror.service). These tests run the unit's own ExecStart
against temporary repositories, so they test the text that gets installed, not a copy of it.

Why (2026-11-13): the mirror fetched into Claude's working checkout and pushed refs/remotes/origin/main from there.
The checkout's reference-transaction hook also pushes origin/main to the mirror, in the background, whenever it moves,
so a run whose fetch advanced main raced the hook's push into the same ref. It failed twice with "incorrect old value
provided" (2026-11-10 10:18, 2026-11-13 13:52 server time), each time in a run that advanced main; rebuilt in temporary
repositories, the old command with that hook failed 17 of 20 such runs. The fix is that the mirror is the only writer
of its refs (no checkout, no push, and the hook deleted when this unit is installed), and runs of it are serialised."""
import os
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)

UNIT = Path(__file__).resolve().parent.parent / "systemd" / "persistence-mirror.service"


def unit():
    """(environment, inner shell command) from the installed unit's text."""
    env, command = {}, None
    for line in UNIT.read_text().splitlines():
        if line.startswith("Environment="):
            key, value = line[len("Environment="):].split("=", 1)
            env[key] = value
        elif line.startswith("ExecStart="):
            argv = shlex.split(line[len("ExecStart="):])
            assert argv[:2] == ["/bin/sh", "-c"] and len(argv) == 3, argv
            command = argv[2]
    return env, command


def git(*args, cwd=None):
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


@unittest.skipUnless(shutil.which("git") and shutil.which("flock"), "needs git and flock")
class Mirror(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        self.upstream, self.mirror, self.work = root / "upstream.git", root / "mirror.git", root / "work"
        git("init", "-q", "--bare", "-b", "main", str(self.upstream))
        git("init", "-q", "--bare", "-b", "main", "--shared=0640", str(self.mirror))
        git("init", "-q", "-b", "main", str(self.work))
        for key, value in (("user.name", "t"), ("user.email", "t@example.invalid"), ("commit.gpgsign", "false")):
            git("config", key, value, cwd=self.work)
        self.n = 0
        self.advance()

    def tearDown(self):
        self.tmp.cleanup()

    def advance(self, force=False):
        """A new commit on upstream's main (or, with force, a commit that rewrites it)."""
        self.n += 1
        (self.work / "f").write_text(str(self.n))
        git("add", "f", cwd=self.work)
        git("commit", "-q", "--amend" if force else "-q", "-m", f"c{self.n}", cwd=self.work)
        git("push", "-q", "--force", str(self.upstream), "HEAD:main", cwd=self.work)
        return git("rev-parse", "HEAD", cwd=self.work)

    def run_unit(self):
        env, command = unit()
        env.update(MIRROR=str(self.mirror), UPSTREAM=str(self.upstream))
        return subprocess.run(["/bin/sh", "-c", command], env={**os.environ, **env}, capture_output=True, text=True)

    def mirrored(self):
        return git("--git-dir", str(self.mirror), "rev-parse", "refs/heads/main")

    def test_the_unit_names_only_the_mirror_and_upstream(self):
        env, command = unit()
        self.assertEqual(env, {"MIRROR": "/srv/persistence.git", "UPSTREAM": "git@github.com:Mako88/Persistence.git"})
        # No working checkout, no push: the mirror's refs have no other writer.
        self.assertNotIn("/home/", command)
        self.assertNotIn("push", command)
        self.assertIn('flock -w 120 "$MIRROR"', command)

    def test_follows_upstream_main_including_a_force_push(self):
        head = self.advance()
        self.assertEqual(self.run_unit().returncode, 0)
        self.assertEqual(self.mirrored(), head)
        head = self.advance()
        self.assertEqual(self.run_unit().returncode, 0)
        self.assertEqual(self.mirrored(), head)
        rewritten = self.advance(force=True)
        result = self.run_unit()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.mirrored(), rewritten)

    def test_a_run_that_changes_nothing_succeeds(self):
        head = self.advance()
        self.assertEqual(self.run_unit().returncode, 0)
        result = self.run_unit()
        self.assertEqual((result.returncode, result.stderr), (0, ""))
        self.assertEqual(self.mirrored(), head)

    def test_overlapping_runs_all_succeed(self):
        # A manual `systemctl start` cannot overlap the timer's run, but a hand-run fetch can; without the lock, two
        # fetches into one ref fail with "incorrect old value provided", as the mirror did.
        failures = []
        for _ in range(15):
            head = self.advance()
            with ThreadPoolExecutor(4) as pool:
                results = list(pool.map(lambda _: self.run_unit(), range(4)))
            failures += [r.stderr for r in results if r.returncode != 0]
            self.assertEqual(self.mirrored(), head)
        self.assertEqual(failures, [])


if __name__ == "__main__":
    unittest.main()
