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

    def patches(self, tmp, inbox=""):
        return [mock.patch.object(hook, "note_line", return_value=None), mock.patch.object(hook, "room_freshness", return_value=None),
                mock.patch.object(hook, "linux_processes", return_value="[]"), mock.patch.object(hook, "own_services", return_value=[]),
                mock.patch.object(hook, "run_quiet", return_value=""), mock.patch.object(hook, "project", return_value="work"),
                mock.patch.object(hook, "inbox", return_value=inbox), mock.patch.object(hook, "STATE", Path(tmp))]

    def run_hook(self, observations, ack, out=None, inbox=""):
        events = []
        with tempfile.TemporaryDirectory() as tmp:
            ps = self.patches(tmp, inbox)
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

    def subagent(self, printed, core="the core"):
        events = []
        with tempfile.TemporaryDirectory() as tmp:
            ps = self.patches(tmp) + [mock.patch.object(hook, "core", return_value=core)]
            for p in ps:
                p.start()
            try:
                ack = lambda obs, e, c: events.append((obs.source, printed.getvalue()))
                with mock.patch.object(observed, "observe", return_value=[OBS("prs", NOW, lines=("SOURCELINE",), ack="x")]), \
                        mock.patch.dict(observed.SOURCES, {"prs": src(None, ack)}, clear=True), mock.patch("sys.stdout", printed):
                    hook.subagent_start({"session_id": "s-1", "cwd": tmp})
            finally:
                for p in ps:
                    p.stop()
        return events

    def test_subagent_start_acks_after_it_prints(self):
        """Arden on 5d07630: both mutants of the SubagentStart path (ack before the print, no ack) stayed green."""
        printed = __import__("io").StringIO()
        events = self.subagent(printed)
        self.assertEqual([source for source, _ in events], ["prs"], "acked once, for what it printed")
        context = json.loads(events[0][1])["hookSpecificOutput"]
        self.assertEqual(context["hookEventName"], "SubagentStart")
        self.assertIn("- SOURCELINE", context["additionalContext"], "the ack ran after the block was written")

    def test_subagent_start_that_prints_nothing_acks_nothing(self):
        self.assertEqual(self.subagent(__import__("io").StringIO(), core="  "), [])

    def test_an_observation_cut_by_cap_is_not_acked(self):
        long = tuple("z" * 190 + str(i) for i in range(observed.MAX_LINES))
        many = [OBS(f"s{i}", NOW, lines=long, ack="x") for i in range(12)]  # ~13.8k chars, over CAP
        text, events = self.run_hook(many, lambda events: lambda obs, e, c: events.append(obs.source))
        self.assertTrue(0 < len(events) < 12, events)
        self.assertNotIn("s11", events, "its lines were cut by CAP, so it wasn't handed over")

    def test_duplicate_text_elsewhere_cannot_make_a_cut_observation_count_as_delivered(self):
        """Ember on 5d07630: delivery was decided by `line in sent`, so a block cut by CAP whose lines also occur earlier
        in Observed now counted as handed over. Delivery is now which complete rendered blocks survived the cap."""
        shared = tuple("d" * 190 + str(i) for i in range(observed.MAX_LINES - 1))
        many = [OBS(f"s{i}", NOW, lines=(f"HEAD-{i:02d}",) + shared, ack="x") for i in range(12)]
        text, events = self.run_hook(many, lambda events: lambda obs, e, c: events.append(obs.source))
        sent = json.loads(text)["hookSpecificOutput"]["additionalContext"]
        blocks = {o.source: "\n".join(said) for o, said in observed.render(many, NOW)}
        whole = [s for s, block in blocks.items() if block in sent]
        cut = [s for s, block in blocks.items() if f"HEAD-{s[1:]:0>2}" in sent and block not in sent]
        self.assertEqual(len(cut), 1, "the cap falls inside one block, whose lines all appear earlier")
        self.assertEqual(events, whole)

    def test_what_comes_before_the_observed_block_moves_where_the_cap_falls(self):
        many = [OBS(f"s{i}", NOW, lines=(f"HEAD-{i:02d}",) + tuple("e" * 190 + str(j) for j in range(5)), ack="x") for i in range(12)]
        text, events = self.run_hook(many, lambda events: lambda obs, e, c: events.append(obs.source), inbox="I" * 3000 + "\n\n")
        sent = json.loads(text)["hookSpecificOutput"]["additionalContext"]
        blocks = {o.source: "\n".join(said) for o, said in observed.render(many, NOW)}
        self.assertEqual(events, [s for s, block in blocks.items() if block in sent])
        self.assertLess(len(events), 8, "the inbox took room the blocks would otherwise have had")

    def test_each_placement_is_exactly_its_rendered_block(self):
        many = [OBS("a", NOW, lines=("one", "two")), OBS("b", NOW), OBS("c", NOW, status="unknown", reason="r", lines=("three",))]
        with tempfile.TemporaryDirectory() as tmp:
            ps = self.patches(tmp)
            for p in ps:
                p.start()
            try:
                with mock.patch.object(observed, "observe", return_value=many):
                    pending = []
                    text = hook.observe({"session_id": "s-1", "cwd": tmp}, pending)
            finally:
                for p in ps:
                    p.stop()
        self.assertEqual([(r.obs.source, text[r.start:r.end]) for r in pending],
                         [(o.source, "\n".join(said)) for o, said in observed.render(many, pending[0].ctx.now)])


