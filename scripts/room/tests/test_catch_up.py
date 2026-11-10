"""Catching up: a message is recorded on arrival, before its transcription, and on start the bridge fetches what it
missed. Arden's bridge lost 5 of John's voice messages on 2026-11-10, each mid-transcription when it died or restarted.
The bridge runs here against a fake Discord: a client, a channel with a history, messages."""
import asyncio
import sys
import tempfile
import time
import unittest
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace as NS

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import roomcore as rc
import bridge as br

ME, JOHN, CH = 900, 1, 10
GUILD = NS(id=5)


def msg(i, text, author=JOHN, ago=0.0, channel=None):
    who = NS(id=author, display_name="John" if author == JOHN else "Cloud", bot=author != JOHN)
    return NS(id=i, channel=channel, author=who, content=text, attachments=[], flags=NS(voice=False), guild=GUILD,
              mentions=[], role_mentions=[], mention_everyone=False, reference=None,
              created_at=datetime.fromtimestamp(time.time() - ago, timezone.utc))


class FakeChannel:
    def __init__(self, cid):
        self.id, self.guild, self.name, self.messages = cid, GUILD, "general", []

    def post(self, *ms):
        for m in ms:
            m.channel = self
            self.messages.append(m)
        return ms

    async def history(self, after, oldest_first, limit):
        await asyncio.sleep(0.05)       # a request to Discord: live messages arrive meanwhile
        for m in sorted(self.messages, key=lambda m: m.id):
            if m.id > after.id:
                yield m

    async def fetch_message(self, i):
        return next(m for m in self.messages if m.id == i)


class FakeClient:
    def __init__(self, channel):
        self.user, self.channel = NS(id=ME, display_name="Cloud"), channel

    def get_channel(self, i):
        return self.channel if i == self.channel.id else None


class SlowBridge(br.Bridge):
    """describe() stands in for transcription: `slow[id]` seconds, or forever when the bridge is to die mid-way."""
    slow = {}

    async def describe(self, m):
        await asyncio.sleep(self.slow.get(m.id, 0))
        return m.content


def bridge_on(state, channel):
    config = dict(rc.DEFAULTS, channels=[str(CH)], people={str(JOHN): "John", str(ME): "Claude"}, selves=[],
                  names={})
    b = SlowBridge(config, rc.RoomStore(state))
    b.client = FakeClient(channel)
    b.room = rc.Room(ME, "Cloud", config, store=b.store)
    b.caught_up = asyncio.Event()
    return b


def logged(state):
    return [m["id"] for m in rc.RoomStore(state).history(1000)]


async def settle(b):
    while b.in_order.last:
        await asyncio.sleep(0.01)


