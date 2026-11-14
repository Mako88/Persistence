"""No test can start a real `claude` (Arden 1570199499278909491, mutation R): imported first by every test module in
the claude-code, room and Codex suites, before anything that reads HOME at import.

A test that lifted its fakes let the code under test take a path nobody expected, and a real working session started
with the tester's PATH and HOME, read their inbox, store and room, and was kept as a wake they set. So the guard holds
even when the code under test is wrong, in three layers:
- HOME is a fresh empty directory, and the variables naming the tester's live store, state, room and session are
  unset, so a login shell reads no profile that would put the real claude back on PATH, and
  anything a module derives from HOME at import (hook.STATE) is a throwaway.
- A fake `claude` is first on PATH. It records its arguments and exits 97; it starts nothing.
- Popen refuses, before starting it, any argv whose program resolves to the real claude binary, and any launch that
  names `claude` anywhere in its argv (a `bash -lc "... claude -p ..."` string, cwd-exec's `python3 -I cwd_exec.py fd
  bash -lc ...`) outside `expected()`. The refusal is recorded as well as raised, so a test fails even when the code
  under test swallows the exception, and even when the launch would have died before reaching any binary (the
  de220054 incident, 2026-11-13: run_due's launch shell failed on a deleted state dir, so the fake was never reached
  and the test passed green on the real launch path).
After each test the processes it started are waited on (briefly), and a test during which the fake was reached or a
launch was refused fails, named; at exit any later call still fails the run. A test that means to reach the fake says
so with `expected()`."""
import atexit
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from contextlib import contextmanager
from pathlib import Path

REAL = shutil.which("claude")
REAL = os.path.realpath(REAL) if REAL else None
ROOT = Path(os.path.realpath(tempfile.mkdtemp(prefix="no-real-claude-")))
BIN, HOME, CALLS, SEEN = ROOT / "bin", ROOT / "home", ROOT / "calls", ROOT / "expected"
for d in (BIN, HOME, CALLS, SEEN):
    d.mkdir()
FAKE = BIN / "claude"
FAKE.write_text("#!/bin/sh\n# no_real_claude's fake: records, starts nothing.\n"
                f"printf '%s\\n' \"$PWD\" \"$*\" > '{CALLS}'/\"$$\"\nexit 97\n", encoding="utf-8")
FAKE.chmod(0o755)
os.environ["PATH"] = str(BIN) + os.pathsep + os.environ.get("PATH", "")
os.environ["HOME"] = str(HOME)
# What names the tester's live self (their store, state, room, session): a test reaches none of it unless it says so.
LIVE = ("PERSISTENCE_STORE_SOCKET", "PERSISTENCE_STATE_DIR", "PERSISTENCE_ROOM_SOCKET", "PERSISTENCE_ROOM_CHANNEL",
        "PERSISTENCE_SESSION", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN")
for name in LIVE:
    os.environ.pop(name, None)
if os.name == "nt":
    os.environ["USERPROFILE"] = str(HOME)

_children = []
_refused = []  # launches of claude refused outside expected(): each fails the test it happened in
_expecting = 0
_init = subprocess.Popen.__init__
# `claude` as a word of its own or the last part of a path, in any argv element or shell string: not `.claude/` or
# `claude-code/`.
_NAMES_CLAUDE = re.compile(r"""(?:^|[\s;&|()<>"'`/=])claude(?=$|[\s;&|()<>"'`])""")


def names_claude(argv):
    """Whether a launch's argv names `claude` anywhere: the program, an argument, or a word in a shell string."""
    for a in argv:
        text = os.fsdecode(a) if isinstance(a, (str, bytes, os.PathLike)) else str(a)
        if _NAMES_CLAUDE.search(text) or (REAL and text and os.path.realpath(text) == REAL):
            return True
    return False


def _guarded_init(self, args, *a, **k):
    argv = [args] if isinstance(args, (str, bytes, os.PathLike)) else list(args)
    program = os.fsdecode(argv[0]) if argv else ""
    env = k.get("env")
    found = shutil.which(program, path=(env or os.environ).get("PATH")) if program else None
    if REAL and found and os.path.realpath(found) == REAL:
        if not _expecting:
            _refused.append(repr(argv))
        raise AssertionError(f"a test tried to start the real claude ({REAL}): {argv!r}")
    if not _expecting and names_claude(argv):
        _refused.append(repr(argv))
        raise AssertionError(f"a test launched claude outside no_real_claude.expected(): {argv!r}")
    _init(self, args, *a, **k)
    _children.append(self)


subprocess.Popen.__init__ = _guarded_init


def settle(timeout=5.0):
    """Wait for what tests started (a detached `bash -lc ... claude` reaches the fake a moment after Popen returns)."""
    deadline = time.monotonic() + timeout
    while _children:
        p = _children.pop()
        try:
            p.wait(max(0.0, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            pass  # a long-lived server a test started: not ours to wait out


def calls():
    return sorted(CALLS.iterdir())


@contextmanager
def expected():
    """A test that reaches the fake on purpose (the guard's own test); yields the calls made inside it."""
    global _expecting
    made = []
    _expecting += 1
    try:
        yield made
    finally:
        _expecting -= 1
        settle()
        for c in calls():
            made.append(c.read_text(encoding="utf-8"))
            c.rename(SEEN / c.name)


def _report(found):
    return "; ".join(c.read_text(encoding="utf-8").replace("\n", " in ", 1).strip() for c in found)


_run = unittest.TestCase.run


def _guarded_run(self, result=None):
    out = _run(self, result)
    settle()
    found = calls()
    refused = _refused[:]
    del _refused[:]
    if found or refused:
        report = "; ".join(filter(None, [_report(found), *(f"refused {r}" for r in refused)]))
        for c in found:
            c.rename(SEEN / c.name)
        try:
            raise AssertionError(f"this test reached `claude` (the guard's fake; a real one would have started), or tried "
                                 f"to launch it outside expected(): {report}")
        except AssertionError:
            if result is None:
                raise
            result.addFailure(self, sys.exc_info())
    return out


unittest.TestCase.run = _guarded_run


@atexit.register
def _at_exit():
    settle(2.0)
    found = calls()
    if found or _refused:
        sys.stderr.write(f"no_real_claude: `claude` was reached or launched outside any test: "
                         f"{'; '.join(filter(None, [_report(found), *_refused]))}\n")
        sys.stderr.flush()
        os._exit(1)
    shutil.rmtree(ROOT, ignore_errors=True)


def check_suite(test, directory):
    """The suite's own check that fails the build: every test module imports this guard before the code under test,
    the fake is what `claude` resolves to, and HOME is the guard's."""
    for f in sorted(Path(directory).glob("test_*.py")):
        text = f.read_text(encoding="utf-8")
        guard = text.find("import no_real_claude")
        test.assertGreater(guard, 0, f"{f.name} doesn't import no_real_claude")
        first = text.find("sys.path.insert(0, str(Path(__file__).resolve().parent.parent))")
        test.assertTrue(first == -1 or guard < first, f"{f.name} imports the code under test before no_real_claude")
    test.assertEqual(shutil.which("claude"), str(FAKE))
    test.assertEqual(str(Path.home()), str(HOME))
