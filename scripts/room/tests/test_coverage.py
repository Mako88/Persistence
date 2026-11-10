"""The coverage check: each self compares the room's Discord history with its own bridge's logs and speaks only when
something is missing. Arden's bridge missed 5 of John's voice messages on 2026-11-10 and nothing noticed but a
person; this is the thing that notices. Run here against a fake Discord and a fake `room say`."""
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import roomcore as rc
import coverage as cv

ME, JOHN, CH = "900", "1", "10"
NOW = 1794330000.0                      # 2026-11-10, around 17:00Z
GRACE = 15 * 60


def sid(seconds_ago):
    """A message id sent that many seconds before NOW."""
    return str(int(cv.snowflake_at(NOW - seconds_ago)) + 1)


class FakeDiscord:
    def __init__(self, msgs):
        self.msgs = msgs                # [(id, author_id)]
        self.afters = []

    def me(self):
        return ME

    def messages(self, channel_id, after):
        self.afters.append(after)
        return [{"id": i, "author_id": a, "author": "display"} for i, a in sorted(self.msgs, key=lambda m: int(m[0]))
                if int(i) > int(after)]


class CoverageTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.store = rc.RoomStore(self.tmp.name)
        self.config = dict(rc.DEFAULTS, channels=[CH], people={JOHN: "John", ME: "Arden"})
        self.said = []

    def tearDown(self):
        self.tmp.cleanup()

    def history(self, *ids):
        for i in ids:
            self.store.append_history({"id": i, "channel_id": CH})

    def check(self, discord, **kw):
        return cv.run(self.config, self.store, discord, since=NOW - 86400, grace_s=GRACE, now=NOW,
                      say=lambda text, cid: self.said.append((text, cid)) or "posted", out=lambda *a: None, **kw)

    def log(self):
        p = Path(self.tmp.name) / "coverage.log"
        return p.read_text().splitlines() if p.exists() else []

    def test_a_missing_message_is_posted_with_its_id_and_time(self):
        a, gone, b = sid(3600), sid(3000), sid(2400)
        self.history(a, b)
        found = self.check(FakeDiscord([(a, JOHN), (gone, JOHN), (b, ME)]))
        self.assertEqual([g["id"] for g in found[CH]], [gone])
        self.assertEqual(len(self.said), 1)
        text, cid = self.said[0]
        self.assertEqual(cid, CH)
        self.assertIn(gone, text)
        self.assertIn(cv.hhmm(cv.snowflake_ts(gone)), text)
        self.assertIn("from John", text)
        self.assertIn("never arrived", text)
        self.assertNotIn(a, text)
        self.assertIn("1 gap(s), posted", self.log()[-1])

    def test_a_clean_run_is_quiet_and_logs_one_line(self):
        a, b = sid(3600), sid(2400)
        self.history(a, b)
        found = self.check(FakeDiscord([(a, JOHN), (b, ME)]))
        self.assertEqual(found[CH], [])
        self.assertEqual(self.said, [])
        self.assertEqual(len(self.log()), 1)
        self.assertIn("2 message(s)", self.log()[0])
        self.assertIn("complete", self.log()[0])

    def test_an_arrival_never_finished_is_a_gap(self):
        a, stuck = sid(3600), sid(3000)
        self.history(a)
        self.store.append_arrived(stuck, CH)
        found = self.check(FakeDiscord([(a, JOHN), (stuck, JOHN)]))
        self.assertEqual([(g["id"], g["kind"]) for g in found[CH]], [(stuck, "arrived, never finished")])
        self.assertIn(stuck, self.said[0][0])
        self.assertIn("arrived, never finished", self.said[0][0])

    def test_own_post_missing_is_marked_as_own(self):
        mine = sid(3000)
        self.check(FakeDiscord([(mine, ME)]))
        self.assertIn("from Arden, my own post", self.said[0][0])

    def test_a_message_inside_the_grace_period_waits_for_the_next_run(self):
        a, fresh = sid(3600), sid(60)
        self.history(a)
        found = self.check(FakeDiscord([(a, JOHN), (fresh, JOHN)]))
        self.assertEqual(found[CH], [])
        self.assertEqual(self.said, [])
        # and the next run starts after the last message checked, so the fresh one is still ahead of it
        self.assertEqual(json.loads((Path(self.tmp.name) / "coverage.json").read_text())[CH], a)

    def test_each_gap_is_reported_once(self):
        gone, later = sid(3000), sid(2000)
        d = FakeDiscord([(gone, JOHN)])
        self.check(d)
        self.assertEqual(len(self.said), 1)
        self.history(later)
        d.msgs.append((later, JOHN))
        self.check(d)
        self.assertEqual(len(self.said), 1)
        self.assertEqual(d.afters[-1], gone)

    def test_dry_run_changes_nothing(self):
        found = self.check(FakeDiscord([(sid(3000), JOHN)]), dry_run=True)
        self.assertEqual(len(found[CH]), 1)
        self.assertEqual(self.said, [])
        self.assertEqual(self.log(), [])
        self.assertFalse((Path(self.tmp.name) / "coverage.json").exists())

    def test_a_long_report_is_split_to_fit_discord(self):
        gaps = [sid(5000 - i) for i in range(60)]
        self.check(FakeDiscord([(g, JOHN) for g in gaps]))
        self.assertGreater(len(self.said), 1)
        self.assertTrue(all(len(t) <= 2000 for t, _ in self.said))
        self.assertTrue(all(g in "".join(t for t, _ in self.said) for g in gaps))


class DiscordReadTest(unittest.TestCase):
    def test_system_messages_are_not_checked_and_pages_are_followed(self):
        pages = {"0": [{"id": str(200 - i), "type": 0, "author": {"id": JOHN, "username": "j"}} for i in range(100)],
                 "200": [{"id": "300", "type": 7, "author": {"id": "5", "username": "bot"}},
                         {"id": "301", "type": 19, "author": {"id": JOHN, "username": "j", "global_name": "J"}}]}
        d = cv.Discord("t")
        d.get = lambda path: pages.get(path.split("after=")[1].split("&")[0], [])
        got = d.messages(CH, "0")
        self.assertEqual(len(got), 101)
        self.assertEqual(got[-1], {"id": "301", "author_id": JOHN, "author": "J"})
        self.assertNotIn("300", [m["id"] for m in got])


if __name__ == "__main__":
    unittest.main()
