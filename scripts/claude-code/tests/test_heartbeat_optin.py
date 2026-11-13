"""The opt-in preflight (heartbeat_optin.py): a read-only check that says whether this self is ready to turn its heartbeat
on, and refuses to say "ready" while a switch would fail silently. Fake home, fake systemctl, fake PATH: nothing real
is read or changed, and no test calls a model."""
import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import heartbeat  # noqa: E402
import heartbeat_optin  # noqa: E402

REPO = Path(__file__).resolve().parent.parent


class Systemctl:
    """Answers `show -p LoadState --value -- <unit>` and `is-enabled -- <unit>` only; anything else fails the test. Not
    test_heartbeat_core.Systemctl: that one refuses these two verbs, as the beat must never use them."""

    def __init__(self, missing=(), enabled=()):
        self.missing, self.enabled, self.calls = set(missing), set(enabled), []

    def __call__(self, argv):
        self.calls.append(list(argv))
        assert argv[0] == "systemctl" and argv[-2] == "--", argv
        verb, unit = argv[1], argv[-1]
        if verb == "show" and argv[2:5] == ["-p", "LoadState", "--value"]:
            return 0, ("not-found\n" if unit in self.missing else "loaded\n")
        if verb == "is-enabled" and len(argv) == 4:
            return (0, "enabled\n") if unit in self.enabled else (1, "disabled\n")
        raise AssertionError(f"systemctl {verb} must never run")


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.home = self.tmp / "home"
        self.deployed = self.home / ".claude" / "persistence"
        self.deployed.mkdir(parents=True)
        for name in heartbeat_optin.DEPLOYED:
            shutil.copy(REPO / name, self.deployed / name)
        self.bin = self.tmp / "bin"
        self.bin.mkdir()
        (self.bin / "pheartbeat").write_text('#!/bin/sh\nexec python3 "$HOME/.claude/persistence/heartbeat.py" "$@"\n')
        (self.bin / "pheartbeat").chmod(0o755)
        self.systemctl = Systemctl()
        self.env = {"PERSISTENCE_HEARTBEAT": "true", "PERSISTENCE_TASKCLAIMS": "1"}

    def settings(self):
        (self.home / ".claude" / "settings.json").write_text(json.dumps({"env": self.env}), encoding="utf-8")

    def check(self, user="ubuntu", stalled=False):
        self.settings()
        return heartbeat_optin.check(home=self.home, user=user, repo=REPO, systemctl=self.systemctl,
                                     path=str(self.bin), stalled=stalled)

    def failing(self, items):
        return {i.name for i in items if i.required and not i.ok}


class Ready(Base):
    def test_everything_in_place_is_ready(self):
        items = self.check()
        self.assertEqual(self.failing(items), set())
        self.assertTrue(heartbeat_optin.ready(items))

    def test_stalled_ready_with_the_source_and_the_wrapper(self):
        self.env.update(PERSISTENCE_HEARTBEAT_STALLED="true", PERSISTENCE_OBSERVE_SOURCES="prs,heartbeat")
        self.assertEqual(self.failing(self.check(stalled=True)), set())

    def test_read_only_systemctl_verbs(self):
        self.check(stalled=True)
        self.assertTrue(self.systemctl.calls)
        self.assertLessEqual({c[1] for c in self.systemctl.calls}, {"show", "is-enabled"})

    def test_writes_nothing(self):
        self.settings()
        before = sorted((p, p.stat().st_mtime_ns) for p in self.tmp.rglob("*"))
        heartbeat_optin.check(home=self.home, user="ubuntu", repo=REPO, systemctl=self.systemctl, path=str(self.bin),
                              stalled=True)
        self.assertEqual(sorted((p, p.stat().st_mtime_ns) for p in self.tmp.rglob("*")), before)


