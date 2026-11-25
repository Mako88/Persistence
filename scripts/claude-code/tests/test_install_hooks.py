"""install-hooks.sh, the host side of deploy-remote.sh: root never opens, makes or writes a path inside a user's home.
Everything there is done by that user through `sudo -u` (#49), so a symlink they planted can't send root's write
elsewhere. On 2026-11-15 one `install -d -o arden .../previous/<stamp>` made previous/ as root (msg
1571540809344352267); with #47 root still compared and `cp -p`'d inside the user's folder, and a symlinked previous/ or
hook file would have sent that write wherever it pointed (Arden's note 3 on #47, msg 1574902428355137729).

The tests can't run as root. A shim `sudo` takes `-u <user>`, logs it, and opens the user's fake home (mode 700) only
while that command runs; the rest of the time the home is mode 000, so anything the script does there itself, as the
caller (root on the host), fails. The shims refuse a path outside the test's temp dir before anything is written, so a
home root hidden in the script stops at its first write, not after (Arden's note 2 on #47). Canaries stand outside
the temp dir: a planted symlink points at one, and it must come through unchanged.

The script runs everything as another account under `env -i PATH=/usr/bin:/bin bash --noprofile --norc -c`. The shim
logs that prefix and checks it, then puts its own dir back at the front of that PATH (and passes TEST_ROOT and FAIL_ON
through), so the guarded writers still stand in front of the real ones. FAIL_ON names a path: a shim writer given it
fails, which is how a test makes a step fail after every check passed (Ember's review of #93); FAIL_CMD=<name> limits
that to one writer. FAIL_AFTER names a path the same way, but the writer does its work first and then fails, and
FAIL_AS=<name> makes the shim sudo fail a script run under that name (`bash -c <script> <name>`). Those three stand in
for a step that dies after its backup landed, after its rename, and a look afterwards that fails (Ember's review of #93
at 735f69b: the failure report must say what it saw, not what the step order implies)."""
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
  case $a in -c) skip=1; continue;; -*|[0-7][0-7][0-7]|[A-Za-z_]*=*) continue;; esac
  case $a in */*) inside "$a" || { echo "test shim: refused $a, outside $TEST_ROOT" >&2; exit 97; };; esac
  if [ -n "${FAIL_ON:-}" ] && [ "$(realpath -ms -- "$a")" = "$FAIL_ON" ] && [ "${FAIL_CMD:-${0##*/}}" = "${0##*/}" ]; then
    echo "test shim: injected failure at $a" >&2; exit 1
  fi
  if [ -n "${FAIL_AFTER:-}" ] && [ "$(realpath -ms -- "$a")" = "$FAIL_AFTER" ] && [ "${FAIL_CMD:-${0##*/}}" = "${0##*/}" ]; then after=1; fi
done
"""

# Every write into a home goes through one of these (main's script used install and cp as root; this one runs mkdir,
# mv and cp as the user).
WRITERS = {"install": "/usr/bin/install", "cp": "/bin/cp", "mv": "/bin/mv", "mkdir": "/bin/mkdir", "ln": "/bin/ln"}
CLEAN = "env -i PATH=/usr/bin:/bin bash --noprofile --norc -c"

SUDO = "#!/usr/bin/env bash\n" + r"""[ "$1" = -u ] || { echo "test shim: sudo without -u: $*" >&2; exit 98; }
u=$2; shift 2
""" + GUARD + r"""pre=; for a; do pre="$pre${pre:+ }$a"; [ "$a" = -c ] && break; done
printf '%s %s\n' "$u" "$pre" >> "$SUDO_LOG"
name=; seen=; for a; do [ -n "$seen" ] && { [ "$seen" = 2 ] && { name=$a; break; }; seen=2; continue; }; [ "$a" = -c ] && seen=1; done
if [ -n "${FAIL_AS:-}" ] && [ "$name" = "$FAIL_AS" ]; then echo "test shim: injected failure running $name as $u" >&2; exit 1; fi
locked=; if [ "$name" = inspect ] && [ -n "${LOCK_FOR_INSPECT:-}" ]; then locked=$LOCK_FOR_INSPECT; fi
if [ "$1 $2 $3" = "env -i PATH=/usr/bin:/bin" ]; then
  shift 3; set -- env -i "PATH=$SHIMS:/usr/bin:/bin" "TEST_ROOT=$TEST_ROOT" "FAIL_ON=${FAIL_ON:-}" \
    "FAIL_CMD=${FAIL_CMD:-}" "FAIL_AFTER=${FAIL_AFTER:-}" "PLANT_LINK=${PLANT_LINK:-}" "$@"
fi
h=$TEST_HOMES/$u
[ -d "$h" ] && chmod 700 "$h"
[ "$u" = "${STAGE_OWNER_UNLOCKS:-}" ] && chmod 700 "$TEST_STAGE"
[ -n "$locked" ] && chmod 000 "$locked"
rc=0; "$@" || rc=$?
[ -n "$locked" ] && chmod 700 "$locked"
[ -d "$h" ] && chmod 000 "$h"
[ "$u" = "${STAGE_OWNER_UNLOCKS:-}" ] && chmod 000 "$TEST_STAGE"
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
            # PLANT_LINK=<path>=<target>: the cp shim makes that link just before it runs, standing in for a link
            # that appears after every check passed (the user racing their own install step).
            plant = ('[ "${0##*/}" = cp ] && [ -n "${PLANT_LINK:-}" ] && /bin/ln -s "${PLANT_LINK#*=}" "${PLANT_LINK%%=*}"\n'
                     if name == "cp" else "")
            # FAIL_AFTER=<path>: the writer given it does its work, then fails, standing in for a step that dies after
            # its last real change (Ember's review of #93 at 735f69b).
            (shims / name).write_text("#!/usr/bin/env bash\nafter=\n" + GUARD + plant
                                      + f'{real} "$@" || exit $?\n'
                                      + '[ -n "$after" ] && { echo "test shim: injected failure after $0 ran" >&2; exit 1; }\n'
                                      + 'exit 0\n')
        (shims / "sudo").write_text(SUDO)
        for p in shims.iterdir():
            p.chmod(0o755)
        self.sudo_log = self.tmp / "sudo.log"
        self.env = {**os.environ, "PATH": f"{shims}:{os.environ['PATH']}", "SUDO_LOG": str(self.sudo_log),
                    "TEST_ROOT": str(self.tmp.resolve()), "TEST_HOMES": str(self.homes), "SHIMS": str(shims),
                    "TEST_STAGE": str(self.stage), "SUDO_USER": self.user}
        self.canary = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.canary)
        (self.canary / "secret").write_text("canary secret\n")

    def unlock(self):
        for h in self.homes.iterdir():
            h.chmod(0o700)

    def run_script(self, users=None, script=SCRIPT, check=True, lock=True, files=FILES):
        # lock=False leaves the homes open to the caller too: the symlink tests use it, so that a script which does its
        # work as root (main at 6101a8f) fails them by following the link, not by the locked home.
        for h in self.homes.iterdir():
            h.chmod(0o000 if lock else 0o700)
        try:
            r = subprocess.run(["bash", str(script), str(self.homes), str(self.stage), STAMP, " ".join(files),
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
        self.assertIn("replaced: hook.py", out)
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
        lines = self.sudo_log.read_text().splitlines()
        self.assertEqual({line.split()[0] for line in lines}, {self.user})
        # Ember's review of #93: every step as another account runs in a clean environment, never sudoers' defaults.
        self.assertEqual({line.split(" ", 1)[1] for line in lines}, {CLEAN})

    def test_a_symlinked_hook_file_is_replaced_not_followed(self):
        # #49: the install's write into the user's folder followed a symlinked hook file (Arden, 1574902428355137729).
        (self.d / "hook.py").symlink_to(self.canary / "secret")
        before = self.canary_state()
        out = self.run_script(lock=False).stdout
        self.assertEqual(self.canary_state(), before)
        self.assert_installed()
        kept = self.d / "previous" / STAMP / "hook.py"
        self.assertTrue(kept.is_symlink(), "the link itself is kept, not what it points at")
        self.assertIn("replaced: hook.py", out)

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
        # Ember's review of #93: every problem is found before anything changes, so not even a new file lands.
        self.assertFalse((self.d / "coordination.py").exists())
        self.assertIn("nothing installed", r.stderr)

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
        self.assertEqual(r.stderr.count("refused hook.py"), 1, r.stderr)  # read once, by the stage's owner
        self.assertIn("nothing installed", r.stderr)
        for u in (self.user, peer):
            d = self.homes / u / ".claude" / "persistence"
            self.assertEqual(list(d.iterdir()), [], u)

    def test_a_user_other_than_the_stage_owner_gets_the_bytes_its_owner_read(self):
        peer = "peer-not-the-stage-owner"
        d = self.homes / peer / ".claude" / "persistence"
        d.mkdir(parents=True)
        (d / "hook.py").write_text("old hook\n")
        out = self.run_script(users=[peer]).stdout
        self.assert_installed(d)
        self.assertEqual((d / "previous" / STAMP / "hook.py").read_text(), "old hook\n")
        self.assertIn(f"{peer}: new: coordination.py; replaced: hook.py (", out)

    def test_a_stage_owned_by_an_untrusted_account_is_refused_before_anything_is_read(self):
        # Ember's blocker 1 on #93: the stage's owner must be root or the account that ran sudo. Here the stage is the
        # test user's, but sudo was "run by" someone else.
        self.env["SUDO_USER"] = "someone-else"
        r = self.run_script(check=False)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn(f"owned by {self.user}, which is neither root nor the account that ran sudo (someone-else)",
                      r.stderr)
        self.assertFalse(self.sudo_log.exists(), "nothing run as anyone")
        self.assertEqual(list(self.d.iterdir()), [])
        del self.env["SUDO_USER"]
        r = self.run_script(check=False)
        self.assertIn("(none)", r.stderr)
        self.assertEqual(list(self.d.iterdir()), [])

    def test_the_stage_is_read_by_its_owner_never_by_root(self):
        # Ember's blocker 1 on #93: at 6a46981 root checked a staged file, then opened it by path, and a swap in
        # between leaked a root-only file (41 of 400 real runs). Here the stage is mode 000 except while a command runs
        # as its owner, so a read by root (the caller, here) fails. The target is a peer, not the owner.
        peer = "peer-not-the-stage-owner"
        d = self.homes / peer / ".claude" / "persistence"
        d.mkdir(parents=True)
        self.env["STAGE_OWNER_UNLOCKS"] = self.user
        self.stage.chmod(0o000)
        try:
            out = self.run_script(users=[peer]).stdout
        finally:
            self.stage.chmod(0o700)
        self.assert_installed(d)
        self.assertIn(f"{peer}: new: hook.py coordination.py", out)
        owner_reads = [line for line in self.sudo_log.read_text().splitlines() if line.startswith(self.user + " ")]
        self.assertEqual(len(owner_reads), len(FILES), "one read per staged file, as its owner")

    def test_a_symlinked_backup_name_is_refused_and_nothing_is_written_through_it(self):
        # Ember's blocker 3 on #93: at 6a46981 `cp` wrote through a link planted at previous/<stamp>/<file> and the
        # output said the copy was kept. Now an existing backup name, link or file, is refused before any change.
        (self.d / "previous" / STAMP).mkdir(parents=True)
        (self.d / "previous" / STAMP / "hook.py").symlink_to(self.canary / "secret")
        (self.d / "hook.py").write_text("old hook\n")
        before = self.canary_state()
        r = self.run_script(check=False, lock=False)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertEqual(self.canary_state(), before)
        self.assertIn(f"{self.user}: hook.py: refused: {self.d}/previous/{STAMP}/hook.py is already there", r.stderr)
        self.assertIn("nothing installed", r.stderr)
        self.assertNotIn("replaced", r.stdout)
        self.assertEqual((self.d / "hook.py").read_text(), "old hook\n")
        self.assertFalse((self.d / "coordination.py").exists())
        (self.d / "previous" / STAMP / "hook.py").unlink()
        (self.d / "previous" / STAMP / "hook.py").write_text("an earlier copy\n")
        r = self.run_script(check=False)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertEqual((self.d / "previous" / STAMP / "hook.py").read_text(), "an earlier copy\n")
        self.assertEqual((self.d / "hook.py").read_text(), "old hook\n")

    def test_a_backup_name_that_appears_after_the_checks_is_not_written_through(self):
        # Ember's blocker 3, the race inside the user's own step: a link appears at previous/<stamp>/hook.py after the
        # checks. The copy goes to a fresh temp name and is hard-linked to its name, which fails on anything there, so
        # nothing is written through it, the run stops, and nothing says the copy was kept.
        (self.d / "hook.py").write_text("old hook\n")
        leaf = self.d / "previous" / STAMP / "hook.py"
        self.env["PLANT_LINK"] = f"{leaf}={self.canary / 'secret'}"
        before = self.canary_state()
        r = self.run_script(check=False, lock=False)
        self.assertEqual(self.canary_state(), before)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn(f"STOPPED at {self.user}: hook.py: ", r.stderr)
        self.assertNotIn("replaced", r.stdout + r.stderr)
        self.assertEqual((self.d / "hook.py").read_text(), "old hook\n")
        self.assertEqual(sorted(p.name for p in leaf.parent.iterdir()), ["hook.py"], "no temp backup left")

    def test_a_symlinked_hook_folder_is_refused(self):
        self.d.rename(self.tmp / "elsewhere")
        self.d.symlink_to(self.canary, target_is_directory=True)
        before = self.canary_state()
        r = self.run_script(check=False, lock=False)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn(f"{self.d} is a symlink or not a directory", r.stderr)
        self.assertEqual(self.canary_state(), before)

    def test_a_file_name_that_isnt_a_plain_name_is_refused(self):
        for bad in ("../hook.py", ".hidden", "a/b"):
            r = self.run_script(files=["hook.py", bad], check=False)
            self.assertEqual(r.returncode, 1, bad)
            self.assertIn(f"the file name '{bad}' isn't a plain name; nothing installed", r.stderr)
            self.assertFalse(self.sudo_log.exists(), bad)
            self.assertEqual(list(self.d.iterdir()), [], bad)

    def test_a_problem_for_a_later_user_stops_every_change_for_an_earlier_one(self):
        # Ember's blocker 2: every user and file is checked before anything changes.
        peer = "peer-not-the-stage-owner"
        d = self.homes / peer / ".claude" / "persistence"
        d.mkdir(parents=True)
        (d / "hook.py").write_text("old hook\n")
        (d / "previous").write_text("not a directory\n")
        (self.d / "hook.py").write_text("old hook\n")
        r = self.run_script(users=[self.user, peer], check=False)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertEqual(r.stdout, "")
        self.assertIn(f"{peer}: hook.py: refused: {d}/previous is a symlink or not a directory", r.stderr)
        self.assertIn("nothing installed: 1 problem(s) found before any change", r.stderr)
        self.assertEqual(sorted(p.name for p in self.d.iterdir()), ["hook.py"])
        self.assertEqual((self.d / "hook.py").read_text(), "old hook\n")

    def test_a_failure_after_the_checks_stops_and_names_what_landed_and_what_was_left(self):
        # Ember's blocker 2: an unexpected failure (here the shim's mv fails on one path) stops the run at once. The
        # report names the file that failed, what had landed, and every user and file not attempted.
        peer = "peer-not-the-stage-owner"
        files = ["hook.py", "coordination.py", "claims.py"]
        (self.stage / "claims.py").write_text("new claims.py\n")
        dp = self.homes / peer / ".claude" / "persistence"
        dp.mkdir(parents=True)
        (self.d / "hook.py").write_text("old hook\n")
        (self.d / "coordination.py").write_text("old coordination\n")
        self.env["FAIL_ON"] = str((self.d / "coordination.py").resolve())
        self.env["FAIL_CMD"] = "cp"  # the backup's copy fails, so neither the backup nor the new version landed
        r = self.run_script(users=[self.user, peer], files=files, check=False)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertEqual(r.stdout, "")
        lines = r.stderr.splitlines()
        self.assertTrue(lines[0].startswith(f"STOPPED at {self.user}: coordination.py: "), lines)
        self.assertIn("test shim: injected failure", lines[0] + lines[1])
        tail = [line for line in lines if not line.startswith(("STOPPED", "test shim", "mv:"))]
        self.assertEqual(tail, [
            self.seen("coordination.py", "a regular file, not the new version", "absent"),
            f"{self.user}: replaced: hook.py (the copies they replaced are in {self.d}/previous/{STAMP})",
            f"not attempted, {self.user}: claims.py",
            f"not attempted, {peer}: hook.py coordination.py claims.py"])
        self.assertEqual((self.d / "hook.py").read_text(), "new hook.py\n")
        self.assertEqual((self.d / "coordination.py").read_text(), "old coordination\n")
        self.assertFalse((self.d / "claims.py").exists())
        self.assertEqual(list(dp.iterdir()), [])
        self.assertEqual([p.name for p in self.d.iterdir() if p.name.startswith(".")], [], "no temp file left")

    def seen(self, f, target, backup, d=None):
        d = d or self.d
        return (f"{self.user}: {f}, seen as {self.user} after the failure: {d}/{f} is {target}; "
                f"the backup's name {d}/previous/{STAMP}/{f}: {backup}")

    def failed_replacement(self, **env):
        # One file to replace, made to fail somewhere in its install step; returns the report's lines after STOPPED.
        (self.d / "hook.py").write_text("old hook\n")
        self.env.update(env)
        r = self.run_script(files=["hook.py"], check=False)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertEqual(r.stdout, "")
        lines = r.stderr.splitlines()
        self.assertTrue(lines[0].startswith(f"STOPPED at {self.user}: hook.py: "), lines)
        self.assertNotIn("as it was", r.stderr)
        return [line for line in lines if not line.startswith(("STOPPED", "test shim", "mv:", "ln:", "cp:"))]

    def test_a_failure_after_the_backup_landed_says_it_landed(self):
        # Ember at 735f69b: the step failed after ln published the backup, before mv. The old report omitted the
        # backup. Now it's looked at: there, and the target isn't the new version.
        tail = self.failed_replacement(FAIL_ON=str((self.d / "hook.py").resolve()), FAIL_CMD="mv")
        self.assertEqual(tail, [self.seen("hook.py", "a regular file, not the new version", "there (a regular file)"),
                                f"{self.user}: no hook file changed"])
        self.assertEqual((self.d / "hook.py").read_text(), "old hook\n")
        self.assertEqual((self.d / "previous" / STAMP / "hook.py").read_text(), "old hook\n", "the backup is kept")

    def test_a_failure_after_the_rename_says_the_new_version_is_there(self):
        # Ember at 735f69b: mv succeeded and the step failed before it said "replaced"; the old report said the target
        # "is as it was", which was false.
        # Ember at 15cfe32: the summary under it still said "no hook file changed", which was false (blocker 4 back on
        # the failure path). The file seen as the new version is counted as changed.
        tail = self.failed_replacement(FAIL_AFTER=str((self.d / "hook.py").resolve()), FAIL_CMD="mv")
        self.assertEqual(tail, [self.seen("hook.py", "the new version: the rename happened", "there (a regular file)"),
                                f"{self.user}: changed, seen after the failure: hook.py (the new version is there)"])
        self.assertNotIn("no hook file changed", "\n".join(tail))
        self.assertEqual((self.d / "hook.py").read_text(), "new hook.py\n")

    def test_a_look_that_fails_says_unknown_not_a_guess(self):
        tail = self.failed_replacement(FAIL_ON=str((self.d / "hook.py").resolve()), FAIL_CMD="mv", FAIL_AS="inspect")
        why = f"unknown (looking as {self.user} failed: test shim: injected failure running inspect as {self.user})"
        # A look that couldn't tell may not be followed by a claim that nothing changed (Ember at 15cfe32).
        self.assertEqual(tail, [self.seen("hook.py", why, why),
                                f"{self.user}: unknown whether it changed: hook.py (it couldn't be looked at)"])
        self.assertNotIn("no hook file changed", "\n".join(tail))

    def test_a_rename_that_landed_before_a_failure_counts_beside_earlier_files(self):
        # The failed file's observed change joins what landed before it, in one summary line.
        (self.d / "coordination.py").write_text("old coordination\n")
        self.env.update(FAIL_AFTER=str((self.d / "coordination.py").resolve()), FAIL_CMD="mv")
        r = self.run_script(check=False)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn(f"{self.user}: new: hook.py; changed, seen after the failure: coordination.py (the new version "
                      f"is there)\n", r.stderr)
        self.assertNotIn("no hook file changed", r.stderr)

    def test_a_path_that_cant_be_looked_at_is_unknown_not_absent(self):
        # stat failing for any reason but "no such file" (here the stamp dir can't be searched during the look) is
        # reported with stat's words, never as absent.
        sd = self.d / "previous" / STAMP
        tail = self.failed_replacement(FAIL_ON=str((self.d / "hook.py").resolve()), FAIL_CMD="mv",
                                       LOCK_FOR_INSPECT=str(sd))
        self.assertEqual(len(tail), 2, tail)
        self.assertTrue(tail[0].startswith(self.seen("hook.py", "a regular file, not the new version", "unknown (")),
                        tail)
        self.assertIn("Permission denied", tail[0])
        self.assertEqual((sd / "hook.py").read_text(), "old hook\n")

    def test_a_symlinked_target_is_reported_as_the_link_not_what_it_points_at(self):
        before = self.canary_state()
        (self.d / "hook.py").symlink_to(self.canary / "secret")
        self.env.update(FAIL_ON=str(self.d / "hook.py"), FAIL_CMD="cp")
        r = self.run_script(files=["hook.py"], check=False, lock=False)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn(self.seen("hook.py", "a symbolic link, not the new version", "absent"), r.stderr)
        self.assertEqual(self.canary_state(), before)

    def test_a_run_that_only_adds_new_files_says_so(self):
        # Ember's blocker 4: at 6a46981 a run whose files were all new said "no hook file changed".
        out = self.run_script().stdout
        self.assertEqual(out, f"{self.user}: new: hook.py coordination.py\n")
        (self.d / "hook.py").write_text("old hook\n")
        (self.d / "coordination.py").unlink()
        (self.stage / "claims.py").write_text("new claims.py\n")
        (self.d / "claims.py").write_text("new claims.py\n")
        out = self.run_script(files=FILES + ["claims.py"]).stdout
        self.assertEqual(out, f"{self.user}: new: coordination.py; replaced: hook.py (the copies they replaced are in "
                              f"{self.d}/previous/{STAMP}); same: claims.py\n")

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
