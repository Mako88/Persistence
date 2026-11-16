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
    """The store as the sleep pass and its handoff use it, mirroring FragmentStore.Sleep.cs: sleep-begin WRITES the
    pass's record (tagged sleep/pass and sleep/open) and sleep-end REWRITES that same fragment and drops sleep/open.
    An earlier fake gave each night a new id at the end, and hid that `newest before` was already tonight's record
    (Arden's review of #73: #4802 before = after, nothing ever handed on)."""

    def __init__(self, first=4168):
        self.records, self.tags, self.next, self.open = {}, {}, first, None

    def begin(self):
        rid, self.next = str(self.next), self.next + 1
        self.records[rid] = "Sleep pass, 2026-11-16: open since 01:00Z, until 05:00Z, proposing only."
        self.tags[rid] = ["sleep/pass", "sleep/open"]
        self.open = rid
        return f"Sleep pass #{rid} is open until 05:00Z. It proposes only: ..."

    def end(self, template=WITH_PROPOSALS):
        rid, self.open = self.open, None
        self.records[rid] = template.format(id=rid) + "\n\nWhat the store logged of this pass: no changes."
        self.tags[rid] = ["sleep/pass"]

    def render(self, rid):
        return f"[#{rid} | Summary | i:0.6 c:1 | tags: {', '.join(self.tags[rid])} | 2026-11-16]\n{self.records[rid]}"

    def __call__(self, *args, **kwargs):
        verb = args[0]
        if verb == "sleep-begin":
            if self.open:
                return (2, "", f"A sleep pass is already open (#{self.open}, until 05:00Z).")
            return (0, self.begin(), "")
        if verb == "sleep-status":
            return (0, self.render(self.open) if self.open else "No sleep pass is open.", "")
        if verb == "sleep-end":
            if not self.open:
                return (2, "", "No sleep pass is open.")
            content = args[args.index("--content") + 1]
            self.end(content.replace("{", "{{").replace("}", "}}"))
            return (0, "", "")
        if verb == "sleep-list":
            return (0, "## Worklist\n\nids for joining use counts\n", "")
        if verb == "list" and "sleep/pass" in args:
            n = max(self.records, key=int) if self.records else None
            return (0, self.render(n) + "\n" if n else "", "")
        if verb == "show":
            rid = str(args[1]).lstrip("#")
            return (0, self.render(rid), "") if rid in self.records else (2, "", "no such fragment")
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

    def pass_ends(self, template=WITH_PROPOSALS, end=True):
        """One night as the store runs it: sleep-begin writes the record, the session's sleep-end rewrites it, and the
        handoff gets the id sleep_pass gets, parsed from begin's reply. `end` False: the pass is still open."""
        opened = self.store.begin()
        if end:
            self.store.end(template)
        return hook.sleep_handoff(hook.pass_record(opened), self.night, home=self.home)

    def live(self):
        return [r for r in self.c.all() if r["subject"] == hook.SLEEP_SUBJECT and r["status"] not in claims.TERMINAL]

    def test_a_record_with_proposals_makes_one_inbox_item_one_claim_and_one_wake(self):
        self.pass_ends()
        self.assertEqual(len(self.sent), 1)
        [claim] = self.live()
        self.assertEqual((claim["status"], claim["dispatch"], claim["origin_session"]), ("open", "handoff", self.night))
        self.assertIn("#4168", claim["task"])
        [item] = self.queue()
        self.assertEqual((item["claim"], item["work"], item["alone"]), (claim["id"], True, True))
        self.assertEqual(datetime.fromtimestamp(item["at"]).strftime("%H:%M"), "07:30")

    def test_the_sleep_pass_hands_on_the_record_its_begin_wrote(self):
        """Through sleep_pass itself, with the store's begin/end cycle: the record is written at begin and edited at
        end, so 'newest before vs after' always matched. Fails on e6b8340 ('this pass wrote no record')."""
        self.store.begin()  # an earlier night's record, so 'newest' is not empty before tonight
        self.store.end()
        self.sent.clear()

        def session(*args, **kwargs):  # the night's claude session: it writes its record with sleep-end
            self.store("sleep-end", "--content", WITH_PROPOSALS.format(id="tonight"))
            return mock.Mock(returncode=0)

        with mock.patch.object(hook, "store", side_effect=lambda *a, **k: self.store(*a, **k)[1]), \
                mock.patch.object(hook, "USES", self.state / "uses.jsonl"), \
                mock.patch.object(hook.subprocess, "run", side_effect=session):
            self.assertEqual(hook.sleep_pass(["--propose"]), 0)
        self.assertEqual(self.sent, [["work", "Sleep record #4169: its proposals are for the sleep handoff claim."]])
        [claim] = self.live()
        self.assertIn("#4169", claim["task"])
        self.assertNotIn("#4168", claim["task"])

    def test_condition_2_only_the_record_id_goes_in_the_inbox(self):
        self.pass_ends()
        [[topic, *words]] = self.sent
        text = " ".join(words)
        self.assertEqual(topic, "work")
        self.assertEqual(re.findall(r"\d+", text), ["4168"])
        self.assertNotIn("SECRET-COMMAND-TEXT", text)
        self.assertNotIn("SECRET-COMMAND-TEXT", self.live()[0]["task"])

    def test_a_record_saying_proposals_none_makes_nothing(self):
        self.pass_ends(NONE)
        self.assertEqual((self.sent, self.live(), self.queue()), ([], [], []))

    def test_a_pass_still_open_is_not_handed_on(self):
        self.pass_ends(end=False)
        self.assertEqual((self.sent, self.live(), self.queue()), ([], [], []))

    def test_a_begin_reply_without_a_record_id_hands_on_nothing(self):
        self.store.begin()
        self.store.end()
        hook.sleep_handoff(hook.pass_record("A sleep pass is already open (#4168, until 05:00Z)."), self.night, home=self.home)
        self.assertEqual((self.sent, self.live(), self.queue()), ([], [], []))

    def test_a_record_that_is_not_a_sleep_pass_is_not_handed_on(self):
        self.store.records["77"], self.store.tags["77"] = WITH_PROPOSALS.format(id=77), ["notes"]
        hook.sleep_handoff("77", self.night, home=self.home)
        self.assertEqual((self.sent, self.live(), self.queue()), ([], [], []))

    def test_a_second_run_on_the_same_record_makes_no_duplicate(self):
        self.pass_ends()
        hook.sleep_handoff("4168", self.night, home=self.home)
        self.assertEqual(len(self.sent), 1)
        self.assertEqual(len(self.live()), 1)
        self.assertEqual(len(self.queue()), 1)

    def test_condition_1_no_stacking_a_second_night_joins_the_live_claim(self):
        self.pass_ends()
        first_task = self.live()[0]["task"]
        self.pass_ends()  # the first night's claim isn't done
        [claim] = self.live()
        self.assertIn("#4168", claim["task"])
        self.assertIn("#4169", claim["task"])
        self.assertEqual(len(self.queue()), 1, "one wake for the one claim")
        self.assertEqual(len(self.sent), 2, "each night's id reaches the inbox, which a running claim's session also reads")
        self.assertEqual(claim["history"][-1]["action"], "amend")
        # Arden's review: the claim's old wording is kept in its history, not lost
        self.assertEqual(claim["history"][-1]["task_was"], first_task)

    def test_condition_1_joins_a_claim_already_taken_and_paused(self):
        self.pass_ends()
        self.due_now()
        self.run_due()
        [claim] = self.live()
        self.c.pause(claim["id"], by=claim["owner"], why="waiting on Arden")
        self.pass_ends()
        [joined] = self.live()
        self.assertEqual(joined["id"], claim["id"])
        self.assertIn("#4169", joined["task"])
        # Arden's review: a join widens the task only; it never reopens or reassigns the claim
        self.assertEqual((joined["status"], joined["owner"]), ("paused", claim["owner"]))

    def test_condition_1_joining_a_claimed_claim_keeps_it_claimed_by_its_owner(self):
        self.pass_ends()
        self.due_now()
        self.run_due()
        [claim] = self.live()
        self.assertEqual(claim["status"], "claimed")
        self.pass_ends()
        [joined] = self.live()
        self.assertEqual((joined["id"], joined["status"], joined["owner"]), (claim["id"], "claimed", claim["owner"]))

    def test_after_the_claim_is_done_the_next_night_gets_its_own(self):
        self.pass_ends()
        self.due_now()
        self.run_due()
        [claim] = self.live()
        self.c.done(claim["id"], by=claim["owner"])
        self.pass_ends()
        [new] = self.live()
        self.assertNotEqual(new["id"], claim["id"])
        self.assertNotIn("#4168", new["task"])

    def test_with_the_setting_unset_nothing_is_handed_on(self):
        with mock.patch.dict(os.environ, {"PERSISTENCE_SLEEP_HANDOFF_AT": ""}):
            self.pass_ends()
        self.assertEqual((self.sent, self.live(), self.queue()), ([], [], []))

    def due_now(self):
        items = hook.wakes()
        for w in items:
            w["at"] = 0
        hook.write_queue_locked(items)

    def test_condition_3_the_wake_waits_while_another_work_session_runs(self):
        other = self.c.claim("Build #67", sid(), str(self.home / "work"))
        self.pass_ends()
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
        self.pass_ends()
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
        self.pass_ends()
        self.due_now()
        self.run_due()
        [call] = self.launched.calls
        self.assertIn("sleep pass", call["prompt"])
        self.assertIn("#4168", call["prompt"])
        self.assertNotIn("room session came due", call["prompt"])
        self.assertIn("sleep pass", call["env"]["PERSISTENCE_SPEAKER"])