class NotReady(Base):
    def test_deployed_file_missing(self):
        (self.deployed / "heartbeat.py").unlink()
        self.assertIn("deployed heartbeat.py", self.failing(self.check()))

    def test_deployed_file_differs_from_the_tree(self):
        (self.deployed / "coordination.py").write_text("# old\n")
        self.assertIn("deployed coordination.py", self.failing(self.check()))

    def test_every_module_heartbeat_imports_is_checked(self):
        # The beat imports these beside itself; an old copy of any of them runs the new heartbeat against old rules.
        self.assertLessEqual({"heartbeat.py", "coordination.py", "claims.py", "hook.py", "observed.py"},
                             set(heartbeat_optin.DEPLOYED))

    def test_unknown_user_has_no_units(self):
        self.assertIn("units", self.failing(self.check(user="nobody")))

    def test_a_unit_in_the_tuple_is_not_installed(self):
        self.systemctl.missing = {"persistence-mirror.service"}
        items = self.check()
        self.assertIn("units", self.failing(items))
        self.assertIn("persistence-mirror.service", next(i for i in items if i.name == "units").detail)

    def test_every_unit_of_the_tuple_is_asked_about(self):
        self.check()
        asked = {c[-1] for c in self.systemctl.calls if c[1] == "show"}
        self.assertLessEqual(set(heartbeat.units_for("ubuntu")), asked)

    def test_timer_not_installed(self):
        self.systemctl.missing = {"persistence-heartbeat@ubuntu.timer"}
        self.assertIn("timer", self.failing(self.check()))

    def test_claims_off_means_no_row_a(self):
        del self.env["PERSISTENCE_TASKCLAIMS"]
        self.assertIn("PERSISTENCE_TASKCLAIMS", self.failing(self.check()))

    def test_claims_true_is_not_on(self):
        # claims.py reads "1" only; "true" would leave row A off while looking on.
        self.env["PERSISTENCE_TASKCLAIMS"] = "true"
        self.assertIn("PERSISTENCE_TASKCLAIMS", self.failing(self.check()))


class TheSilentFailures(Base):
    def test_stalled_on_without_the_observe_source_is_never_ready(self):
        # The footgun: R and Q wakes would fire, never render, and go undeliverable. Caught even without --stalled.
        self.env.update(PERSISTENCE_HEARTBEAT_STALLED="true", PERSISTENCE_OBSERVE_SOURCES="prs,claims")
        self.assertIn("PERSISTENCE_OBSERVE_SOURCES", self.failing(self.check()))
        self.assertIn("PERSISTENCE_OBSERVE_SOURCES", self.failing(self.check(stalled=True)))

    def test_source_name_matched_exactly(self):
        self.env.update(PERSISTENCE_HEARTBEAT_STALLED="1", PERSISTENCE_OBSERVE_SOURCES="heartbeats, prs")
        self.assertIn("PERSISTENCE_OBSERVE_SOURCES", self.failing(self.check()))
        self.env["PERSISTENCE_OBSERVE_SOURCES"] = " prs , heartbeat "
        self.assertNotIn("PERSISTENCE_OBSERVE_SOURCES", self.failing(self.check()))

    def test_stalled_on_with_the_heartbeat_off(self):
        self.env.update(PERSISTENCE_HEARTBEAT="false", PERSISTENCE_HEARTBEAT_STALLED="true",
                        PERSISTENCE_OBSERVE_SOURCES="heartbeat")
        self.assertIn("PERSISTENCE_HEARTBEAT_STALLED", self.failing(self.check()))

    def test_stalled_needs_pheartbeat_on_path(self):
        # The tick's fixed text tells the session to run `pheartbeat detect`/`continue`; without it a tick can't stop.
        (self.bin / "pheartbeat").unlink()
        self.env.update(PERSISTENCE_HEARTBEAT_STALLED="true", PERSISTENCE_OBSERVE_SOURCES="heartbeat")
        self.assertIn("pheartbeat", self.failing(self.check(stalled=True)))

    def test_pheartbeat_not_required_before_stalled(self):
        (self.bin / "pheartbeat").unlink()
        self.assertEqual(self.failing(self.check()), set())

    def test_os_environ_does_not_count(self):
        # The heartbeat reads settings.json only, so the preflight does too.
        self.env = {}
        import os
        from unittest import mock
        with mock.patch.dict(os.environ, {"PERSISTENCE_TASKCLAIMS": "1"}):
            self.assertIn("PERSISTENCE_TASKCLAIMS", self.failing(self.check()))


class Cli(Base):
    def test_exit_status_and_lines(self):
        self.settings()
        out = []
        code = heartbeat_optin.main(["check"], home=self.home, user="ubuntu", repo=REPO, systemctl=self.systemctl,
                                    path=str(self.bin), out=out.append)
        self.assertEqual(code, 0)
        self.assertEqual(out[-1], "ready: PERSISTENCE_HEARTBEAT")
        del self.env["PERSISTENCE_TASKCLAIMS"]
        self.settings()
        out.clear()
        code = heartbeat_optin.main(["check", "--stalled"], home=self.home, user="ubuntu", repo=REPO,
                                    systemctl=self.systemctl, path=str(self.bin), out=out.append)
        self.assertEqual(code, 1)
        self.assertTrue(out[-1].startswith("NOT READY for PERSISTENCE_HEARTBEAT_STALLED:"), out[-1])
        self.assertTrue(any(line.startswith("MISSING PERSISTENCE_TASKCLAIMS") for line in out), out)


if __name__ == "__main__":
    unittest.main()
