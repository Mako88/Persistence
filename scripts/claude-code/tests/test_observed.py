"""A bigger "Observed now": pluggable sources (docs/design/observed-now-sources.md).

Each source returns a structured Observation; one renderer makes the text; reads write nothing and acks run only after
the block is handed over; nothing is on unless the store's own settings name it."""
import json
import os
import subprocess
import sys
import tempfile
import threading
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


def snapshot(root):
    """Every file under root with its bytes and mtime: what a read must leave exactly as it found it."""
    return {str(p): (p.read_bytes(), p.stat().st_mtime_ns) for p in Path(root).rglob("*") if p.is_file()}


NOW = datetime(2026, 11, 13, 4, 0, tzinfo=timezone.utc)
OBS = observed.Observation


def src(read, ack=None):
    return observed.Source(read, ack)


class Runner(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()

    def test_off_by_default_no_source_is_called(self):
        boom = mock.Mock(side_effect=AssertionError("called"))
        with mock.patch.dict(observed.SOURCES, {"prs": src(boom)}):
            self.assertEqual(observed.observe({}, ctx(self.tmp)), [])
        boom.assert_not_called()

    def test_failures_are_statuses_and_the_rest_still_show(self):
        def bad(event, c):
            raise observed.Unreadable("no token")

        def worse(event, c):
            raise KeyError("x")

        good = lambda event, c: OBS("b", c.now, lines=("fine",))
        with mock.patch.dict(observed.SOURCES, {"a": src(bad), "b": src(good), "c": src(worse), "d": src(lambda e, c: ["prose"])},
                             clear=True):
            out = observed.observe({}, ctx(self.tmp, {"PERSISTENCE_OBSERVE_SOURCES": "a, b,c,d"}, now=NOW))
        self.assertEqual([(o.source, o.status, o.reason) for o in out],
                         [("a", "unknown", "no token"), ("b", "ok", ""), ("c", "error", "KeyError"),
                          ("d", "error", "returned list, not an Observation")])

    def test_a_slow_source_times_out_and_its_late_result_is_dropped(self):
        done = threading.Event()

        def slow(event, c):
            time.sleep(0.6)
            done.set()
            return OBS("slow", c.now, lines=("late",))

        with mock.patch.dict(observed.SOURCES, {"slow": src(slow)}, clear=True):
            began = time.monotonic()
            out = observed.observe({}, ctx(self.tmp, {"PERSISTENCE_OBSERVE_SOURCES": "slow", "PERSISTENCE_OBSERVE_SOURCE_SECONDS": "0.2"}))
            self.assertLess(time.monotonic() - began, 0.5, "the block doesn't wait for a slow source")
            done.wait(2); time.sleep(0.05)
        self.assertEqual([(o.status, o.reason, o.lines) for o in out], [("unknown", "timed out", ())])

    def test_an_unknown_name_is_said(self):
        with mock.patch.dict(observed.SOURCES, {}, clear=True):
            out = observed.observe({}, ctx(self.tmp, {"PERSISTENCE_OBSERVE_SOURCES": "nope"}))
        self.assertEqual([(o.status, o.reason) for o in out], [("error", "no source named 'nope'")])


class Render(unittest.TestCase):
    def text(self, *observations, now=NOW):
        return [line for _, said in observed.render(list(observations), now) for line in said]

    def test_ok_is_its_lines(self):
        self.assertEqual(self.text(OBS("prs", NOW, lines=("PR #1 x", "PR #2 y"))), ["- PR #1 x", "- PR #2 y"])

    def test_ok_with_nothing_to_say_is_nothing(self):
        self.assertEqual(self.text(OBS("claims", NOW)), [])

    def test_unknown_and_error_say_status_and_reason_then_any_lines(self):
        self.assertEqual(self.text(OBS("deployed", NOW, status="unknown", reason="no marker", lines=("hooks: match",)),
                                   OBS("c", NOW, status="error", reason="KeyError")),
                         ["- deployed: unknown (no marker)", "- hooks: match", "- c: error (KeyError)"])

    def test_stale_data_carries_its_age(self):
        out = self.text(OBS("prs", NOW, lines=("PR #1",), freshness=NOW - timedelta(minutes=10), reason="GitHub: URLError; cached"))
        self.assertEqual(out, ["- prs: as of 10m ago (GitHub: URLError; cached)", "- PR #1"])
        self.assertEqual(self.text(OBS("prs", NOW, lines=("PR #1",), freshness=NOW - timedelta(seconds=30))), ["- PR #1"])

    def test_bounded_lines_and_truncation(self):
        out = self.text(OBS("x", NOW, lines=tuple(f"l{i}" for i in range(9)) + ("y" * 500,), truncated=2))
        self.assertEqual(len(out), observed.MAX_LINES + 1)
        self.assertEqual(out[-1], f"- x: {2 + 10 - observed.MAX_LINES} more not shown")
        out = self.text(OBS("x", NOW, lines=("y" * 500,)))
        self.assertLessEqual(len(out[0]), observed.MAX_LINE_CHARS + 2)


class Acknowledge(unittest.TestCase):
    def test_acks_ok_observations_with_an_ack_only(self):
        acked = []
        ack = lambda obs, e, c: acked.append(obs.source)
        with mock.patch.dict(observed.SOURCES, {"a": src(None, ack), "b": src(None, ack), "c": src(None, ack), "d": src(None)},
                             clear=True):
            observed.acknowledge([OBS("a", NOW, ack="t"), OBS("b", NOW, status="unknown", ack="t"), OBS("c", NOW),
                                  OBS("d", NOW, ack="t")], {}, ctx(tempfile.mkdtemp()))
        self.assertEqual(acked, ["a"])

    def test_a_failing_ack_doesnt_stop_the_rest(self):
        acked = []

        def boom(obs, e, c):
            raise OSError("ro")
        with mock.patch.dict(observed.SOURCES, {"a": src(None, boom), "b": src(None, lambda o, e, c: acked.append(1))}, clear=True):
            observed.acknowledge([OBS("a", NOW, ack=1), OBS("b", NOW, ack=1)], {}, ctx(tempfile.mkdtemp()))
        self.assertEqual(acked, [1])


class Hook(unittest.TestCase):
    """hook.observe carries the rendered sources; observe_hook acks only after printing, and only what was sent."""

    def patches(self, tmp):
        return [mock.patch.object(hook, "note_line", return_value=None), mock.patch.object(hook, "room_freshness", return_value=None),
                mock.patch.object(hook, "linux_processes", return_value="[]"), mock.patch.object(hook, "own_services", return_value=[]),
                mock.patch.object(hook, "run_quiet", return_value=""), mock.patch.object(hook, "project", return_value="work"),
                mock.patch.object(hook, "inbox", return_value=""), mock.patch.object(hook, "STATE", Path(tmp))]

    def run_hook(self, observations, ack, out=None):
        events = []
        with tempfile.TemporaryDirectory() as tmp:
            ps = self.patches(tmp)
            for p in ps:
                p.start()
            try:
                with mock.patch.object(observed, "observe", return_value=observations), \
                        mock.patch.dict(observed.SOURCES, {o.source: src(None, ack(events)) for o in observations}, clear=True), \
                        mock.patch("sys.stdout", out or __import__("io").StringIO()) as printed:
                    hook.observe_hook({"session_id": "s-1", "cwd": tmp})
                    text = printed.getvalue() if hasattr(printed, "getvalue") else ""
            finally:
                for p in ps:
                    p.stop()
        return text, events

    def test_rendered_then_acked_after_print(self):
        printed = __import__("io").StringIO()
        ack = lambda events: lambda obs, e, c: events.append((obs.source, printed.getvalue()))
        text, events = self.run_hook([OBS("prs", NOW, lines=("SOURCELINE",), ack="x")], ack, printed)
        self.assertIn("- SOURCELINE", text)
        self.assertEqual(len(events), 1)
        self.assertIn("SOURCELINE", events[0][1], "the ack ran after the block was written")

    def test_an_observation_cut_by_cap_is_not_acked(self):
        long = tuple("z" * 190 + str(i) for i in range(observed.MAX_LINES))
        many = [OBS(f"s{i}", NOW, lines=long, ack="x") for i in range(12)]  # ~13.8k chars, over CAP
        text, events = self.run_hook(many, lambda events: lambda obs, e, c: events.append(obs.source))
        self.assertTrue(0 < len(events) < 12, events)
        self.assertNotIn("s11", events, "its lines were cut by CAP, so it wasn't handed over")


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

    def read_and_ack(self, now=NOW):
        c = ctx(self.tmp, self.values, now=now)
        obs = observed.prs({}, c)
        observed.acknowledge([obs], {}, c)
        return obs

    def test_lists_open_prs_with_head_and_latest_review_per_reviewer(self):
        calls = []
        with mock.patch.object(observed, "github_get", self.fake(calls)):
            obs = observed.prs({}, ctx(self.tmp, self.values, now=NOW))
        self.assertEqual(obs.status, "ok")
        self.assertEqual(len(obs.lines), 1)
        line = obs.lines[0]
        self.assertIn("#20", line); self.assertIn("bec95e7", line); self.assertIn("arden-bot APPROVED", line)
        self.assertNotIn("COMMENTED", line, "only each reviewer's latest review")
        self.assertTrue(all(token == "tok123" for _, token in calls), "only the Token: line is used")

    def test_the_cache_is_written_by_the_ack_not_the_read(self):
        calls = []
        with mock.patch.object(observed, "github_get", self.fake(calls)):
            observed.prs({}, ctx(self.tmp, self.values, now=NOW))
            self.assertFalse((Path(self.tmp) / "observed-prs.json").exists(), "a read writes nothing")
            first = self.read_and_ack()
            n = len(calls)
            again = observed.prs({}, ctx(self.tmp, self.values, now=NOW + timedelta(seconds=30)))
        self.assertEqual(first.lines, again.lines)
        self.assertEqual(len(calls), n, "a second prompt inside the TTL doesn't call GitHub")
        self.assertEqual(again.freshness, NOW, "a cached answer says when it was true")

    def test_the_cache_holds_no_token_and_is_owner_only(self):
        with mock.patch.object(observed, "github_get", self.fake([])):
            self.read_and_ack()
        cache = Path(self.tmp) / "observed-prs.json"
        self.assertNotIn("tok123", cache.read_text())
        self.assertEqual(cache.stat().st_mode & 0o077, 0)

    def test_a_cache_others_can_write_is_not_trusted(self):
        with mock.patch.object(observed, "github_get", self.fake([])):
            self.read_and_ack()
        os.chmod(Path(self.tmp) / "observed-prs.json", 0o666)
        calls = []
        with mock.patch.object(observed, "github_get", self.fake(calls)):
            observed.prs({}, ctx(self.tmp, self.values, now=NOW + timedelta(seconds=30)))
        self.assertTrue(calls, "a group/world-writable cache is ignored and GitHub asked")

    def test_offline_shows_the_stale_cache_with_its_age(self):
        with mock.patch.object(observed, "github_get", self.fake([])):
            self.read_and_ack()
        with mock.patch.object(observed, "github_get", side_effect=OSError("https://x?access_token=tok123")):
            obs = observed.prs({}, ctx(self.tmp, self.values, now=NOW + timedelta(minutes=10)))
        self.assertEqual((obs.status, obs.freshness, obs.ack), ("ok", NOW, None))
        text = "\n".join(l for _, said in observed.render([obs]) for l in said)
        self.assertIn("as of 10m ago (GitHub: OSError; cached)", text); self.assertIn("#20", text)
        self.assertNotIn("tok123", text, "an exception's text never reaches the block")

    def test_offline_with_no_cache_is_unknown(self):
        with mock.patch.object(observed, "github_get", side_effect=OSError("offline")):
            obs = observed.prs({}, ctx(self.tmp, self.values))
        self.assertEqual((obs.status, obs.reason), ("unknown", "GitHub: OSError, nothing cached"))

    def test_no_token_is_unknown_and_says_no_secret(self):
        self.secrets.write_text("Password: hunter2\n")
        with mock.patch.object(observed, "github_get", side_effect=AssertionError("called without a token")):
            obs = observed.prs({}, ctx(self.tmp, self.values))
        self.assertEqual(obs.status, "unknown")
        self.assertNotIn("hunter2", repr(obs))

    def test_more_than_shown_is_truncated(self):
        pulls = [dict(PULLS[0], number=n) for n in range(1, 9)]
        with mock.patch.object(observed, "github_get", lambda url, t, to: [] if "/reviews" in url else pulls):
            obs = observed.prs({}, ctx(self.tmp, self.values))
        self.assertEqual((len(obs.lines), obs.truncated), (observed.PRS_SHOWN, 3))


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
            return observed.deployed({}, ctx(self.tmp, self.values, run=hook.run_quiet, now=NOW))

    def test_installed_hooks_that_differ_from_main_are_named(self):
        out = "\n".join(self.run_it().lines)
        self.assertIn("hooks", out); self.assertIn("hook.py", out); self.assertIn("differ from main", out)

    def test_matching_hooks_say_so(self):
        (self.installed / "hook.py").write_text("# hook.py v2\n")
        self.assertIn("hooks: match main", "\n".join(self.run_it().lines))

    def test_a_hook_file_main_has_but_not_installed_is_missing(self):
        (self.installed / "hook.py").write_text("# hook.py v2\n")
        (self.installed / "run_line.py").unlink()
        out = "\n".join(self.run_it().lines)
        self.assertIn("missing: run_line.py", out); self.assertNotIn("differ", out)

    def test_store_marker_behind_main(self):
        self.marker.write_text(self.first + "\n")
        obs = self.run_it()
        self.assertEqual(obs.status, "ok")
        self.assertIn(self.first[:7], "\n".join(obs.lines)); self.assertIn("1 commit behind main", "\n".join(obs.lines))

    def test_no_marker_is_unknown_with_what_was_read(self):
        obs = self.run_it()
        self.assertEqual(obs.status, "unknown"); self.assertIn("which commit is deployed", obs.reason)
        self.assertIn("hooks:", obs.lines[0], "what could be read still shows")

    def test_no_main_is_unknown(self):
        obs = observed.deployed({}, ctx(self.tmp, {"PERSISTENCE_OBSERVE_REPO": str(self.tmp)}, run=hook.run_quiet))
        self.assertEqual(obs.status, "unknown")


class Switches(unittest.TestCase):
    def test_strip_is_named_off_when_unset(self):
        out = "\n".join(observed.switches({}, ctx(tempfile.mkdtemp(), {"PERSISTENCE_RECALL_COUNT": "3"})).lines)
        self.assertIn("STRIP_ROOM_PREAMBLE off", out); self.assertIn("RECALL_COUNT=3", out)

    def test_strip_on_and_the_store_unit_read(self):
        def run(cmd, cwd=None, timeout=10):
            self.assertEqual(cmd[:2], ["systemctl", "show"])
            return "Environment=PERSISTENCE_STORE_DB=/x PERSISTENCE_EMBEDDING_QUERYTAILCHARS=1200 PERSISTENCE_EMBEDDING_QUERYCHUNKCHARS=0 GH_TOKEN=sekrit\n"
        values = {"PERSISTENCE_RECALL_STRIP_ROOM_PREAMBLE": "1", "PERSISTENCE_OBSERVE_STORE_UNIT": "persistence-store"}
        out = "\n".join(observed.switches({}, ctx(tempfile.mkdtemp(), values, run=run)).lines)
        self.assertIn("STRIP_ROOM_PREAMBLE on", out)
        self.assertIn("EMBEDDING_QUERYTAILCHARS=1200", out)
        self.assertNotIn("STORE_DB", out, "only recall and query switches")
        self.assertNotIn("sekrit", out)

    def test_an_unreadable_unit_is_unknown(self):
        obs = observed.switches({}, ctx(tempfile.mkdtemp(), {"PERSISTENCE_OBSERVE_STORE_UNIT": "x"}))
        self.assertEqual(obs.status, "unknown"); self.assertIn("STRIP_ROOM_PREAMBLE off", obs.lines[0], "the hook side still shows")


class Claims(unittest.TestCase):
    def setUp(self):
        self.state = Path(tempfile.mkdtemp())
        (self.state / "tasks").mkdir()
        self.now = NOW

    def claim(self, n, status, minutes_ago, owner="other"):
        when = (self.now - timedelta(minutes=minutes_ago)).isoformat(timespec="seconds")
        cid = f"{n:08d}-0000-4000-8000-000000000000"
        (self.state / "tasks" / f"{cid}.json").write_text(json.dumps(
            {"id": cid, "task": f"task number {n}", "status": status, "updated": when, "owner": owner, "outcome": "ok"}))

    def look(self, minutes=0, ack=True):
        c = ctx(self.state, now=self.now + timedelta(minutes=minutes))
        obs = observed.claims_done({"session_id": "me"}, c)
        if ack:
            observed.acknowledge([obs], {"session_id": "me"}, c)
        return obs

    def test_finished_since_last_ack_not_own_capped(self):
        self.claim(1, "done", 30); self.claim(2, "abandoned", 20); self.claim(3, "claimed", 10)
        self.claim(4, "done", 5, owner="me"); self.claim(5, "done", 60 * 8)
        for n in range(6, 11):
            self.claim(n, "done", n)
        obs = self.look()
        text = "\n".join(obs.lines)
        self.assertNotIn("task number 3", text, "still running")
        self.assertNotIn("task number 4", text, "this session's own")
        self.assertNotIn("task number 5", text, "older than the first look's window")
        self.assertIn("task number 6", text)
        self.assertEqual((len(obs.lines), obs.truncated), (observed.CLAIMS_SHOWN, 7 - observed.CLAIMS_SHOWN))
        self.assertEqual(obs.event_range, ((self.now - timedelta(minutes=30)).isoformat(), (self.now - timedelta(minutes=6)).isoformat()))
        # The ack moved the cursor: nothing new on the next look.
        self.assertEqual(self.look(1).lines, ())
        self.claim(11, "done", -1.5)  # after the cursor (now+1m), before the look (now+2m)
        self.assertIn("task number 11", "\n".join(self.look(2).lines))

    def test_without_an_ack_the_next_wake_sees_the_same(self):
        self.claim(1, "done", 30)
        first = self.look(ack=False)
        self.assertEqual(first.lines, self.look(1, ack=False).lines, "a wake that failed consumed nothing")

    def test_a_claim_finishing_between_read_and_ack_is_not_lost(self):
        c = ctx(self.state, now=self.now)
        obs = observed.claims_done({"session_id": "me"}, c)
        self.claim(1, "done", -0.5)  # finishes after the read's now, before the ack
        observed.acknowledge([obs], {"session_id": "me"}, c)
        self.assertIn("task number 1", "\n".join(self.look(1).lines))


class Pure(unittest.TestCase):
    """Reading a source twice gives the same Observation and leaves the state dir exactly as it was (Ember's seam)."""

    def test_every_source_read_twice_is_identical_and_writes_nothing(self):
        state = Path(tempfile.mkdtemp())
        (state / "tasks").mkdir()
        (state / "tasks" / "a.json").write_text(json.dumps({"id": "a" * 8, "task": "t", "status": "done", "owner": "o",
                                                             "updated": (NOW - timedelta(minutes=5)).isoformat()}))
        secrets = state / "accounts"; secrets.write_text("Token: tok\n")
        values = {"PERSISTENCE_OBSERVE_GITHUB_TOKEN_FILE": str(secrets), "PERSISTENCE_OBSERVE_REPO": str(state),
                  "PERSISTENCE_OBSERVE_STORE_MARKER": str(state / "none")}
        get = lambda url, t, to: REVIEWS if "/reviews" in url else PULLS
        self.assertEqual(set(observed.SOURCES), {"prs", "deployed", "switches", "claims"}, "a new source joins this test")
        with mock.patch.object(observed, "github_get", get):
            for name, source in observed.SOURCES.items():
                before = snapshot(state)
                c = ctx(state, values, run=hook.run_quiet, now=NOW)
                first, second = source.read({"session_id": "me"}, c), source.read({"session_id": "me"}, c)
                self.assertEqual(first, second, name)
                self.assertEqual(snapshot(state), before, f"{name}'s read wrote to the state dir")


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