class CatchUp(unittest.TestCase):
    def setUp(self):
        self.state = tempfile.mkdtemp()
        self.ch = FakeChannel(CH)
        SlowBridge.slow = {}

    def test_a_restart_replays_missed_messages_in_order_before_live_ones(self):
        (first,) = self.ch.post(msg(100, "before the restart", ago=60))
        rc.RoomStore(self.state).append_history(br.to_msg(first, ME).__dict__)
        self.ch.post(msg(101, "a long voice message", ago=50), msg(102, "short", ago=40), msg(103, "Cloud, there?", ago=30))
        SlowBridge.slow = {101: 0.2}

        async def main():
            b = bridge_on(self.state, self.ch)

            async def live():      # arrives while the catch-up is fetching, as on_message does
                await b.caught_up.wait()
                await b.take(self.ch.post(msg(104, "live", ago=0))[0])
            waiting = asyncio.create_task(live())
            await asyncio.sleep(0)
            await b.catch_up()
            await waiting
            await settle(b)
            return b
        b = asyncio.run(main())
        self.assertEqual(logged(self.state), ["100", "101", "102", "103", "104"])
        self.assertEqual([d["id"] for d in b.room.unread], ["101", "102", "103", "104"])
        self.assertEqual({d["id"] for d, _ in b.room.pending}, {"101", "102", "103", "104"})   # recent: they wake

    def test_a_crash_mid_transcription_is_finished_after_restart(self):
        (first,) = self.ch.post(msg(100, "seen", ago=120))
        rc.RoomStore(self.state).append_history(br.to_msg(first, ME).__dict__)
        (voice,) = self.ch.post(msg(101, "the 8.7-minute one", ago=60))
        SlowBridge.slow = {101: 3600}

        async def dies():
            b = bridge_on(self.state, self.ch)
            b.caught_up.set()
            run = asyncio.create_task(b.take(voice))
            await asyncio.sleep(0.05)
            run.cancel()         # the OOM kill
        asyncio.run(dies())
        self.assertEqual(logged(self.state), ["100"])
        # A later message reached history in the meantime, so "after the newest logged" alone would skip 101.
        (later,) = self.ch.post(msg(102, "after", ago=30))
        rc.RoomStore(self.state).append_history(br.to_msg(later, ME).__dict__)
        SlowBridge.slow = {}

        async def restarts():
            b = bridge_on(self.state, self.ch)
            await b.catch_up()
            await settle(b)
        asyncio.run(restarts())
        self.assertEqual(sorted(logged(self.state)), ["100", "101", "102"])
        self.assertEqual(rc.RoomStore(self.state).catch_up_state()[1], [])     # nothing owed any more

    def test_old_missed_messages_are_unread_and_wake_no_one(self):
        (first,) = self.ch.post(msg(100, "seen", ago=3600))
        rc.RoomStore(self.state).append_history(br.to_msg(first, ME).__dict__)
        self.ch.post(msg(101, "Cloud, from an hour ago", ago=1800), msg(102, "Cloud, just now", ago=5))

        async def main():
            b = bridge_on(self.state, self.ch)
            await b.catch_up()
            await settle(b)
            return b
        b = asyncio.run(main())
        self.assertEqual([d["id"] for d in b.room.unread], ["101", "102"])
        self.assertEqual([d["id"] for d, _ in b.room.pending], ["102"])

    def test_own_posts_missed_are_recorded_and_wake_no_one(self):
        (first,) = self.ch.post(msg(100, "seen", ago=60))
        rc.RoomStore(self.state).append_history(br.to_msg(first, ME).__dict__)
        self.ch.post(msg(101, "my upload", author=ME, ago=30))

        async def main():
            b = bridge_on(self.state, self.ch)
            await b.catch_up()
            await settle(b)
            return b
        b = asyncio.run(main())
        self.assertEqual(logged(self.state), ["100", "101"])
        self.assertEqual(b.room.unread, [])

    def test_a_message_is_taken_once_however_it_arrives(self):
        (first,) = self.ch.post(msg(100, "seen", ago=60))
        rc.RoomStore(self.state).append_history(br.to_msg(first, ME).__dict__)
        (both,) = self.ch.post(msg(101, "live and in history", ago=1))

        async def main():
            b = bridge_on(self.state, self.ch)
            await b.catch_up()
            self.assertIsNone(b.take(both))
            await settle(b)
        asyncio.run(main())
        self.assertEqual(logged(self.state), ["100", "101"])

    def test_no_history_yet_means_no_starting_point(self):
        self.ch.post(msg(100, "before the bridge ever ran", ago=60))

        async def main():
            b = bridge_on(self.state, self.ch)
            await b.catch_up()
            self.assertTrue(b.caught_up.is_set())
        asyncio.run(main())
        self.assertEqual(logged(self.state), [])


if __name__ == "__main__":
    unittest.main()


class SayLineBreaks(unittest.TestCase):
    """A self reads the room with line breaks shown as ⏎ and wrote them back that way, so John saw a literal ⏎ in
    Discord where a break was meant (2026-11-10). The mark is the bridge's own: it goes out as a line break."""

    def test_the_mark_goes_out_as_a_line_break(self):
        ch = FakeChannel(CH)
        sent = []

        async def send(text, reference=None, allowed_mentions=None):
            sent.append(text)
            return NS(id=555, jump_url="https://discord/555")
        ch.send = send

        async def main():
            b = bridge_on(tempfile.mkdtemp(), ch)
            return b, await b.say({"text": "Arden, merged. ⏎ - **Tests:** 106 ⏎ - **Docs:** done", "channel": str(CH)})
        b, r = asyncio.run(main())
        self.assertTrue(r["ok"])
        self.assertEqual(sent, ["Arden, merged.\n- **Tests:** 106\n- **Docs:** done"])
        self.assertEqual(b.store.history(1)[0]["text"], sent[0])     # history keeps what Discord got
