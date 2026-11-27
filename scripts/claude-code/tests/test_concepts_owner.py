"""Whose store the extractor reads (Arden, room msg 1575884298655899741): it read PERSISTENCE_PEER_NAME, which nothing
sets, and fell back to "Claude", so its first run on Arden's store filed 3 of 4 of Arden's own actions under Claude.
The owner is PERSISTENCE_PEERNAME (the name every store unit and the hook already use) or the store's own `whoami`; the
misspelling is read only as a warned fallback, so Arden's workaround keeps working; with no name, or two that disagree,
it refuses and moves no floor. And no script may read a peer-name variable other than the canonical one, or default it
to a literal name: a silent self-name default is the mistake class."""
import io
import json
import os
import re
import sys
import tempfile
import unittest
from contextlib import redirect_stderr
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import concepts  # noqa: E402

SCRIPTS = Path(__file__).resolve().parents[2]
FRAGMENT = {"id": 5, "type": "Personal", "content": "I rejected #1 and re-extracted.", "created": "2026-11-27T14:41:00Z"}


class Store:
    def __init__(self, whoami=""):
        self.whoami = whoami
        self.calls = []

    def __call__(self, *args, stdin=None, **kw):
        self.calls.append(args)
        if args[0] == "whoami":
            return self.whoami
        if args[0] == "list":
            return json.dumps([FRAGMENT] if "--archived" not in args and int(args[args.index("--after") + 1]) < 5 else [])
        if args[:2] in (("concept", "list"), ("concept", "touched")):
            return "[]"
        return ""


def env(**names):
    clean = {k: v for k, v in os.environ.items() if k not in ("PERSISTENCE_PEERNAME", "PERSISTENCE_PEER_NAME")}
    return mock.patch.dict(os.environ, {**clean, **names}, clear=True)


class Owner(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.prompts = []

    def extractor(self, store):
        return concepts.Extractor(self.tmp.name, store, call=lambda p: (self.prompts.append(p), "{}")[1])

    def test_the_canonical_variable_names_the_owner_in_the_prompt(self):
        with env(PERSISTENCE_PEERNAME="Arden"):
            x = self.extractor(Store())
            x.set_floor(0)
            x.catch_up(1)
        self.assertEqual("Arden", x.owner)
        self.assertIn('The self who owns the store is "Arden"', self.prompts[0])
        self.assertNotIn('"Claude"', self.prompts[0])

    def test_with_no_variable_the_store_says_whose_it_is(self):
        with env():
            self.assertEqual("Arden", self.extractor(Store("Arden /home/arden/store/arden.db\n")).owner)

    def test_the_old_spelling_still_works_and_says_so(self):
        err = io.StringIO()
        with env(PERSISTENCE_PEER_NAME="Arden"), redirect_stderr(err):
            self.assertEqual("Arden", self.extractor(Store("Arden /home/arden/store/arden.db")).owner)
        self.assertIn("PERSISTENCE_PEER_NAME", err.getvalue())
        self.assertIn("PERSISTENCE_PEERNAME", err.getvalue())

    def test_no_name_anywhere_refuses_and_moves_no_floor(self):
        with env():
            x = self.extractor(Store(""))
            x.set_floor(0)
            with self.assertRaises(concepts.NoOwner):
                x.owner
            out = x.catch_up(1)
        self.assertEqual(0, x.floor())
        self.assertEqual([], self.prompts)
        self.assertIn("PERSISTENCE_PEERNAME", " ".join(out))

    def test_a_variable_the_store_contradicts_refuses(self):
        with env(PERSISTENCE_PEERNAME="Claude"):
            x = self.extractor(Store("Arden /home/arden/store/arden.db"))
            x.set_floor(0)
            out = x.catch_up(1)
        self.assertEqual(0, x.floor())
        self.assertEqual([], self.prompts)
        self.assertIn("Arden", " ".join(out))

    def test_a_given_owner_is_used_without_asking_the_store(self):
        store = Store("")
        with env():
            self.assertEqual("Tessel", concepts.Extractor(self.tmp.name, store, call=lambda p: "{}", owner="Tessel").owner)
        self.assertNotIn(("whoami",), store.calls)


# A read of a peer-name variable: PERSISTENCE_PEERNAME, PERSISTENCE_PEER_NAME, or any other spelling of it.
READ = re.compile(r"""(?:environ\.get|getenv|environ\[)\s*\(?\s*["'](PERSISTENCE_PEER_?NAMES?|PERSISTENCE_\w*PEER\w*NAME\w*)["']""")
# ... and what a missing one becomes: a second argument, or `or "<literal>"` right after the read.
DEFAULT = re.compile(r"""(?:,\s*["']([^"']*)["']\s*\)|\)\s*or\s*["']([^"']*)["'])""")
NOT_A_NAME = {"me", ""}   # 'me (the heartbeat)': a pronoun, not a guess at who the self is


class NoSilentSelfName(unittest.TestCase):
    """Fails the build on the mistake class, not just this instance."""

    def reads(self):
        for path in sorted(SCRIPTS.rglob("*.py")):
            if "tests" in path.parts or "__pycache__" in path.parts:
                continue
            for n, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
                for m in READ.finditer(line):
                    yield path.relative_to(SCRIPTS), n, line, m

    def test_only_the_canonical_variable_is_read(self):
        stray = [f"{p}:{n}: {m.group(1)}" for p, n, line, m in self.reads()
                 if m.group(1) != "PERSISTENCE_PEERNAME" and not (m.group(1) == "PERSISTENCE_PEER_NAME" and "LEGACY_OWNER_VAR" in line)]
        self.assertEqual([], stray)

    def test_no_read_defaults_to_a_literal_name(self):
        guessed = []
        for p, n, line, m in self.reads():
            d = DEFAULT.match(line, m.end())
            if d and (d.group(1) or d.group(2) or "") not in NOT_A_NAME:
                guessed.append(f"{p}:{n}: defaults to {d.group(1) or d.group(2)!r}")
        self.assertEqual([], guessed)

    def test_the_check_sees_the_old_bug(self):
        line = 'self.owner = owner or os.environ.get("PERSISTENCE_PEER_NAME") or "Claude"'
        m = READ.search(line)
        self.assertEqual("PERSISTENCE_PEER_NAME", m.group(1))
        self.assertEqual("Claude", DEFAULT.match(line, m.end()).group(2))


if __name__ == "__main__":
    unittest.main()
