"""install-hooks.sh, the host side of deploy-remote.sh: every directory it makes in a user's home is made by that user's
name. On 2026-11-15 one `install -d -o arden .../previous/<stamp>` made previous/ as root (install -d makes missing
parents as the caller), and Arden couldn't add to it or clean it up (msg 1571540809344352267).

The tests can't run as root, so a shim `install` on PATH records each call and runs the real one without -o/-g. A
directory that appears in the fake home without a recorded `install -d -o <user>` for it was made by whoever ran the
script: root, on the host."""
import getpass
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401

SCRIPT = Path(__file__).resolve().parent.parent / "install-hooks.sh"
FILES = ["hook.py", "coordination.py"]

SHIM = r"""#!/usr/bin/env bash
printf '%s\n' "$*" >> "$INSTALL_LOG"
args=()
while [ $# -gt 0 ]; do
  case $1 in -o|-g) shift 2;; *) args+=("$1"); shift;; esac
done
exec /usr/bin/install "${args[@]}"
"""


class InstallHooksTest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.user = getpass.getuser()
        self.homes = self.tmp / "home"
        self.d = self.homes / self.user / ".claude" / "persistence"
        self.d.mkdir(parents=True)
        self.stage = self.tmp / "stage"
        self.stage.mkdir()
        for f in FILES:
            (self.stage / f).write_text(f"new {f}\n")
        shims = self.tmp / "shims"
        shims.mkdir()
        (shims / "install").write_text(SHIM)
        (shims / "install").chmod(0o755)
        self.log = self.tmp / "install.log"
        self.env = {**os.environ, "PATH": f"{shims}:{os.environ['PATH']}", "INSTALL_LOG": str(self.log)}

    def run_script(self, stamp="20261115T150000Z"):
        return subprocess.run(["bash", str(SCRIPT), str(self.homes), str(self.stage), stamp, " ".join(FILES), self.user],
                              env=self.env, capture_output=True, text=True, check=True)

    def dirs_made_as_user(self):
        calls = self.log.read_text().splitlines() if self.log.exists() else []
        made = set()
        for c in calls:
            a = c.split()
            if "-d" in a and "-o" in a and a[a.index("-o") + 1] == self.user:
                made.add(Path(a[-1]))
        return made

    def test_a_first_backup_makes_previous_and_the_stamp_dir_as_the_user(self):
        (self.d / "hook.py").write_text("old hook\n")
        before = {p for p in self.d.rglob("*") if p.is_dir()}
        out = self.run_script().stdout
        new_dirs = {p for p in self.d.rglob("*") if p.is_dir()} - before
        self.assertEqual(new_dirs, {self.d / "previous", self.d / "previous" / "20261115T150000Z"})
        self.assertEqual(new_dirs - self.dirs_made_as_user(), set(), "made as the caller (root on the host), not the user")
        self.assertEqual((self.d / "previous" / "20261115T150000Z" / "hook.py").read_text(), "old hook\n")
        self.assertIn("replaced hook.py", out)

    def test_a_later_deploy_keeps_the_existing_previous_and_makes_only_its_stamp_dir(self):
        (self.d / "previous" / "older").mkdir(parents=True)
        (self.d / "hook.py").write_text("old hook\n")
        self.run_script(stamp="20261116T000000Z")
        self.assertEqual(self.dirs_made_as_user(), {self.d / "previous" / "20261116T000000Z"})
        self.assertTrue((self.d / "previous" / "older").is_dir())

    def test_unchanged_files_make_no_directory(self):
        for f in FILES:
            (self.d / f).write_text(f"new {f}\n")
        out = self.run_script().stdout
        self.assertFalse((self.d / "previous").exists())
        self.assertIn("no hook file changed", out)

    def test_every_installed_file_is_given_to_the_user(self):
        self.run_script()
        calls = [c.split() for c in self.log.read_text().splitlines() if "-d" not in c.split()]
        self.assertEqual(len(calls), len(FILES))
        for a in calls:
            self.assertEqual(a[a.index("-o") + 1], self.user)


if __name__ == "__main__":
    unittest.main()
