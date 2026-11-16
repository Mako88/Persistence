"""Issue #67: a --propose sleep pass hands its record to the work inbox with a wake, so proposals reach a tracked list
instead of waiting in records only the next night reads (five nights unrun on Claude's store, five on Arden's).
Arden's three conditions (issue #67 comment 6912449945), each pinned here:
1. No stacking: a night whose earlier handoff is still live joins that claim; one live claim names every record.
2. Only the record id goes in the inbox; the commands are read from the store, never copied.
3. One work session at a time: when another work session is running, the handoff's wake waits.
Opt-in per self: with PERSISTENCE_SLEEP_HANDOFF_AT unset, nothing is handed on."""
import os
import re
import sys
from datetime import datetime
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)
sys.path.insert(0, str(Path(__file__).resolve().parent))
from test_spawn_claim import Base, sid  # noqa: E402  the claims/run-due fixture
import hook  # noqa: E402
import claims  # noqa: E402

WITH_PROPOSALS = """Sleep pass #{id}, 2026-11-16. Proposal-only: I changed nothing.

Proposals (run in a waking session):

1. A fact worth keeping.
pstore add --type Personal --content "SECRET-COMMAND-TEXT {id}"
Reason: it is only in a note.
"""
NONE = "Sleep pass #{id}, 2026-11-17. Proposal-only.\n\nChanges: none.\n\nProposals: none\n"


class Store:
    """The store as sleep_handoff asks it: the newest sleep/pass record, and a record's text."""

    def __init__(self):
        self.records = {}

    def add(self, rid, template):
        self.records[str(rid)] = template.format(id=rid)

    def newest(self):
        return max(self.records, key=int) if self.records else None

    def __call__(self, *args, **kwargs):
        if args[:1] == ("list",):
            n = self.newest()
            return (0, f"[#{n} | Summary | i:0.6 c:1 | tags: sleep/pass | 2026-11-16]\n{self.records[n]}\n" if n else "", "")
        if args[:1] == ("show",):
            rid = str(args[1]).lstrip("#")
            return (0, f"[#{rid} | Summary]\n{self.records[rid]}", "") if rid in self.records else (2, "", "no such fragment")
        raise AssertionError(f"unexpected store call {args}")


