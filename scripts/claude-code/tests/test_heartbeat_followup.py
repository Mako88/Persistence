"""The follow-up to PR #28 (merged at e4161ca), Claude's three non-blocking points (room 1570872186808307785, review
5896294036):

(a) one stalled-layer predicate for run-due (hook.stalled_layer_on) and the beat (heartbeat.stalled_on), with a
    parity table that includes the string "false";
(b) the G3 lock: no interleaving needs it once the queue is read before the ledger, so it stays as a backstop
    (comment in Beat.stalled_layer) and the read order it backs up is pinned here;
(c) an ack intent file that doesn't parse is abandoned, not live, so its entry doesn't stay queued.

Fake clock, temporary state, a fake process table; nothing is launched."""
import json
import sys
import unittest
from datetime import timedelta
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude
sys.path.insert(0, str(Path(__file__).resolve().parent))
import test_heartbeat_revision as R  # noqa: E402
import test_heartbeat_stalled as S  # noqa: E402
from test_heartbeat_stalled import coordination, heartbeat, hook, observed  # noqa: E402

MISSING = object()


# ------------------------------------------------------------------- (a) one predicate, parity including "false"
class OnePredicate(S.Base):
    # (value in the settings file's env block, on?). JSON values are written as JSON: True is `true`, None is `null`.
    TABLE = [
        ("true", True), ("TRUE", True), (" True ", True), ("1", True), (" 1\n", True), (True, True), (1, True),
        ("false", False), ("False", False), ("FALSE", False), (" false ", False), ("0", False), ("", False),
        ("   ", False), ("no", False), ("off", False), ("yes", False), ("on", False), ("truee", False),
        ("1.0", False), ("2", False), (False, False), (0, False), (None, False), (1.0, False), ([], False),
        (["true"], False), ({}, False), ({"true": 1}, False), (MISSING, False),
    ]

    def write_stalled(self, value):
        env = {"PERSISTENCE_HEARTBEAT": "true", "PERSISTENCE_OBSERVE_SOURCES": "heartbeat"}
        if value is not MISSING:
            env["PERSISTENCE_HEARTBEAT_STALLED"] = value
        (self.home / ".claude" / "settings.json").write_text(json.dumps({"env": env}), encoding="utf-8")

    def test_run_due_and_the_beat_agree_on_every_value_and_false_is_off(self):
        for value, on in self.TABLE:
            with self.subTest(value=value):
                self.write_stalled(value)
                self.assertIs(heartbeat.stalled_on(self.home), on)
                self.assertIs(hook.stalled_layer_on(self.home), on)

    def test_they_agree_when_the_file_is_missing_broken_or_odd(self):
        path = self.home / ".claude" / "settings.json"
        cases = {"missing": None, "not json": "{", "a list": "[]", "env a list": '{"env": ["true"]}',
                 "env null": '{"env": null}', "env a string": '{"env": "PERSISTENCE_HEARTBEAT_STALLED=true"}'}
        for name, text in cases.items():
            with self.subTest(case=name):
                path.unlink(missing_ok=True)
                if text is not None:
                    path.write_text(text, encoding="utf-8")
                self.assertIs(heartbeat.stalled_on(self.home), False)
                self.assertIs(hook.stalled_layer_on(self.home), False)

    def test_the_environment_alone_turns_neither_on(self):
        self.write_stalled("false")
        with mock.patch.dict("os.environ", {"PERSISTENCE_HEARTBEAT_STALLED": "true"}):
            self.assertIs(heartbeat.stalled_on(self.home), False)
            self.assertIs(hook.stalled_layer_on(self.home), False)

    def test_run_due_asks_the_beats_predicate_and_keeps_no_copy(self):
        # Whatever heartbeat.stalled_on answers, run-due answers: a second copy of the rule can drift from the first.
        for answer in (True, False):
            with self.subTest(answer=answer), mock.patch.object(heartbeat, "stalled_on", return_value=answer) as asked:
                self.write_stalled("false" if answer else "true")  # the file says the opposite
                self.assertIs(hook.stalled_layer_on(self.home), answer)
                asked.assert_called_once_with(self.home)

    def test_the_master_switch_uses_the_same_rule(self):
        for value, on in self.TABLE:
            with self.subTest(value=value):
                env = {} if value is MISSING else {"PERSISTENCE_HEARTBEAT": value}
                (self.home / ".claude" / "settings.json").write_text(json.dumps({"env": env}), encoding="utf-8")
                self.assertIs(heartbeat.enabled(self.home), on)

    def test_run_due_hands_its_locked_step_the_shared_answer(self):
        # The consequence at switch-on: with the string "false", run-due's locked step is told the layer is off, so
        # its enqueued facts carry no stalled fields. The locked step is replaced: nothing is fired or launched.
        for value, on in (("false", False), ("true", True)):
            with self.subTest(value=value), mock.patch.object(hook, "_run_due_locked") as locked, \
                    mock.patch.object(hook, "RUN_LINE", Path(__file__)):  # present, so run-due gets to its step
                self.write_stalled(value)
                hook.run_due(grace=0, home=self.home)
                locked.assert_called_once()
                self.assertIs(locked.call_args.args[-1], on)