class ServeAck(unittest.TestCase):
    """Ember on 5d07630: under `serve` the ack ran inside answer(), before do_POST wrote the reply; a disconnect in between
    consumed events the session never received. reply() acks only after the write it's given has returned."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.ps = Hook.patches(None, self.tmp)
        for p in self.ps:
            p.start()
        self.acked = []
        sources = {"prs": src(None, lambda obs, e, c: self.acked.append(obs.source))}
        for p in (mock.patch.object(observed, "observe", return_value=[OBS("prs", NOW, lines=("SOURCELINE",), ack="x")]),
                  mock.patch.dict(observed.SOURCES, sources, clear=True),
                  mock.patch.object(sys, "stdout", hook.PerRequest(sys.stdout))):
            p.start()
            self.ps.append(p)

    def tearDown(self):
        for p in reversed(self.ps):
            p.stop()

    def post(self, write):
        event = json.dumps({"session_id": "s-1", "cwd": self.tmp, "hook_event_name": "UserPromptSubmit"}).encode()
        return hook.reply("/observe", event, write)

    def test_a_failed_write_acks_nothing(self):
        def broken(body):
            self.assertEqual(self.acked, [], "nothing acked before the write")
            raise BrokenPipeError("client went away")

        with self.assertRaises(BrokenPipeError):
            self.post(broken)
        self.assertEqual(self.acked, [])

    def test_a_successful_write_acks_after_it(self):
        written = []

        def ok(body):
            self.assertEqual(self.acked, [], "nothing acked before the write")
            written.append(body)

        self.post(ok)
        self.assertIn(b"SOURCELINE", written[0])
        self.assertEqual(self.acked, ["prs"])

    def test_a_hook_that_fails_after_deferring_acks_nothing(self):
        real = hook.observe_hook

        def then_fail(event):
            real(event)
            raise RuntimeError("after the block was built")

        written = []
        with mock.patch.object(hook, "observe_hook", then_fail), mock.patch("sys.stderr", __import__("io").StringIO()):
            self.post(written.append)
        self.assertEqual(written, [b""], "the session got nothing")
        self.assertEqual(self.acked, [])

    def test_direct_mode_still_acks_after_its_flush(self):
        with mock.patch.object(sys, "stdout", __import__("io").StringIO()):
            hook.observe_hook({"session_id": "s-1", "cwd": self.tmp})
        self.assertEqual(self.acked, ["prs"])


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

    def test_a_claim_stamped_before_the_read_but_written_after_it_is_not_lost(self):
        """Arden's repro on 5d07630: claims.py stamps `updated` (second resolution) before the file is written. A read
        in that gap, acked with cursor = the read's now, skipped the claim for good."""
        self.now = datetime(2026, 11, 13, 4, 10, 5, 400000, tzinfo=timezone.utc)
        c = ctx(self.state, now=self.now)
        obs = observed.claims_done({"session_id": "me"}, c)  # 04:10:05.4, the file not written yet
        observed.acknowledge([obs], {"session_id": "me"}, c)
        self.claim(1, "done", 0.4 / 60)  # stamped 04:10:05, written now
        self.assertIn("task number 1", "\n".join(self.look(5).lines))

    def test_a_claim_shown_inside_the_settle_margin_is_not_shown_again(self):
        self.claim(1, "done", 0.1)
        self.assertIn("task number 1", "\n".join(self.look().lines))
        self.assertEqual(self.look(0.2).lines, (), "at-least-once, without repeats")
        self.claim(2, "done", -0.3)
        self.assertEqual(["task number 2" in line for line in self.look(0.5).lines], [True])
        self.assertEqual(self.look(5).lines, ())

    def cursor(self):
        return json.loads(observed.claims_cursor({"session_id": "me"}, ctx(self.state)).read_text(encoding="utf-8"))

    def test_a_claim_reopened_and_finished_again_inside_the_margin_is_shown_again(self):
        """Arden on 2b6e140: `seen` keyed by id alone hid the second finish of a claim reopened inside CLAIMS_SETTLE."""
        self.claim(1, "done", 0.1)
        self.assertIn("task number 1", "\n".join(self.look().lines))
        self.claim(1, "done", -0.2)  # reopened, finished again 12 s after the first look
        self.assertEqual(["task number 1" in line for line in self.look(0.3).lines], [True], "the second finish shows")
        self.assertEqual(self.look(0.4).lines, (), "and only once")

    def test_the_cursor_never_moves_behind_the_previous_one(self):
        """Arden's green mutant on 2b6e140 (`at` without max(since, ...)): a cursor 5d07630 wrote at its read's now is
        ahead of the next read's now - CLAIMS_SETTLE. Moving it back would show again what that read already showed."""
        written = self.now - timedelta(seconds=5)
        observed.write_atomic(observed.claims_cursor({"session_id": "me"}, ctx(self.state)), written.isoformat())
        self.claim(1, "done", 10 / 60)  # finished before that read, so it showed then
        self.assertEqual(self.look().lines, ())
        self.assertEqual(self.cursor()["at"], written.isoformat())
        self.assertEqual(self.look(0.1).lines, (), "not shown a second time")

    def test_seen_forgets_ids_once_the_cursor_passes_them(self):
        """Arden's green mutant on 2b6e140 (`seen` not pruned at `at`): it would grow with every claim ever shown."""
        self.claim(1, "done", 0.1)
        self.look()
        self.assertEqual(list(self.cursor()["seen"]), [f"{1:08d}-0000-4000-8000-000000000000"], "inside the margin")
        self.look(1)
        self.assertEqual(self.cursor()["seen"], {}, "the cursor is past it now")

    def test_a_cursor_written_before_the_margin_is_still_read(self):
        observed.write_atomic(observed.claims_cursor({"session_id": "me"}, ctx(self.state)), (self.now - timedelta(minutes=10)).isoformat())
        self.claim(1, "done", 20); self.claim(2, "done", 5)
        self.assertEqual(["task number 2" in line for line in self.look().lines], [True])


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
        wake = "11111111-1111-4111-8111-111111111111"
        (state / "heartbeat.json").write_text(json.dumps({  # a fault, and a queued R entry this session is the wake of
            "delivery_fault": {"rev": 2, "since": NOW.isoformat(), "counts": {"cap": 1, "hb_failed": 1, "undeliverable": 0}},
            "reports_q": {"unit:a.service:1:failed": {"state": "queued", "wake": wake}}}))
        (state / "fired.jsonl").write_text(json.dumps({"firing": wake, "fact": "enqueued", "at": NOW.isoformat(),
                                                       "session": "me", "hb": "reports"}) + "\n")
        self.assertEqual(set(observed.SOURCES), {"prs", "deployed", "switches", "claims", "heartbeat"},
                         "a new source joins this test")
        with mock.patch.object(observed, "github_get", get):
            for name, source in observed.SOURCES.items():
                before = snapshot(state)
                c = ctx(state, values, run=hook.run_quiet, now=NOW)
                first, second = source.read({"session_id": "me"}, c), source.read({"session_id": "me"}, c)
                self.assertEqual(first, second, name)
                self.assertEqual(snapshot(state), before, f"{name}'s read wrote to the state dir")
                if name == "heartbeat":
                    self.assertTrue(first.lines and first.ack, "the fixture should make the heartbeat source render")


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
