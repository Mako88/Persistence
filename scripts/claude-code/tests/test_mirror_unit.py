"""The mirror of main that Arden pulls from (persistence-mirror.service). These tests run the unit's own ExecStart
against temporary repositories, so they test the text that gets installed, not a copy of it.

Why (2026-11-13): the mirror fetched into Claude's working checkout and pushed refs/remotes/origin/main from there.
The checkout's reference-transaction hook also pushes origin/main to the mirror, in the background, whenever it moves,
so a run whose fetch advanced main raced the hook's push into the same ref. It failed twice with "incorrect old value
provided" (2026-11-10 10:18, 2026-11-13 13:52 server time), each time in a run that advanced main; rebuilt in temporary
repositories, the old command with that hook failed 17 of 20 such runs. The fix is that the mirror is the only writer
of its refs (no checkout, no push, and the hook moved aside when this unit is installed), and runs of it are serialised."""
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
RUNBOOK = UNIT.with_name("persistence-mirror.md")


def unit():
    """(environment, inner shell command) from the installed unit's text."""
    env, command = {}, None
    for line in UNIT.read_text().splitlines():
        if line.startswith("Environment="):
            (assignment,) = shlex.split(line[len("Environment="):])  # systemd quotes an assignment with spaces whole
            key, value = assignment.split("=", 1)
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
        self.assertEqual(env, {
            "MIRROR": "/srv/persistence.git",
            "UPSTREAM": "git@github.com:Mako88/Persistence.git",
            # No agent and no terminal under systemd: one pinned key, and a failure instead of a prompt.
            "GIT_SSH_COMMAND": "ssh -i /home/ubuntu/.ssh/id_ed25519 -o IdentitiesOnly=yes -o BatchMode=yes",
        })
        # No working checkout, no push: the mirror's refs have no other writer.
        self.assertNotIn("/home/", command)
        self.assertNotIn("push", command)
        self.assertIn('flock -w 120 "$MIRROR"', command)
        self.assertIn("--no-tags", command)

    def test_install_step_1_checks_the_units_own_fetch_path(self):
        # The tests below fetch from local paths, so SSH as ubuntu is checked only by install step 1 (Arden, review
        # 5900691699). It must use the unit's exact URL and SSH command, or it checks something else.
        env, _ = unit()
        runbook = RUNBOOK.read_text()
        step1 = runbook[runbook.index("# 1. "):runbook.index("# 2. ")]
        self.assertIn(f"GIT_SSH_COMMAND='{env['GIT_SSH_COMMAND']}'", step1)
        self.assertIn(f"git ls-remote {env['UPSTREAM']} refs/heads/main", step1)
        self.assertIn("sudo -u ubuntu env -i", step1)
        # Neither the old unit nor the hook is versioned: kept, never deleted.
        # The only removal is step 6's own temporary directory (Arden's nit, review 5901571578).
        self.assertEqual([l.strip() for l in runbook.splitlines() if "rm " in l], ['rm -r "$NEW"'])
        self.assertIn('mv "$HOOK" "$HOOK.disabled-$STAMP"', runbook)
        self.assertIn('mv "$HOOK.disabled-$STAMP" "$HOOK"', runbook)

    def test_install_step_6_replaces_existing_units(self):
        # The first install failed here (2026-11-14): `git show | install /dev/stdin "$UNITS/$f"` exits 1 with "No such
        # file or directory" under uutils coreutils whenever the destination exists, and on home it always does. So run
        # step 6's own lines, against old units, with sudo shimmed to drop only the root ownership.
        runbook = RUNBOOK.read_text()
        step6 = runbook[runbook.index("# 6. "):runbook.index("# 7. ")]
        step6 = step6.replace("sudo systemctl daemon-reload", "")
        self.assertNotIn("/dev/stdin", "\n".join(l for l in step6.splitlines() if not l.lstrip().startswith("#")))
        root = Path(self.tmp.name)
        repo, units, bin_ = root / "repo", root / "units", root / "bin"
        git("init", "-q", "-b", "main", str(repo))
        for key, value in (("user.name", "t"), ("user.email", "t@example.invalid"), ("commit.gpgsign", "false")):
            git("config", key, value, cwd=repo)
        (repo / "scripts/claude-code/systemd").mkdir(parents=True)
        for f in ("persistence-mirror.service", "persistence-mirror.timer"):
            shutil.copy(UNIT.with_name(f), repo / "scripts/claude-code/systemd" / f)
        git("add", ".", cwd=repo)
        git("commit", "-q", "-m", "units", cwd=repo)
        merged = git("rev-parse", "HEAD", cwd=repo)
        git("clone", "-q", "--bare", str(repo), str(root / "origin.git"))
        git("remote", "add", "origin", str(root / "origin.git"), cwd=repo)
        units.mkdir()
        for f in ("persistence-mirror.service", "persistence-mirror.timer"):
            (units / f).write_text("old\n")
        bin_.mkdir()
        sudo = bin_ / "sudo"
        sudo.write_text('#!/bin/sh\nfor a; do shift; case "$a" in -o|-g) skip=1;; *) [ -n "$skip" ] && skip= || set -- "$@" "$a";; esac; done\nexec "$@"\n')
        sudo.chmod(0o755)
        env = {**os.environ, "PATH": f"{bin_}:{os.environ['PATH']}", "TMPDIR": str(root)}
        script = f'set -eu\nSTAMP=test\nREPO={shlex.quote(str(repo))}\nUNITS={shlex.quote(str(units))}\nMERGED={merged}\n{step6}'
        result = subprocess.run(["/bin/bash", "-c", script], env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        for f in ("persistence-mirror.service", "persistence-mirror.timer"):
            self.assertEqual((units / f).read_text(), UNIT.with_name(f).read_text())
        self.assertEqual(list(root.glob("persistence-mirror-test.*")), [], "step 6 leaves its temporary directory")

    def test_runbook_runs_other_users_commands_from_root(self):
        # The second install failed here (2026-11-14, STAMP 20261114T042450Z): step 7's `sudo -u arden git ls-remote`
        # ran in ubuntu's checkout, which arden can't stat, and git exited 128. sudo keeps the caller's cwd, so every
        # line that runs as another user must cd / first. Run each such line from an unreadable-to-them cwd, with sudo
        # shimmed to record the cwd it was started in, then do nothing.
        lines = [l.strip() for l in RUNBOOK.read_text().splitlines()
                 if "sudo -u " in l and "sudo -u ubuntu" not in l and not l.lstrip().startswith("#")]
        self.assertTrue(lines, "step 7 checks the mirror as arden")
        root = Path(self.tmp.name)
        bin_, record, here = root / "bin", root / "cwd", root / "checkout"
        bin_.mkdir()
        here.mkdir()
        sudo = bin_ / "sudo"
        sudo.write_text(f'#!/bin/sh\npwd >> {shlex.quote(str(record))}\n')
        sudo.chmod(0o755)
        env = {**os.environ, "PATH": f"{bin_}:{os.environ['PATH']}"}
        for line in lines:
            record.unlink(missing_ok=True)
            result = subprocess.run(["/bin/bash", "-c", f"set -eu\n{line}"], cwd=here, env=env, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(record.read_text().splitlines(), ["/"], line)

    def test_runbook_install_and_rollback_run_whole(self):
        # Arden (1571018877792550974): after the arden check, step 7 had never run, and neither had any rollback line
        # under test, so a third install would find the next untested line live. So run the install (steps 2-7) and
        # then the rollback, as the runbook's own text, against scratch paths: a scratch checkout with its hook,
        # scratch unit and backup directories, and this test's mirror and upstream. sudo and systemctl are fakes
        # (FAKES below); `systemctl start persistence-mirror.service` runs the ExecStart of the unit loaded at the last
        # daemon-reload, so the install has to have put the new unit there for the mirror to move. Step 1 is left
        # out: it is the live SSH check (test_install_step_1_...), and it ran for real in both installs.
        # Not checked: root permission on files (the fakes run as the caller), and the real systemd's behaviour.
        root = Path(self.tmp.name)
        repo, units, backups, bin_, state = root / "repo", root / "units", root / "backups", root / "bin", root / "state"
        git("clone", "-q", str(self.upstream), str(repo))
        hook = repo / ".git/hooks/reference-transaction"
        hook.write_text("#!/bin/sh\n# the old second writer\n")
        # Upstream's main becomes the merge commit of "this PR": it carries the new units.
        new_units = {f: UNIT.with_name(f).read_text() for f in ("persistence-mirror.service", "persistence-mirror.timer")}
        (self.work / "scripts/claude-code/systemd").mkdir(parents=True)
        for f, text in new_units.items():
            (self.work / "scripts/claude-code/systemd" / f).write_text(text)
        git("add", ".", cwd=self.work)
        git("commit", "-q", "-m", "units", cwd=self.work)
        git("push", "-q", str(self.upstream), "HEAD:main", cwd=self.work)
        merged = git("rev-parse", "HEAD", cwd=self.work)
        # The old unit leaves a mark when it runs, so the rollback's manual run is seen to be the old unit's.
        old_units = {"persistence-mirror.service": "[Service]\nType=oneshot\nExecStart=/bin/sh -c 'touch \"$MIRROR.old-ran\"'\n",
                     "persistence-mirror.timer": "[Timer]\nOnUnitActiveSec=5min\n"}
        units.mkdir()
        for f, text in old_units.items():
            (units / f).write_text(text)
        for d in (bin_, state, backups):
            d.mkdir()
        for name, text in FAKES.items():
            (bin_ / name).write_text(text.replace("PYTHON", sys.executable))
            (bin_ / name).chmod(0o755)
        (state / "in-flight").touch()  # step 3 must wait out a run already going
        env = {**os.environ, "PATH": f"{bin_}:{os.environ['PATH']}", "TMPDIR": str(root), "FAKE_STATE": str(state),
               "FAKE_UNITS": str(units), "FAKE_MIRROR": str(self.mirror), "FAKE_UPSTREAM": str(self.upstream)}
        subprocess.run([str(bin_ / "systemctl"), "daemon-reload"], env={**env, "FAKE_ROOT": "1"}, check=True)
        subprocess.run([str(bin_ / "systemctl"), "start", "persistence-mirror.timer"], env={**env, "FAKE_ROOT": "1"},
                       check=True)

        def block(heading):
            text = RUNBOOK.read_text()
            text = text[text.index(f"## {heading}\n"):]
            return text[text.index("```sh\n") + 6:text.index("\n```", text.index("```sh\n"))]

        def substitute(text, old, new, count=None):
            n = text.count(old)
            self.assertTrue(n if count is None else n == count, f"runbook no longer has {old!r} ({n} times)")
            return text.replace(old, new)

        install = block("Install")
        install = install[:install.index("# 1. ")] + install[install.index("# 2. "):]
        install = substitute(install, "REPO=/home/ubuntu/work/Persistence", f"REPO={shlex.quote(str(repo))}", 1)
        install = substitute(install, "UNITS=/etc/systemd/system", f"UNITS={shlex.quote(str(units))}", 1)
        install = substitute(install, "BACKUP=/var/backups/", f"BACKUP={shlex.quote(str(backups))}/", 1)
        install = substitute(install, "MERGED=<the merge commit of this PR, named in the room>", f"MERGED={merged}", 1)
        install = substitute(install, "/srv/persistence.git", shlex.quote(str(self.mirror)))
        self.assertNotIn("/srv/", install)
        self.assertNotIn("/etc/", install)
        result = subprocess.run(["/bin/bash", "-c", install], cwd=repo, env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        stamp = result.stdout.splitlines()[0].removeprefix("STAMP=")
        calls = (state / "calls").read_text().splitlines()
        self.assertEqual(self.mirrored(), merged, "the manual run in step 7 used the new unit")
        self.assertEqual(calls[-2:], ["root start persistence-mirror.timer", "user list-timers persistence-mirror.timer"])
        self.assertEqual((state / "loaded-persistence-mirror.service").read_text(),
                         new_units["persistence-mirror.service"])
        self.assertTrue((state / "active-persistence-mirror.timer").exists())
        self.assertFalse((state / "in-flight").exists(), "step 3 waited on the service")
        self.assertFalse(Path(f"{self.mirror}.old-ran").exists())
        self.assertEqual((state / "as-arden").read_text().splitlines(), ["/"])
        self.assertFalse(hook.exists())
        self.assertTrue((hook.parent / f"reference-transaction.disabled-{stamp}").exists())
        self.assertEqual({f: (backups / f"persistence-mirror-{stamp}" / f).read_text() for f in old_units}, old_units)
        self.assertEqual(list(root.glob(f"persistence-mirror-{stamp}.*")), [])

        rollback = block("Rollback")
        rollback = substitute(rollback, "STAMP=<from the room>", f"STAMP={stamp}", 1)
        rollback = substitute(rollback, "REPO=/home/ubuntu/work/Persistence", f"REPO={shlex.quote(str(repo))}", 1)
        rollback = substitute(rollback, "UNITS=/etc/systemd/system", f"UNITS={shlex.quote(str(units))}", 1)
        rollback = substitute(rollback, "BACKUP=/var/backups/", f"BACKUP={shlex.quote(str(backups))}/", 1)
        self.assertNotIn("/srv/", rollback)
        self.assertNotIn("/etc/", rollback)
        (state / "calls").unlink()
        (state / "in-flight").touch()  # a timer run in flight again
        result = subprocess.run(["/bin/bash", "-c", rollback], cwd=repo, env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stdout.splitlines()[-1], "success")
        self.assertEqual({f: (units / f).read_text() for f in old_units}, old_units)
        self.assertEqual((state / "loaded-persistence-mirror.service").read_text(), old_units["persistence-mirror.service"])
        self.assertEqual((state / "calls").read_text().splitlines()[-1], "root start persistence-mirror.timer")
        self.assertTrue((state / "active-persistence-mirror.timer").exists())
        self.assertFalse((state / "in-flight").exists(), "the rollback waited on the service")
        self.assertTrue(Path(f"{self.mirror}.old-ran").exists(), "the rollback's manual run ran the old unit")
        self.assertTrue(hook.exists())
        self.assertTrue((backups / f"persistence-mirror-{stamp}").is_dir(), "the backup is left in place")

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

    def test_tags_are_not_mirrored(self):
        # Only main has one writer here; a followed tag would be a ref nobody decided to publish (Arden's minor point).
        self.advance()
        git("tag", "t1", cwd=self.work)
        git("push", "-q", str(self.upstream), "t1", cwd=self.work)
        self.assertEqual(self.run_unit().returncode, 0)
        self.assertEqual(git("--git-dir", str(self.mirror), "tag", "--list"), "")

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


# Fakes for test_runbook_install_and_rollback_run_whole. They fail where the real thing would, for what the runbook
# could get wrong: a mistyped verb or unit, a systemctl that changes state without sudo, a unit file changed on disk
# but not daemon-reloaded (systemd keeps running the loaded one), another user's command run from the caller's cwd.
FAKES = {
    # sudo: drops install's -o/-g (the test isn't root) and marks the command as root; `-u arden` records its cwd.
    "sudo": r"""#!/bin/sh
if [ "$1" = -u ]; then
  [ "$2" = arden ] && pwd >> "$FAKE_STATE/as-arden"
  shift 2
  exec "$@"
fi
for a; do shift; case "$a" in -o|-g) skip=1;; *) [ -n "$skip" ] && skip= || set -- "$@" "$a";; esac; done
FAKE_ROOT=1 exec "$@"
""",
    "systemctl": r"""#!PYTHON
import os, shlex, subprocess, sys
from pathlib import Path
state, units = Path(os.environ["FAKE_STATE"]), Path(os.environ["FAKE_UNITS"])
args = sys.argv[1:]
root = os.environ.get("FAKE_ROOT") == "1"
with open(state / "calls", "a") as calls:
    print("root" if root else "user", *args, file=calls)
def unit(name):
    if not (state / f"loaded-{name}").exists():
        sys.exit(f"Unit {name} not found.")
    return name
def need_root():
    if not root:
        sys.exit("Failed: Access denied (interactive authentication required)")
verb = args[0] if args else ""
if verb == "daemon-reload" and len(args) == 1:
    need_root()
    for f in units.glob("persistence-mirror.*"):
        (state / f"loaded-{f.name}").write_text(f.read_text())
elif verb == "stop" and len(args) == 2:
    need_root()
    (state / f"active-{unit(args[1])}").unlink(missing_ok=True)
elif verb == "start" and len(args) == 2:
    need_root()
    name = unit(args[1])
    if name.endswith(".timer"):
        (state / f"active-{name}").touch()
        sys.exit(0)
    env, command = dict(os.environ), None
    for line in (state / f"loaded-{name}").read_text().splitlines():
        if line.startswith("Environment="):
            (assignment,) = shlex.split(line[len("Environment="):])
            key, value = assignment.split("=", 1)
            env[key] = value
        elif line.startswith("ExecStart="):
            command = shlex.split(line[len("ExecStart="):])
    env.update(MIRROR=os.environ["FAKE_MIRROR"], UPSTREAM=os.environ["FAKE_UPSTREAM"])
    ok = subprocess.run(command, env=env).returncode == 0
    (state / f"result-{name}").write_text("success" if ok else "exit-code")
    sys.exit(0 if ok else 1)
elif args[:2] == ["is-active", "-q"] and len(args) == 3:
    unit(args[2])
    flight = state / "in-flight"  # a run in progress: active once, then done
    if flight.exists():
        flight.unlink()
        sys.exit(0)
    sys.exit(3)
elif args[:3] == ["show", "-p", "Result"] and args[3:4] == ["--value"] and len(args) == 5:
    result = state / f"result-{unit(args[4])}"
    print(result.read_text() if result.exists() else "success")
elif verb == "list-timers" and len(args) == 2:
    name = unit(args[1])
    print(name, "active" if (state / f"active-{name}").exists() else "inactive")
else:
    sys.exit(f"fake systemctl: unknown command {args}")
""",
}


if __name__ == "__main__":
    unittest.main()
