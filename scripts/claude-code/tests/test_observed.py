"""A bigger "Observed now": pluggable sources (docs/design/observed-now-sources.md).

Each source is read at the wake and never stored; a source that can't read says so in one line; nothing is on
unless the store's own settings name it."""
import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import hook  # noqa: E402
import observed  # noqa: E402


def ctx(state, values=None, run=None, now=None):
    values = values or {}
    return observed.Context(state=Path(state), setting=lambda name, default: values.get(name, default),
                            run=run or (lambda *a, **k: ""), now=now or datetime.now(timezone.utc), env=values)


class Runner(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()

    def test_off_by_default_no_source_is_called(self):
        boom = mock.Mock(side_effect=AssertionError("called"))
        with mock.patch.dict(observed.SOURCES, {"prs": boom}):
            self.assertEqual(observed.lines({}, ctx(self.tmp)), [])
        boom.assert_not_called()

    def test_a_failing_source_is_one_line_and_the_rest_still_show(self):
        def bad(event, c):
            raise observed.Unreadable("no token")

        def worse(event, c):
            raise KeyError("x")

        good = lambda event, c: ["- good: fine"]
        with mock.patch.dict(observed.SOURCES, {"a": bad, "b": good, "c": worse}, clear=True):
            out = observed.lines({}, ctx(self.tmp, {"PERSISTENCE_OBSERVE_SOURCES": "a, b,c"}))
        self.assertEqual(out, ["- a: couldn't read (no token)", "- good: fine", "- c: couldn't read (KeyError)"])

    def test_a_slow_source_times_out(self):
        slow = lambda event, c: time.sleep(3) or ["- late"]
        with mock.patch.dict(observed.SOURCES, {"slow": slow}, clear=True):
            began = time.monotonic()
            out = observed.lines({}, ctx(self.tmp, {"PERSISTENCE_OBSERVE_SOURCES": "slow", "PERSISTENCE_OBSERVE_SOURCE_SECONDS": "0.3"}))
        self.assertEqual(out, ["- slow: couldn't read (timed out)"])
        self.assertLess(time.monotonic() - began, 2, "the block doesn't wait for a slow source")

    def test_an_unknown_name_is_said(self):
        with mock.patch.dict(observed.SOURCES, {}, clear=True):
            self.assertEqual(observed.lines({}, ctx(self.tmp, {"PERSISTENCE_OBSERVE_SOURCES": "nope"})),
                             ["- observe: no source named 'nope'"])

    def test_observe_carries_the_sources(self):
        with tempfile.TemporaryDirectory() as tmp, \
                mock.patch.object(observed, "lines", return_value=["- prs: SOURCELINE"]), \
                mock.patch.object(hook, "note_line", return_value=None), \
                mock.patch.object(hook, "room_freshness", return_value=None), \
                mock.patch.object(hook, "linux_processes", return_value="[]"), \
                mock.patch.object(hook, "own_services", return_value=[]), \
                mock.patch.object(hook, "run_quiet", return_value=""), \
                mock.patch.object(hook, "project", return_value="work"):
            text = hook.observe({"session_id": "s-1", "cwd": tmp})
        self.assertIn("- prs: SOURCELINE", text)


PULLS = [{"number": 20, "title": "CHANGELOG: bench-only caveat", "head": {"sha": "bec95e7aaaa", "ref": "claude/changelog"},
          "updated_at": "2026-11-13T01:45:00Z", "draft": False}]
REVIEWS = [{"user": {"login": "arden-bot"}, "state": "COMMENTED", "submitted_at": "2026-11-13T02:00:00Z"},
           {"user": {"login": "arden-bot"}, "state": "APPROVED", "submitted_at": "2026-11-13T03:00:00Z"}]


class Prs(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.secrets = Path(self.tmp) / "accounts"
        self.secrets.write_text("Password: hunter2\nToken: tok123\nOther: x\n")
        self.values = {"PERSISTENCE_OBSERVE_GITHUB_TOKEN_FILE": str(self.secrets)}

    def fake(self, calls):
        def get(url, token, timeout):
            calls.append((url, token))
            return REVIEWS if "/reviews" in url else PULLS
        return get

    def test_lists_open_prs_with_head_and_latest_review_per_reviewer(self):
        calls = []
        with mock.patch.object(observed, "github_get", self.fake(calls)):
            out = observed.prs({}, ctx(self.tmp, self.values))
        self.assertEqual(len(out), 1)
        self.assertIn("#20", out[0]); self.assertIn("bec95e7", out[0]); self.assertIn("arden-bot APPROVED", out[0])
        self.assertNotIn("COMMENTED", out[0], "only each reviewer's latest review")
        self.assertTrue(all(token == "tok123" for _, token in calls), "only the Token: line is used")

    def test_cached_inside_the_ttl(self):
        calls = []
        with mock.patch.object(observed, "github_get", self.fake(calls)):
            first = observed.prs({}, ctx(self.tmp, self.values))
            n = len(calls)
            again = observed.prs({}, ctx(self.tmp, self.values))
        self.assertEqual(first, again)
        self.assertEqual(len(calls), n, "a second prompt inside the TTL doesn't call GitHub")

    def test_offline_shows_the_stale_cache_with_its_age(self):
        with mock.patch.object(observed, "github_get", self.fake([])):
            observed.prs({}, ctx(self.tmp, self.values))
        later = datetime.now(timezone.utc) + timedelta(minutes=10)
        with mock.patch.object(observed, "github_get", side_effect=OSError("offline")):
            out = observed.prs({}, ctx(self.tmp, self.values, now=later))
        self.assertIn("#20", out[-1])
        self.assertIn("cached 10m ago", out[0])

    def test_offline_with_no_cache_cannot_read(self):
        with mock.patch.object(observed, "github_get", side_effect=OSError("offline")):
            with self.assertRaises(observed.Unreadable):
                observed.prs({}, ctx(self.tmp, self.values))

    def test_no_token_cannot_read(self):
        self.secrets.write_text("Password: hunter2\n")
        with mock.patch.object(observed, "github_get", side_effect=AssertionError("called without a token")):
            with self.assertRaises(observed.Unreadable) as e:
                observed.prs({}, ctx(self.tmp, self.values))
        self.assertNotIn("hunter2", str(e.exception))


def git(repo, *args):
    return subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True, check=True).stdout.strip()


class Deployed(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.repo = self.tmp / "repo"
        (self.repo / "scripts" / "claude-code").mkdir(parents=True)
        git(self.repo, "init", "-q", "-b", "main")
        git(self.repo, "config", "user.email", "t@t"); git(self.repo, "config", "user.name", "t")
        for f in observed.HOOK_FILES:
            (self.repo / "scripts" / "claude-code" / f).write_text(f"# {f}\n")
        git(self.repo, "add", "."); git(self.repo, "commit", "-qm", "one")
        self.first = git(self.repo, "rev-parse", "HEAD")
        (self.repo / "scripts" / "claude-code" / "hook.py").write_text("# hook.py v2\n")
        git(self.repo, "commit", "-qam", "two")
        self.installed = self.tmp / "installed"
        self.installed.mkdir()
        for f in observed.HOOK_FILES:
            (self.installed / f).write_text(f"# {f}\n")
        self.marker = self.tmp / "DEPLOYED"
        self.values = {"PERSISTENCE_OBSERVE_REPO": str(self.repo), "PERSISTENCE_OBSERVE_STORE_MARKER": str(self.marker)}

    def run_it(self):
        with mock.patch.object(observed, "INSTALLED", self.installed):
            return observed.deployed({}, ctx(self.tmp, self.values, run=hook.run_quiet))

    def test_installed_hooks_that_differ_from_main_are_named(self):
        out = "\n".join(self.run_it())
        self.assertIn("hooks", out); self.assertIn("hook.py", out); self.assertIn("differ from main", out)

    def test_matching_hooks_say_so(self):
        (self.installed / "hook.py").write_text("# hook.py v2\n")
        self.assertIn("hooks: match main", "\n".join(self.run_it()))

    def test_a_hook_file_main_has_but_not_installed_is_missing(self):
        (self.installed / "hook.py").write_text("# hook.py v2\n")
        (self.installed / "run_line.py").unlink()
        out = "\n".join(self.run_it())
        self.assertIn("missing: run_line.py", out); self.assertNotIn("differ", out)

    def test_store_marker_behind_main(self):
        self.marker.write_text(self.first + "\n")
        out = "\n".join(self.run_it())
        self.assertIn(self.first[:7], out); self.assertIn("1 commit behind main", out)

    def test_no_marker_says_it_cannot_tell(self):
        self.assertIn("store: couldn't read which commit", "\n".join(self.run_it()))


class Switches(unittest.TestCase):
    def test_strip_is_named_off_when_unset(self):
        out = "\n".join(observed.switches({}, ctx(tempfile.mkdtemp(), {"PERSISTENCE_RECALL_COUNT": "3"})))
        self.assertIn("STRIP_ROOM_PREAMBLE off", out); self.assertIn("RECALL_COUNT=3", out)

    def test_strip_on_and_the_store_unit_read(self):
        def run(cmd, cwd=None, timeout=10):
            self.assertEqual(cmd[:2], ["systemctl", "show"])
            return "Environment=PERSISTENCE_STORE_DB=/x PERSISTENCE_EMBEDDING_QUERYTAILCHARS=1200 PERSISTENCE_EMBEDDING_QUERYCHUNKCHARS=0\n"
        values = {"PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE": "1", "PERSISTENCE_OBSERVE_STORE_UNIT": "persistence-store"}
        out = "\n".join(observed.switches({}, ctx(tempfile.mkdtemp(), values, run=run)))
        self.assertIn("STRIP_ROOM_PREAMBLE on", out)
        self.assertIn("EMBEDDING_QUERYTAILCHARS=1200", out)
        self.assertNotIn("STORE_DB", out, "only recall and query switches")


class Claims(unittest.TestCase):
    def setUp(self):
        self.state = Path(tempfile.mkdtemp())
        (self.state / "tasks").mkdir()
        self.now = datetime(2026, 11, 13, 4, 0, tzinfo=timezone.utc)

    def claim(self, n, status, minutes_ago, owner="other"):
        when = (self.now - timedelta(minutes=minutes_ago)).isoformat(timespec="seconds")
        cid = f"{n:08d}-0000-4000-8000-000000000000"
        (self.state / "tasks" / f"{cid}.json").write_text(json.dumps(
            {"id": cid, "task": f"task number {n}", "status": status, "updated": when, "owner": owner, "outcome": "ok"}))

    def test_finished_since_last_look_not_own_capped(self):
        self.claim(1, "done", 30); self.claim(2, "abandoned", 20); self.claim(3, "claimed", 10)
        self.claim(4, "done", 5, owner="me"); self.claim(5, "done", 60 * 8)
        for n in range(6, 11):
            self.claim(n, "done", n)
        out = observed.claims_done({"session_id": "me"}, ctx(self.state, now=self.now))
        text = "\n".join(out)
        self.assertNotIn("task number 3", text, "still running")
        self.assertNotIn("task number 4", text, "this session's own")
        self.assertNotIn("task number 5", text, "older than the first look's window")
        self.assertIn("task number 6", text)
        self.assertIn("more", out[-1])
        self.assertLessEqual(len(out), 6)
        # The cursor moved: nothing new on the next look.
        self.assertEqual(observed.claims_done({"session_id": "me"}, ctx(self.state, now=self.now + timedelta(minutes=1))), [])
        self.claim(11, "done", -1.5)  # after the cursor (now+1m), before the look (now+2m)
        later = observed.claims_done({"session_id": "me"}, ctx(self.state, now=self.now + timedelta(minutes=2)))
        self.assertIn("task number 11", "\n".join(later))


class CliName(unittest.TestCase):
    def test_pstore_when_on_path(self):
        with mock.patch.object(hook, "remote", return_value=None), \
                mock.patch("shutil.which", side_effect=lambda n: "/x/pstore" if n == "pstore" else None):
            self.assertEqual(hook.cli_name(), "pstore")

    def test_the_store_exe_name_otherwise(self):
        with mock.patch.object(hook, "remote", return_value=None), mock.patch("shutil.which", return_value=None), \
                mock.patch.dict(os.environ, {"PERSISTENCE_STORE_EXE": ""}):
            self.assertEqual(hook.cli_name(), "persistence-store")

    def test_no_prompt_text_names_a_bare_persistence_store_command(self):
        # The class of mistake: a prompt naming a command the session can't run. On home the CLI is `pstore`, and room
        # sessions may only run pstore, so `persistence-store search|help|show` was refused every time.
        source = Path(hook.__file__).read_text(encoding="utf-8")
        for sub in ("search", "help", "show", "core", "guide"):
            self.assertNotIn(f"`persistence-store {sub}", source)

if __name__ == "__main__":
    unittest.main()
