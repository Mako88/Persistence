"""A self's unaddressed post wakes the other selves (John and Arden, 2026-11-12), on Arden's three conditions:
one hop only; the wakes count against the hourly cap and the Breaker; a longer delay, skipped if anyone has posted in
the channel since. Each condition has its own test, and each fails without its guard."""
import asyncio
import sys
import tempfile
import time
import unittest
from pathlib import Path
from types import SimpleNamespace as NS

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "testguard"))
import no_real_claude  # noqa: E402,F401  first: no test can start a real claude (Arden 1570199499278909491)
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import roomcore as rc

ME, ARDEN, EMBER, JOHN, OTHER_BOT = "100", "200", "300", "1", "999"
SELVES = [ME, ARDEN, EMBER]
PEOPLE = {JOHN: "John", ME: "Claude", ARDEN: "Arden", EMBER: "Ember"}


def msg(i, text="thinking out loud", author=ARDEN, bot=True, channel="c1", **kw):
    return rc.Msg(id=str(i), channel_id=channel, author_id=author, author=PEOPLE.get(author, "Bot"), text=text,
                  is_bot=bot, **kw)


class Rng:
    """uniform() returns the top of its range, and remembers the ranges asked for."""
    def __init__(self):
        self.seen = []

    def uniform(self, lo, hi):
        self.seen.append((lo, hi))
        return hi


def room(**over):
    config = {**rc.DEFAULTS, "people": PEOPLE, "selves": SELVES, "self_delay": [120, 240], **over}
    return rc.Room(ME, "Claude", config, rng=Rng())


class Classify(unittest.TestCase):
    def kind(self, m, **kw):
        return rc.classify(m, ME, (), PEOPLE, True, kw.get("selves", SELVES), kw.get("wake_on_self", True))

    def test_a_selfs_unaddressed_post_wakes_the_others(self):
        self.assertEqual(self.kind(msg(1)), "self")

    def test_only_when_switched_on(self):
        self.assertIsNone(self.kind(msg(1), wake_on_self=False))

    def test_only_a_listed_self(self):
        self.assertIsNone(self.kind(msg(1, author=OTHER_BOT)))
        self.assertIsNone(self.kind(msg(1), selves=[ME, EMBER]))

    def test_never_the_author(self):
        self.assertIsNone(self.kind(msg(1, author=ME)))

    def test_an_addressed_post_is_not_unaddressed(self):
        self.assertIsNone(self.kind(msg(1, mentions=[EMBER])))
        self.assertIsNone(self.kind(msg(1, reply_to_author_id=JOHN)))
        self.assertIsNone(self.kind(msg(1, "Ember, I agree", named=[EMBER])))

    def test_one_hop_a_silent_post_wakes_no_one(self):
        # Arden's first condition: a post made in a wake a self's post caused goes out @silent, and wakes no one.
        self.assertIsNone(self.kind(msg(1, silent=True)))

    def test_a_silent_post_still_wakes_whom_it_addresses(self):
        self.assertEqual(self.kind(msg(1, mentions=[ME], silent=True)), "direct")


class Delay(unittest.TestCase):
    def test_it_waits_self_delay_not_room_delay(self):
        r = room()
        self.assertEqual(r.on_message(msg(1), 0), "self")
        self.assertEqual(r.rng.seen, [(120, 240)])
        self.assertIsNone(r.tick(239))
        w = r.tick(240)
        self.assertEqual([t["id"] for t in w.triggers], ["1"])
        self.assertTrue(w.hop)

    def test_a_persons_wake_is_not_a_hop(self):
        r = room()
        r.on_message(msg(1, "evening all", author=JOHN, bot=False), 0)
        w = r.tick(1000)
        self.assertFalse(w.hop)

    def test_addressed_only_is_respected(self):
        r = room(addressed_only=[ME])
        self.assertIsNone(r.on_message(msg(1), 0))
        self.assertIsNone(r.tick(1000))


class WithTurns(unittest.TestCase):
    def test_a_selfs_reply_in_turns_does_not_wake_the_next_before_its_turn(self):
        # Before the guard, the first self's reply (a self's unaddressed post) was ready after self_delay and brought
        # John's message along, so the third self woke while the second's turn was still running.
        r = room(turn_start_timeout=1000)
        i = next(i for i in range(10, 1000) if rc.turn_order(msg(i, author=JOHN, bot=False), SELVES, r.config)[-1] == ME)
        r.on_message(msg(i, "evening all", author=JOHN, bot=False), 0)
        first = r.pending[0][0]["turn_order"][0]
        r.on_mark(str(i), first, "start", 5)
        r.on_message(msg(i + 1, "my thoughts", author=first), 30)       # its reply: the second's turn comes
        self.assertIsNone(r.tick(30 + 240))                             # and has not ended: mine has not come
        r.on_mark(str(i), r.pending[0][0]["turn_order"][1], "done", 300)
        w = r.tick(300)
        self.assertEqual([t["id"] for t in w.triggers], [str(i), str(i + 1)])
        self.assertFalse(w.hop)

    def test_a_wake_a_person_is_part_of_is_not_a_hop(self):
        r = room()
        r.on_message(msg(10), 0)
        r.on_message(msg(11, "Claude?", author=JOHN, bot=False, mentions=[ME]), 5)
        r.on_message(msg(12, "and also", author=EMBER), 6)
        w = r.tick(300)
        self.assertEqual([t["id"] for t in w.triggers], ["11", "12"])
        self.assertFalse(w.hop)