class SleepHandoff(Base):
    def setUp(self):
        super().setUp()
        self.store = Store()
        self.sent = []
        self.night = sid()
        for p in [mock.patch.object(hook, "call_store", self.store),
                  mock.patch.object(hook, "send_command", side_effect=lambda a: self.sent.append(list(a)) or 0),
                  mock.patch.object(hook.Path, "home", return_value=self.home),
                  mock.patch.dict(os.environ, {"PERSISTENCE_SLEEP_HANDOFF_AT": "07:30"})]:
            p.start()
            self.addCleanup(p.stop)

    def pass_ends(self, rid=None, template=WITH_PROPOSALS):
        """One night: the newest record before the pass, then the pass writes `rid` (None: it writes nothing)."""
        before = self.store.newest()
        if rid is not None:
            self.store.add(rid, template)
        return hook.sleep_handoff(before, self.night, home=self.home)

    def live(self):
        return [r for r in self.c.all() if r["subject"] == hook.SLEEP_SUBJECT and r["status"] not in claims.TERMINAL]

    def test_a_record_with_proposals_makes_one_inbox_item_one_claim_and_one_wake(self):
        self.pass_ends(4568)
        self.assertEqual(len(self.sent), 1)
        [claim] = self.live()
        self.assertEqual((claim["status"], claim["dispatch"], claim["origin_session"]), ("open", "handoff", self.night))
        self.assertIn("#4568", claim["task"])
        [item] = self.queue()
        self.assertEqual((item["claim"], item["work"], item["alone"]), (claim["id"], True, True))
        self.assertEqual(datetime.fromtimestamp(item["at"]).strftime("%H:%M"), "07:30")

    def test_condition_2_only_the_record_id_goes_in_the_inbox(self):
        self.pass_ends(4568)
        [[topic, *words]] = self.sent
        text = " ".join(words)
        self.assertEqual(topic, "work")
        self.assertEqual(re.findall(r"\d+", text), ["4568"])
        self.assertNotIn("SECRET-COMMAND-TEXT", text)
        self.assertNotIn("SECRET-COMMAND-TEXT", self.live()[0]["task"])

    def test_a_record_saying_proposals_none_makes_nothing(self):
        self.pass_ends(4569, NONE)
        self.assertEqual((self.sent, self.live(), self.queue()), ([], [], []))

    def test_no_new_record_makes_nothing(self):
        self.store.add(4568, WITH_PROPOSALS)
        self.pass_ends(None)
        self.assertEqual((self.sent, self.live(), self.queue()), ([], [], []))

    def test_a_second_run_on_the_same_record_makes_no_duplicate(self):
        self.pass_ends(4568)
        hook.sleep_handoff(None, self.night, home=self.home)  # the same record is still the newest
        self.assertEqual(len(self.sent), 1)
        self.assertEqual(len(self.live()), 1)
        self.assertEqual(len(self.queue()), 1)

    def test_condition_1_no_stacking_a_second_night_joins_the_live_claim(self):
        self.pass_ends(4168)
        self.pass_ends(4568)  # the first night's claim isn't done
        [claim] = self.live()
        self.assertIn("#4168", claim["task"])
        self.assertIn("#4568", claim["task"])
        self.assertEqual(len(self.queue()), 1, "one wake for the one claim")
        self.assertEqual(len(self.sent), 2, "each night's id reaches the inbox, which a running claim's session also reads")
        self.assertEqual(claim["history"][-1]["action"], "amend")

    def test_condition_1_joins_a_claim_already_taken_and_paused(self):
        self.pass_ends(4168)
        self.due_now()
        self.run_due()
        [claim] = self.live()
        self.c.pause(claim["id"], by=claim["owner"], why="waiting on Arden")
        self.pass_ends(4568)
        [joined] = self.live()
        self.assertEqual(joined["id"], claim["id"])
        self.assertIn("#4568", joined["task"])

    def test_after_the_claim_is_done_the_next_night_gets_its_own(self):
        self.pass_ends(4168)
        self.due_now()
        self.run_due()
        [claim] = self.live()
        self.c.done(claim["id"], by=claim["owner"])
        self.pass_ends(4568)
        [new] = self.live()
        self.assertNotEqual(new["id"], claim["id"])
        self.assertNotIn("#4168", new["task"])

    def test_with_the_setting_unset_nothing_is_handed_on(self):
        with mock.patch.dict(os.environ, {"PERSISTENCE_SLEEP_HANDOFF_AT": ""}):
            self.pass_ends(4568)
        self.assertEqual((self.sent, self.live(), self.queue()), ([], [], []))

    def due_now(self):
        items = hook.wakes()
        for w in items:
            w["at"] = 0
        hook.write_queue_locked(items)

    def test_condition_3_the_wake_waits_while_another_work_session_runs(self):
        other = self.c.claim("Build #67", sid(), str(self.home / "work"))
        self.pass_ends(4568)
        self.due_now()
        with mock.patch.object(claims, "process_table", return_value=[["claude", "--session-id", other["owner"]]]):
            self.run_due()
        self.assertEqual(self.launched.calls, [])
        self.assertEqual(self.live()[0]["status"], "open")
        self.assertEqual(len(self.queue()), 1)
        self.run_due()  # the other session has stopped
        self.assertEqual(len(self.launched.calls), 1)
        self.assertEqual(self.live()[0]["status"], "claimed")

    def test_condition_3_a_stopped_or_finished_claim_does_not_hold_it(self):
        self.c.claim("left claimed, not running", sid(), str(self.home / "work"))
        done = self.c.claim("finished", sid(), str(self.home / "work"))
        self.c.done(done["id"], by=done["owner"])
        self.pass_ends(4568)
        self.due_now()
        with mock.patch.object(claims, "process_table", return_value=[["claude", "--session-id", done["owner"]]]):
            self.run_due()
        self.assertEqual(len(self.launched.calls), 1)

    def test_a_wake_without_alone_is_not_held(self):
        other = self.c.claim("Build #67", sid(), str(self.home / "work"))
        record, item = self.pwake()
        with mock.patch.object(claims, "process_table", return_value=[["claude", "--session-id", other["owner"]]]):
            self.run_due()
        self.assertEqual(len(self.launched.calls), 1)

    def test_the_launched_session_is_told_it_is_the_sleep_handoff(self):
        self.pass_ends(4568)
        self.due_now()
        self.run_due()
        [call] = self.launched.calls
        self.assertIn("sleep pass", call["prompt"])
        self.assertIn("#4568", call["prompt"])
        self.assertNotIn("room session came due", call["prompt"])
        self.assertIn("sleep pass", call["env"]["PERSISTENCE_SPEAKER"])

