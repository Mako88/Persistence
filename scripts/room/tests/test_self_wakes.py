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


class HopThroughAddress(unittest.TestCase):
    def test_a_silent_post_that_addresses_this_self_wakes_it_as_a_hop(self):
        # C2 (Arden, d846149): Ember's hop post "Claude, ..." woke Claude directly with hop=False, so Claude's
        # unaddressed reply went out loud and woke everyone again.
        r = room()
        self.assertEqual(r.on_message(msg(1, "Claude, what do you think?", author=EMBER, silent=True), 0), "direct")
        w = r.tick(1000)
        self.assertTrue(w.hop)

    def test_a_loud_post_that_addresses_this_self_is_not_a_hop(self):
        r = room()
        r.on_message(msg(1, "Claude, what do you think?", author=EMBER), 0)
        self.assertFalse(r.tick(1000).hop)

    def test_a_persons_message_alongside_still_makes_it_theirs(self):
        r = room()
        r.on_message(msg(1, "Claude, what do you think?", author=EMBER, silent=True), 0)
        r.on_message(msg(2, "Claude?", author=JOHN, bot=False, mentions=[ME]), 1)
        self.assertFalse(r.tick(1000).hop)


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


    def test_a_late_older_post_does_not_undo_a_newer_one(self):
        # The monotonic guard in note_posted (Arden's mutation of it survived): out-of-order delivery never lowers it.
        r = room()
        r.on_message(msg(15), 0)
        r.note_posted("c1", 20)
        r.note_posted("c1", 16)
        self.assertEqual(r.newest["c1"], 20)
        self.assertIsNone(r.tick(1000))


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

    def bridge(self, history=()):
        import bridge as br
        sent = []

        class Channel:
            id = 10

            async def send(self, text, reference=None, allowed_mentions=None, silent=False):
                sent.append(silent)
                return NS(id=555 + len(sent), jump_url="x")

            async def fetch_message(self, i):
                raise LookupError(i)

        config = dict(rc.DEFAULTS, people=PEOPLE, selves=SELVES)
        b = br.Bridge(config, rc.RoomStore(tempfile.mkdtemp()))
        for h in history:
            b.store.append_history(h)
        b.client = NS(user=NS(id=int(ME), display_name="Cloud"), get_channel=lambda i: Channel())
        b.room = rc.Room(ME, "Cloud", config, store=b.store)
        return b, sent

    def test_only_the_hop_wakes_own_posts_go_out_silent(self):
        # C1 (Arden, d846149): silent went by time, so a work session's report during a hop wake went out @silent.
        b, sent = self.bridge()

        async def main():
            b.begin(rc.Wake(triggers=[], unread=[], channel_id="10", speaker="x", hop=True))
            token = b.wake_token
            await b.say({"text": "hop", "channel": "10", "wake": token})        # the wake's own
            await b.say({"text": "a work session's report", "channel": "10"})   # no token: not the wake's
            await b.say({"text": "forged", "channel": "10", "wake": "nope"})
            b.begin(None)
            await b.say({"text": "left running by the hop wake", "channel": "10", "wake": token})
            b.begin(rc.Wake(triggers=[], unread=[], channel_id="10", speaker="x", hop=False))
            await b.say({"text": "a person's wake", "channel": "10", "wake": b.wake_token})
        asyncio.run(main())
        self.assertEqual(sent, [True, False, False, True, False])
        self.assertEqual(b.room.newest["10"], 560)      # its own posts count as posted after

    def test_a_hop_token_expires_and_is_dropped(self):
        # Ember's review of b2eb6df: hop_tokens grew forever. A token keeps its posts silent for HOP_TOKEN_SECONDS
        # (hours, for detached work), then goes out loud like any other, and the next wake drops it.
        import bridge as br
        from unittest import mock
        b, sent = self.bridge()
        t0 = 1_000_000.0
        with mock.patch.object(br.time, "time", return_value=t0):
            b.begin(rc.Wake(triggers=[], unread=[], channel_id="10", speaker="x", hop=True))
            token = b.wake_token
            b.begin(None)
        self.assertGreaterEqual(br.HOP_TOKEN_SECONDS, 3600)               # hours, not minutes
        late = t0 + br.HOP_TOKEN_SECONDS - 1
        with mock.patch.object(br.time, "time", return_value=late):
            asyncio.run(b.say({"text": "detached work, late", "channel": "10", "wake": token}))
            b.begin(None)
            self.assertIn(token, b.hop_tokens)                            # still young: kept
        with mock.patch.object(br.time, "time", return_value=t0 + br.HOP_TOKEN_SECONDS):
            asyncio.run(b.say({"text": "too late", "channel": "10", "wake": token}))
            b.begin(None)
        self.assertEqual(sent, [True, False])
        self.assertNotIn(token, b.hop_tokens)                             # dropped: the set is bounded

    def test_a_hop_post_to_people_only_goes_out_loud(self):
        # Arden's minor item: after C2, one that addresses only a person may ping them; one that addresses a self not.
        b, sent = self.bridge(history=[{"id": "70", "channel_id": "10", "author_id": JOHN, "author": "John", "text": "q"},
                                       {"id": "71", "channel_id": "10", "author_id": ARDEN, "author": "Arden", "text": "q"}])

        async def main():
            b.begin(rc.Wake(triggers=[], unread=[], channel_id="10", speaker="x", hop=True))
            for text, reply in [("John, have a look", None), (f"<@{JOHN}> see this", None), ("answering you", "70"),
                                ("Arden, have a look", None), (f"<@{JOHN}> and <@{EMBER}>", None),
                                ("answering you", "71"), ("answering you", "999"), ("to no one", None),
                                (f"<@{OTHER_BOT}> hi", None), (f"<@{JOHN}> and <@&5>", None),
                                ("John, look @everyone", None)]:
                await b.say({"text": text, "channel": "10", "reply": reply, "wake": b.wake_token})
        asyncio.run(main())
        self.assertEqual(sent, [False, False, False, True, True, True, True, True, True, True, True])

    def test_a_command_wakes_room_say_carries_its_token(self):
        # End to end: a hop wake's process runs the real `room say` over the bridge's socket, and the request it sends
        # carries the wake's hop token (the env var the bridge sets, the CLI reads and sends).
        import bridge as br
        import contextlib
        import io
        d = Path(tempfile.mkdtemp())
        sock = d / "room.sock"
        cli = Path(__file__).resolve().parent.parent / "room"
        (d / "wake.py").write_text("import subprocess, sys\n"
                                   f"subprocess.run([sys.executable, {str(cli)!r}, 'say', 'a hop post'], check=True)\n")
        config = {**rc.DEFAULTS, "wake_timeout": 20, "cwd": str(d)}
        b = br.Bridge(config, rc.RoomStore(d))
        b.client = NS(user=NS(id=int(ME), display_name="Cloud"))
        b.room = NS(finish=lambda now: None)
        seen = []

        async def say(req):
            seen.append(req.get("wake") in b.hop_tokens)
            return {"ok": True, "id": "1", "link": "x"}

        async def mark(turns, which):
            pass
        b.say, b.mark = say, mark
        br.STATE = d
        real_argv, real_sock = rc.wake_argv, rc.socket_path
        rc.wake_argv = lambda *a, **k: ([sys.executable, str(d / "wake.py")], None)
        rc.socket_path = lambda: sock
        t = [{"id": "7", "channel_id": "c1", "author": "Arden", "author_id": ARDEN, "text": "musing", "is_bot": True,
              "ts": time.time()}]

        async def main():
            server = await asyncio.start_unix_server(b.handle, path=str(sock))
            async with server:
                await b.run_wake(rc.Wake(triggers=t, unread=t, channel_id="c1", speaker="the room", hop=True))
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                asyncio.run(main())
        finally:
            rc.wake_argv, rc.socket_path = real_argv, real_sock
        self.assertEqual(seen, [True])

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
                seen.append(req.get("wake") in b.hop_tokens)
                return {"ok": True}

            async def mark(turns, which):
                pass
            b.say, b.mark = say, mark
            br.STATE = Path(tempfile.mkdtemp())
            t = [th.m(7, "musing", author="Arden", bot=True)]
            asyncio.run(b.run_wake(rc.Wake(triggers=t, unread=t, channel_id="c1", speaker="the room", hop=hop)))
            app.server.shutdown()
            self.assertEqual(seen, [hop])
            self.assertIsNone(b.wake_token)             # cleared when the wake ends


if __name__ == "__main__":
    unittest.main()