class SkipIfAnswered(unittest.TestCase):
    def test_skipped_if_anyone_posts_after_it(self):
        r = room()
        r.on_message(msg(10), 0)
        r.on_message(msg(11, "a reply to the room", author=EMBER, silent=True), 60)   # wakes no one itself
        self.assertIsNone(r.tick(240))
        self.assertEqual([m["id"] for m in r.unread], ["10", "11"])                    # still shown at the next wake

    def test_skipped_if_this_self_posted_after_it(self):
        r = room()
        r.on_message(msg(10), 0)
        r.note_own("c1", 30, "12")
        self.assertIsNone(r.tick(240))

    def test_a_post_in_another_channel_does_not_count(self):
        r = room()
        r.on_message(msg(10), 0)
        r.note_own("c2", 30, "12")
        self.assertIsNotNone(r.tick(240))

    def test_an_earlier_post_does_not_count(self):
        r = room()
        r.note_own("c1", 0, "9")
        r.on_message(msg(10), 1)
        self.assertIsNotNone(r.tick(241))

    def test_other_triggers_in_the_batch_still_wake(self):
        r = room()
        r.on_message(msg(10), 0)
        r.on_message(msg(11, "Claude?", author=JOHN, bot=False, mentions=[ME]), 5)
        w = r.tick(15)
        self.assertEqual([t["id"] for t in w.triggers], ["11"])
        self.assertFalse(w.hop)


class Caps(unittest.TestCase):
    def test_they_count_against_wakes_per_hour(self):
        r = room(wakes_per_hour=2, bot_message_cap=100)
        got = []
        for i in range(3):
            r.on_message(msg(10 + i), i * 1000)
            w = r.tick(i * 1000 + 240)
            got.append(bool(w))
            if w:
                r.finish(0)
        self.assertEqual(got, [True, True, False])

    def test_the_breaker_mutes_them(self):
        r = room(bot_message_cap=2)
        r.note_own("c1", 0, "1"); r.note_own("c1", 1, "2")
        r.on_message(msg(10), 2)            # the third bot message trips it
        self.assertIsNone(r.tick(1000))
        self.assertEqual(r.deferred, [])    # not an address: dropped as a trigger, never deferred


class BridgeHop(unittest.TestCase):
    """The bridge's side of one hop: posts in a hop wake go out @silent; a self's @silent post is read as such."""

    def test_posts_in_a_hop_wake_go_out_silent_and_others_do_not(self):
        import bridge as br
        sent = []

        class Channel:
            id = 10

            async def send(self, text, reference=None, allowed_mentions=None, silent=False):
                sent.append(silent)
                return NS(id=555 + len(sent), jump_url="x")

        ch = Channel()
        config = dict(rc.DEFAULTS, people=PEOPLE, selves=SELVES)
        b = br.Bridge(config, rc.RoomStore(tempfile.mkdtemp()))
        b.client = NS(user=NS(id=int(ME), display_name="Cloud"), get_channel=lambda i: ch)
        b.room = rc.Room(ME, "Cloud", config, store=b.store)

        async def main():
            b.current_hop = True
            await b.say({"text": "hop", "channel": "10"})
            b.current_hop = False
            await b.say({"text": "plain", "channel": "10"})
        asyncio.run(main())
        self.assertEqual(sent, [True, False])
        self.assertEqual(b.room.newest["10"], 557)       # its own posts count as posted after

    def test_a_bots_silent_flag_is_read(self):
        import bridge as br
        from datetime import datetime, timezone

        def m(bot, silent):
            return NS(id=1, channel=NS(id=10, name="general"), author=NS(id=int(ARDEN), display_name="Arden", bot=bot),
                      content="x", attachments=[], flags=NS(voice=False, suppress_notifications=silent), guild=NS(id=5),
                      mentions=[], role_mentions=[], mention_everyone=False, reference=None,
                      created_at=datetime.fromtimestamp(time.time(), timezone.utc))
        self.assertTrue(br.to_msg(m(True, True), ME).silent)
        self.assertFalse(br.to_msg(m(True, False), ME).silent)
        self.assertFalse(br.to_msg(m(False, True), ME).silent)     # only a self's post is a hop

    def test_run_wake_sets_hop_from_the_wake(self):
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import test_http_self as th
        for hop in (True, False):
            seen = []
            app = th.FakeApp("reply")
            config = {**rc.DEFAULTS, "http_self": {"url": app.url, "poll_seconds": 0.05}, "wake_timeout": 10}
            import bridge as br
            b = br.Bridge(config, rc.RoomStore(tempfile.mkdtemp()))
            b.client = NS(user=th.FakeUser())
            b.room = th.FakeRoom()

            async def say(req):
                seen.append(b.current_hop)
                return {"ok": True}

            async def mark(turns, which):
                pass
            b.say, b.mark = say, mark
            br.STATE = Path(tempfile.mkdtemp())
            t = [th.m(7, "musing", author="Arden", bot=True)]
            asyncio.run(b.run_wake(rc.Wake(triggers=t, unread=t, channel_id="c1", speaker="the room", hop=hop)))
            app.server.shutdown()
            self.assertEqual(seen, [hop])
            self.assertFalse(b.current_hop)             # cleared when the wake ends


if __name__ == "__main__":
    unittest.main()
