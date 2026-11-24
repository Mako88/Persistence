"""install-hooks.sh, the host side of deploy-remote.sh: root never opens, makes or writes a path inside a user's home.
Everything there is done by that user through `sudo -u` (#49), so a symlink they planted can't send root's write
elsewhere. On 2026-11-15 one `install -d -o arden .../previous/<stamp>` made previous/ as root (msg
1571540809344352267); with #47 root still compared and `cp -p`'d inside the user's folder, and a symlinked previous/ or
hook file would have sent that write wherever it pointed (Arden's note 3 on #47, msg 1574902428355137729).

The tests can't run as root. A shim `sudo` takes `-u <user>`, logs it, and opens the user's fake home (mode 700) only
while that command runs; the rest of the time the home is mode 000, so anything the script does there itself, as the
caller (root on the host), fails. The shims refuse a path outside the test's temp dir before anything is written, so a
home root hidden in the script stops at its first write, not after (Arden's note 2 on #47). Canaries stand outside
the temp dir: a planted symlink points at one, and it must come through unchanged."""
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
STAMP = "20261115T150000Z"

# Refuses (exit 97, nothing run) if any path it's given, as written (a symlink in it not followed: `cp -P` copies a
# planted link without following it), lies outside $TEST_ROOT. The text after `-c` is a script, not
# a path.
GUARD = r"""inside() { case $(realpath -ms -- "$1") in "$TEST_ROOT"/*) return 0;; *) return 1;; esac; }
skip=
for a in "$@"; do
  if [ -n "$skip" ]; then skip=; continue; fi
  case $a in -c) skip=1; continue;; -*|[0-7][0-7][0-7]) continue;; esac
  case $a in */*) inside "$a" || { echo "test shim: refused $a, outside $TEST_ROOT" >&2; exit 97; };; esac
done
"""

# Every write into a home goes through one of these (main's script used install and cp as root; this one runs mkdir,
# mv and cp as the user).
WRITERS = {"install": "/usr/bin/install", "cp": "/bin/cp", "mv": "/bin/mv", "mkdir": "/bin/mkdir"}

SUDO = "#!/usr/bin/env bash\n" + r"""[ "$1" = -u ] || { echo "test shim: sudo without -u: $*" >&2; exit 98; }
u=$2; shift 2
""" + GUARD + r"""printf '%s %s\n' "$u" "$1" >> "$SUDO_LOG"
h=$TEST_HOMES/$u
[ -d "$h" ] && chmod 700 "$h"
rc=0; "$@" || rc=$?
[ -d "$h" ] && chmod 000 "$h"
exit $rc
"""


