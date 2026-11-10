#!/usr/bin/env python3
"""The room bridge: one process per self, run as that self's Linux user. No model in it.

It holds the Discord gateway connection, decides (roomcore.Room) when the self is woken, runs the wake command,
and posts what the self says through `room say` over a local unix socket. See README.md.
"""
import asyncio
import json
import os
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import roomcore as rc

CONFIG_DIR = rc.CONFIG_DIR
STATE = CONFIG_DIR / "state"


def to_msg(m, me_id):
    """discord.Message -> roomcore.Msg. The only place that knows discord.py's shapes (besides the client below)."""
    import discord
    ref = m.reference.resolved if m.reference and isinstance(m.reference.resolved, discord.Message) else None
    return rc.Msg(
        id=str(m.id), channel_id=str(m.channel.id), author_id=str(m.author.id), author=m.author.display_name,
        text=rc.with_attachments(m.content, [a.filename for a in m.attachments], bool(getattr(m.flags, "voice", False))),
        is_bot=m.author.bot, is_dm=m.guild is None,
        mentions=[str(u.id) for u in m.mentions], role_mentions=[str(r.id) for r in m.role_mentions],
        mention_everyone=m.mention_everyone, reply_to_author_id=str(ref.author.id) if ref else None,
        channel_name=getattr(m.channel, "name", None) or "DM", ts=m.created_at.timestamp())


class Bridge:
    def __init__(self, config, store):
        self.config, self.store = config, store
        self.room = None
        self.client = None
        self.current_channel = None     # the channel of the wake that is running

    # -- the wake
    async def run_wake(self, wake):
        prompt = rc.build_prompt(wake.unread, wake.triggers, self.client.user.display_name, self.client.user.id)
        argv, new_session = rc.wake_argv(self.config, self.store)
        env = {**os.environ, "PERSISTENCE_SPEAKER": wake.speaker, "PERSISTENCE_ROOM_CHANNEL": wake.channel_id,
               "PERSISTENCE_ROOM_SOCKET": str(rc.socket_path())}
        cwd = self.config["cwd"] or (str(Path.home() / "work") if (Path.home() / "work").is_dir() else str(Path.home()))
        self.current_channel = wake.channel_id
        log = STATE / f"wake-{int(time.time())}.json"
        try:
            with log.open("w") as out:
                proc = await asyncio.create_subprocess_exec(
                    *argv, cwd=cwd, env=env, stdin=asyncio.subprocess.PIPE, stdout=out, stderr=asyncio.subprocess.STDOUT)
                try:
                    await asyncio.wait_for(proc.communicate(prompt.encode()), self.config["wake_timeout"])
                except asyncio.TimeoutError:
                    proc.kill()
                    print(f"wake timed out after {self.config['wake_timeout']}s", flush=True)
            if new_session and proc.returncode == 0:
                self.store.save_session({"id": new_session, "created": True})
            elif proc.returncode:
                print(f"wake exited {proc.returncode}; see {log}", flush=True)
        except OSError as e:
            print(f"wake could not start: {e}", flush=True)
        finally:
            self.current_channel = None
            self.room.finish(time.time())

    async def ticker(self):
        while True:
            await asyncio.sleep(1)
            wake = self.room.tick(time.time())
            if wake:
                asyncio.create_task(self.run_wake(wake))

    # -- the local socket: one JSON request per connection
    async def handle(self, reader, writer):
        try:
            req = json.loads((await reader.readline()).decode())
            if req.get("op") == "say":
                reply = await self.say(req)
            elif req.get("op") == "read":
                reply = {"ok": True, "messages": self.store.history(int(req.get("limit") or 20), req.get("channel"), req.get("id"))}
            else:
                reply = {"ok": False, "error": "unknown request"}
        except Exception as e:  # report to the CLI rather than drop the connection
            reply = {"ok": False, "error": f"{type(e).__name__}: {e}"}
        writer.write((json.dumps(reply) + "\n").encode())
        await writer.drain()
        writer.close()

    async def say(self, req):
        import discord
        bad = rc.validate_post(req.get("text", ""))
        if bad:
            return {"ok": False, "error": bad}
        channel_id = req.get("channel") or self.current_channel or self.config["default_channel"]
        if not channel_id:
            return {"ok": False, "error": "no channel: no wake is running here and none was given (--channel)"}
        channel = self.client.get_channel(int(channel_id)) or await self.client.fetch_channel(int(channel_id))
        ref = None
        if req.get("reply"):
            ref = discord.MessageReference(message_id=int(req["reply"]), channel_id=int(channel_id), fail_if_not_exists=True)
        # No @everyone/@here or role pings from a self: a loop hazard. Mentioning people and selves works.
        sent = await channel.send(req["text"], reference=ref,
                                  allowed_mentions=discord.AllowedMentions(everyone=False, roles=False, users=True, replied_user=True))
        self.room.note_own(channel_id, time.time())
        self.store.append_history({
            "id": str(sent.id), "channel_id": str(channel_id), "author_id": str(self.client.user.id),
            "author": rc.name_of(self.client.user.id, self.client.user.display_name, self.config["people"]), "text": req["text"], "is_bot": True, "ts": time.time()})
        return {"ok": True, "id": str(sent.id), "link": sent.jump_url}

    # -- start
    def start(self, token):
        import discord
        intents = discord.Intents.default()
        intents.message_content = True      # must also be enabled for the bot in the developer portal
        self.client = discord.Client(intents=intents)
        bridge = self

        @self.client.event
        async def on_ready():
            if bridge.room:
                return
            bridge.room = rc.Room(bridge.client.user.id, bridge.client.user.display_name, bridge.config, store=bridge.store)
            bridge.room.unread = bridge.store.load_unread()
            sock = rc.socket_path()
            sock.unlink(missing_ok=True)
            old = os.umask(0o177)
            try:
                await asyncio.start_unix_server(bridge.handle, path=str(sock))
            finally:
                os.umask(old)
            asyncio.create_task(bridge.ticker())
            print(f"room bridge up as {bridge.client.user} ({bridge.client.user.id})", flush=True)

        @self.client.event
        async def on_message(m):
            if bridge.room:
                bridge.room.on_message(to_msg(m, bridge.client.user.id), time.time())

        self.client.run(token)


def main():
    config = rc.load_config(os.environ.get("PERSISTENCE_ROOM_CONFIG"))
    token = rc.read_token(config["token_path"])     # refuses a token file others can read
    STATE.mkdir(parents=True, exist_ok=True, mode=0o700)
    Bridge(config, rc.RoomStore(STATE)).start(token)


if __name__ == "__main__":
    main()