# ------------------------------------------------------------------ (b) the read order the G3 lock backs up
class ReadOrder(R.Base):
    def test_the_stalled_layer_reads_the_queue_before_the_ledger(self):
        # Without the G3 lock the snapshot holds only because the queue is read first (comment in Beat.stalled_layer):
        # an item run-due fires between the reads is then seen in both, never in neither. Pin that order, so the lock
        # stays a backstop and not the only guard.
        self.activity(S.at(6, 33))
        self.beat(S.at(8, 8))  # a quiet wake is queued: the layer has something to read
        events = []
        real_queue, real_snap = heartbeat.read_queue, coordination.snapshot

        def read_queue(state):
            events.append(("queue", tuple(coordination.held_locks())))
            return real_queue(state)

        def snapshot(state, *a, **kw):
            caller = sys._getframe(1).f_code
            if caller.co_filename == heartbeat.__file__:
                events.append(("ledger:" + caller.co_name, tuple(coordination.held_locks())))
            return real_snap(state, *a, **kw)

        with mock.patch.object(heartbeat, "read_queue", read_queue), mock.patch.object(coordination, "snapshot", snapshot):
            self.beat(self.now + timedelta(minutes=10))
        layer = [i for i, (e, _) in enumerate(events) if e == "ledger:stalled_layer"]
        self.assertEqual(len(layer), 1, events)
        i = layer[0]
        self.assertGreater(i, 0, events)
        self.assertEqual(events[i - 1][0], "queue", events)  # the queue read comes straight before the ledger read
        self.assertIn("wakes", events[i][1])  # and both are under the lock, the backstop
        self.assertIn("wakes", events[i - 1][1])


# --------------------------------------------------------------------- (c) an unparseable intent is abandoned
class UnparseableIntent(R.Base):
    BODIES = {
        "empty": b"",
        "truncated json": b'{"pid": 12',
        "garbage": b"\x00\xffnot json at all",
        "invalid utf-8": b"\xff\xfe\xfd",
        "null": b"null",
        "a list": b"[]",
        "a number": b"42",
        "a string": b'"live"',
        "no fields": b"{}",
        "time not a time": b'{"pid": 1, "start": 1, "at": "soon"}',
        "time a number": b'{"pid": 1, "start": 1, "at": 5}',
    }

    def fired_and_shown(self):
        return R.AckLockLost.fired_and_shown(self)

    def test_an_intent_that_does_not_parse_is_abandoned_and_its_entry_retryable(self):
        for name, body in self.BODIES.items():
            with self.subTest(intent=name):
                self.tearDown(); self.setUp()
                key, f, obs, ctx = self.fired_and_shown()
                intent = self.state / observed.ACK_INTENT / f
                intent.parent.mkdir(parents=True, exist_ok=True)
                intent.write_bytes(body)
                # Within the fresh window: a real, live intent would hold the entry here (AckLockLost's
                # test_a_live_fresh_intent_holds_the_entry...), so only "abandoned" releases it.
                self.now = self.now + timedelta(seconds=10)
                beat = heartbeat.Beat.__new__(heartbeat.Beat)
                beat.state, beat.now = self.state, self.now
                self.assertEqual(heartbeat.Beat.ack_intents(beat), {f: "abandoned"})
                self.beat()
                e = self.hb()["reports_q"][key]
                self.assertEqual((e["state"], e["failures"]), ("none", 0))  # retryable, no failed attempt
                self.assertFalse(intent.exists())  # consumed: it can't hold anything on a later beat either
                self.assertNotIn("delivery_fault", self.hb())


if __name__ == "__main__":
    unittest.main()