class InstallHooksTest(unittest.TestCase):
    user = getpass.getuser()

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.homes = self.tmp / "home"
        self.d = self.homes / self.user / ".claude" / "persistence"
        self.d.mkdir(parents=True)
        self.stage = self.tmp / "stage"
        self.stage.mkdir(mode=0o700)
        for f in FILES:
            (self.stage / f).write_text(f"new {f}\n")
        shims = self.tmp / "shims"
        shims.mkdir()
        for name, real in WRITERS.items():
            (shims / name).write_text("#!/usr/bin/env bash\n" + GUARD + f'exec {real} "$@"\n')
        (shims / "sudo").write_text(SUDO)
        for p in shims.iterdir():
            p.chmod(0o755)
        self.sudo_log = self.tmp / "sudo.log"
        self.env = {**os.environ, "PATH": f"{shims}:{os.environ['PATH']}", "SUDO_LOG": str(self.sudo_log),
                    "TEST_ROOT": str(self.tmp.resolve()), "TEST_HOMES": str(self.homes)}
        self.canary = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.canary)
        (self.canary / "secret").write_text("canary secret\n")

    def unlock(self):
        for h in self.homes.iterdir():
            h.chmod(0o700)

    def run_script(self, users=None, script=SCRIPT, check=True, lock=True):
        # lock=False leaves the homes open to the caller too: the symlink tests use it, so that a script which does its
        # work as root (main at 6101a8f) fails them by following the link, not by the locked home.
        for h in self.homes.iterdir():
            h.chmod(0o000 if lock else 0o700)
        try:
            r = subprocess.run(["bash", str(script), str(self.homes), str(self.stage), STAMP, " ".join(FILES),
                                *(users or [self.user])], env=self.env, capture_output=True, text=True)
        finally:
            self.unlock()
        if check:
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        return r

    def canary_state(self):
        return sorted((str(p.relative_to(self.canary)), p.read_bytes() if p.is_file() else None)
                      for p in self.canary.rglob("*"))

    def assert_installed(self, d=None):
        d = d or self.d
        for f in FILES:
            p = d / f
            self.assertFalse(p.is_symlink(), f)
            self.assertTrue(p.is_file(), f)
            self.assertEqual(p.read_text(), f"new {f}\n")
            self.assertEqual(p.stat().st_uid, os.getuid())
            self.assertEqual(p.stat().st_mode & 0o777, 0o644)

    def test_a_first_backup_makes_previous_and_the_stamp_dir_as_the_user(self):
        (self.d / "hook.py").write_text("old hook\n")
        out = self.run_script().stdout
        self.assertEqual((self.d / "previous").stat().st_mode & 0o777, 0o755)
        self.assertEqual((self.d / "previous" / STAMP).stat().st_mode & 0o777, 0o700)
        self.assertEqual((self.d / "previous" / STAMP / "hook.py").read_text(), "old hook\n")
        self.assertEqual(sorted(p.name for p in (self.d / "previous" / STAMP).iterdir()), ["hook.py"])
        self.assertIn("replaced hook.py", out)
        self.assert_installed()

    def test_a_later_deploy_keeps_the_existing_previous_and_adds_only_its_stamp_dir(self):
        (self.d / "previous" / "older").mkdir(parents=True)
        (self.d / "hook.py").write_text("old hook\n")
        self.run_script()
        self.assertEqual(sorted(p.name for p in (self.d / "previous").iterdir()), [STAMP, "older"])

    def test_unchanged_files_make_no_directory_and_leave_no_temp_file(self):
        for f in FILES:
            (self.d / f).write_text(f"new {f}\n")
        out = self.run_script().stdout
        self.assertFalse((self.d / "previous").exists())
        self.assertIn("no hook file changed", out)
        self.assertEqual(sorted(p.name for p in self.d.iterdir()), sorted(FILES))

    def test_every_step_in_the_home_runs_as_the_user(self):
        # The home is mode 000 except inside `sudo -u <user>`: a compare, mkdir or copy the script made itself, as
        # root, would fail here. Main at 6101a8f fails this test.
        (self.d / "hook.py").write_text("old hook\n")
        self.run_script()
        self.assert_installed()
        self.assertTrue((self.d / "previous" / STAMP / "hook.py").is_file())
        users = {line.split()[0] for line in self.sudo_log.read_text().splitlines()}
        self.assertEqual(users, {self.user})

    def test_a_symlinked_hook_file_is_replaced_not_followed(self):
        # #49: the install's write into the user's folder followed a symlinked hook file (Arden, 1574902428355137729).
        (self.d / "hook.py").symlink_to(self.canary / "secret")
        before = self.canary_state()
        out = self.run_script(lock=False).stdout
        self.assertEqual(self.canary_state(), before)
        self.assert_installed()
        kept = self.d / "previous" / STAMP / "hook.py"
        self.assertTrue(kept.is_symlink(), "the link itself is kept, not what it points at")
        self.assertIn("replaced hook.py", out)

    def test_a_symlinked_previous_is_refused_and_nothing_is_written_through_it(self):
        # #49: previous/ pointing outside the home sent the backup (and its stamp dir) wherever it pointed.
        (self.d / "previous").symlink_to(self.canary, target_is_directory=True)
        (self.d / "hook.py").write_text("old hook\n")
        before = self.canary_state()
        r = self.run_script(check=False, lock=False)
        self.assertEqual(self.canary_state(), before)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("refused", r.stderr)
        self.assertEqual(self.canary_state(), before)
        self.assertEqual((self.d / "hook.py").read_text(), "old hook\n", "not replaced without its copy")
        self.assertEqual((self.d / "coordination.py").read_text(), "new coordination.py\n")  # new, needs no copy

    def test_a_symlinked_stamp_dir_is_refused_too(self):
        (self.d / "previous").mkdir()
        (self.d / "previous" / STAMP).symlink_to(self.canary, target_is_directory=True)
        (self.d / "hook.py").write_text("old hook\n")
        before = self.canary_state()
        r = self.run_script(check=False, lock=False)
        self.assertEqual(self.canary_state(), before)
        self.assertEqual(r.returncode, 1)
        self.assertEqual((self.d / "hook.py").read_text(), "old hook\n")

    def test_a_symlink_in_the_stage_is_refused_and_nothing_lands(self):
        # Arden, msg 1574902675328335974: root reading the staged file must not follow a symlink there. Both readers:
        # root, for a user who isn't the stage's owner, and the owner reading their own stage.
        peer = "peer-not-the-stage-owner"
        (self.homes / peer / ".claude" / "persistence").mkdir(parents=True)
        (self.stage / "hook.py").unlink()
        (self.stage / "hook.py").symlink_to(self.canary / "secret")
        r = self.run_script(users=[self.user], check=False, lock=False)
        self.assertNotIn(b"canary", b"".join(p.read_bytes() for p in self.d.rglob("*") if p.is_file()))
        r = self.run_script(users=[self.user, peer], check=False)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertEqual(r.stderr.count("refused hook.py"), 2, r.stderr)
        for u in (self.user, peer):
            d = self.homes / u / ".claude" / "persistence"
            self.assertFalse((d / "hook.py").exists() or (d / "hook.py").is_symlink(), u)
            self.assertNotIn(b"canary", b"".join(p.read_bytes() for p in d.rglob("*") if p.is_file()))
            self.assertEqual((d / "coordination.py").read_text(), "new coordination.py\n")

    def test_a_user_other_than_the_stage_owner_gets_the_bytes_root_read(self):
        peer = "peer-not-the-stage-owner"
        d = self.homes / peer / ".claude" / "persistence"
        d.mkdir(parents=True)
        (d / "hook.py").write_text("old hook\n")
        out = self.run_script(users=[peer]).stdout
        self.assert_installed(d)
        self.assertEqual((d / "previous" / STAMP / "hook.py").read_text(), "old hook\n")
        self.assertIn(f"{peer}: replaced hook.py", out)

    def test_a_stage_dir_others_can_write_is_refused(self):
        self.stage.chmod(0o777)
        r = self.run_script(check=False)
        self.assertEqual(r.returncode, 1)
        self.assertIn("written by group or others", r.stderr)
        self.assertFalse((self.d / "hook.py").exists())

    def test_a_symlinked_stage_dir_is_refused(self):
        real = self.tmp / "real-stage"
        self.stage.rename(real)
        self.stage.symlink_to(real, target_is_directory=True)
        r = self.run_script(check=False)
        self.assertEqual(r.returncode, 1)
        self.assertIn("symlink", r.stderr)
        self.assertFalse((self.d / "hook.py").exists())

    def test_a_home_root_hidden_in_the_script_is_refused_before_it_writes(self):
        # Arden's note 2 on #47: a script that ignores $1 for a built-in root must fail at its first write, not after
        # it. The canary stands in for /home/: it's outside the test's temp dir, and it must stay as it was.
        d = self.canary / self.user / ".claude" / "persistence"
        d.mkdir(parents=True)
        (d / "hook.py").write_text("old hook\n")
        before = self.canary_state()
        hidden = self.tmp / "hidden-root.sh"
        hidden.write_text(SCRIPT.read_text().replace("homes=${1:?usage}", f"homes={self.canary}; : ${{1:?usage}}", 1))
        self.assertNotEqual(hidden.read_text(), SCRIPT.read_text())
        r = self.run_script(script=hidden, check=False)
        self.assertNotEqual(r.returncode, 0, r.stderr)
        self.assertIn("test shim: refused", r.stderr)
        self.assertEqual(self.canary_state(), before)


if __name__ == "__main__":
    unittest.main()
