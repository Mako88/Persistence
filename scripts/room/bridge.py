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


def to_msg(m, me_id, text=None):
    """discord.Message -> roomcore.Msg. The only place that knows discord.py's shapes (besides the client below).
    `text` is the message's text with its attachments described (Bridge.describe); without it, names only."""
    import discord
    ref = m.reference.resolved if m.reference and isinstance(m.reference.resolved, discord.Message) else None
    if text is None:
        text = rc.with_attachments(m.content, [a.filename for a in m.attachments], bool(getattr(m.flags, "voice", False)))
    return rc.Msg(
        id=str(m.id), channel_id=str(m.channel.id), author_id=str(m.author.id), author=m.author.display_name,
        text=text,
        is_bot=m.author.bot, is_dm=m.guild is None,
        mentions=[str(u.id) for u in m.mentions], role_mentions=[str(r.id) for r in m.role_mentions],
        mention_everyone=m.mention_everyone, reply_to_author_id=str(ref.author.id) if ref else None,
        channel_name=getattr(m.channel, "name", None) or "DM", ts=m.created_at.timestamp())


def safe_name(name):
    """An attachment's file name made safe to write: no directories, no leading dots."""
    name = "".join(c if c.isalnum() or c in "._-" else "_" for c in Path(str(name)).name).lstrip(".")
    return name or "attachment"


TRANSCRIBER = Path(__file__).resolve().parent / "transcribe.py"
SETTINGS = ("transcriber", "parakeet_dir", "vad_dir", "whisper_model", "whisper_dir", "pause_seconds")


def own_rss_mb():
    """This process's resident memory, logged with each transcription to watch for growth (Arden's OOM, 2026-11-10)."""
    try:
        for line in Path("/proc/self/status").read_text().splitlines():
            if line.startswith("VmRSS:"):
                return int(line.split()[1]) // 1024
    except OSError:
        pass
    return None


def transcribe(path, config):
    """A voice message's text with its pauses marked, made by transcribe.py in a child process so the model's memory
    goes back to the system when it exits: loaded in the bridge, faster-whisper's never was (one 171 s clip left the
    bridge at 673 MB; Arden's reached 2.8 GB and was OOM-killed, 2026-11-10). None if the child fails. Timings, the
    child's peak and the bridge's own resident size go to state/timings.jsonl."""
    settings = {k: config[k] for k in SETTINGS if k in config} | {"threads": config.get("transcribe_threads", 2)}
    t0 = time.time()
    r = subprocess.run([sys.executable, str(TRANSCRIBER), str(path), json.dumps(settings)], capture_output=True,
                       text=True, timeout=config.get("transcribe_timeout", 600))
    if r.returncode != 0:
        print(f"transcribe.py failed ({r.returncode}) on {path}: {r.stderr[-2000:]}", flush=True)
        return None
    out = json.loads(r.stdout.strip().splitlines()[-1])
    log_timing({"what": "transcribe", "file": str(path), **{k: v for k, v in out.items() if k != "text"},
                "wall_s": round(time.time() - t0, 1), "bridge_rss_mb": own_rss_mb()})
    return out["text"]


def log_timing(d):
    try:
        with (STATE / "timings.jsonl").open("a", encoding="utf-8") as f:
            f.write(json.dumps({"at": round(time.time(), 1), **d}) + "\n")
    except OSError as e:
        print(f"could not log timing {d}: {e}", flush=True)


def http_json(method, url, body=None, headers=None, timeout=30):
    """One JSON request to a self's app (stdlib: the bridge has no HTTP client of its own). An empty body is None."""
    import urllib.request
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"Content-Type": "application/json", **(headers or {})})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        raw = r.read()
    return json.loads(raw) if raw.strip() else None


class Bridge:
    def __init__(self, config, store):
        self.config, self.store = config, store
        self.room = None
        self.client = None
        self.current_channel = None     # the channel of the wake that is running
        self.in_order = rc.InOrder()

    # -- attachments: saved for the woken self to Read; a voice message transcribed
    async def describe(self, m):
        voice = bool(getattr(m.flags, "voice", False))
        if not m.attachments:
            return rc.with_attachments(m.content, [], voice)
        folder = STATE / "attachments" / str(m.id)
        saved, transcript = {}, None
        for a in m.attachments:
            if a.size and a.size > self.config["attachment_max_bytes"]:
                continue
            try:
                folder.mkdir(parents=True, exist_ok=True, mode=0o700)
                path = folder / safe_name(a.filename)
                await a.save(path)
            except Exception as e:
                print(f"could not save attachment {a.filename} of {m.id}: {e}", flush=True)
                continue
            kind = "image" if (a.content_type or "").startswith("image/") else "file"
            saved[a.filename] = (kind, str(path))
            if voice and transcript is None:
                try:
                    make = lambda: transcribe(path, self.config)
                    transcript, how = await asyncio.to_thread(
                        rc.shared_transcript, self.config["transcript_cache"], m.id, make)
                    log_timing({"what": "transcript", "message": str(m.id), "how": how})
                except Exception as e:
                    print(f"could not transcribe {path}: {e}", flush=True)
        return rc.with_attachments(m.content, [a.filename for a in m.attachments], voice, saved, transcript)

    # -- marks: a reaction on each message that woke this self, when the wake starts and when it ends
    async def mark(self, turns, which):
        emoji = self.config["turn_marks"][which]
        for t in turns:
            try:
                channel = self.client.get_channel(int(t["channel_id"])) or await self.client.fetch_channel(int(t["channel_id"]))
                await channel.get_partial_message(int(t["id"])).add_reaction(emoji)
            except Exception as e:     # the others pass this self over after turn_start_timeout
                print(f"could not mark {t['id']} {which}: {e}", flush=True)
            self.room.on_mark(t["id"], self.room.me_id, which, time.time())

    # -- the wake
    async def run_wake(self, wake):
        if self.config["http_self"]:
            return await self.run_http_wake(wake)
        prompt = rc.build_prompt(wake.unread, wake.triggers, self.client.user.display_name, self.client.user.id,
                                 tail=self.config["context_tail"])
        argv, new_session = rc.wake_argv(self.config, self.store)
        env = {**os.environ, "PERSISTENCE_SPEAKER": wake.speaker, "PERSISTENCE_ROOM_CHANNEL": wake.channel_id,
               "PERSISTENCE_ROOM_SOCKET": str(rc.socket_path())}
        cwd = self.config["cwd"] or (str(Path.home() / "work") if (Path.home() / "work").is_dir() else str(Path.home()))
        self.current_channel = wake.channel_id
        log = STATE / f"wake-{int(time.time())}.json"
        await self.mark(wake.to_mark, "start")
        started = time.time()
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
            # From the newest trigger's arrival: the delay, the turn wait and the wake itself, as John feels it.
            newest = max((t.get("ts") or 0) for t in wake.triggers)
            log_timing({"what": "wake", "triggers": [t["id"] for t in wake.triggers], "wake_s": round(time.time() - started, 1),
                        "since_message_s": round(time.time() - newest, 1) if newest else None})
            self.current_channel = None
            await self.mark(wake.to_mark, "done")
            self.room.finish(time.time())

    async def run_http_wake(self, wake):
        """A self served over HTTP by the Persistence app (`http_self`; Ember, 2026-11-10): the room prompt goes to its
        conversation API, and its reply event is posted here as its bot, since it has no `room say`. A silent turn
        posts nothing. Marks, timings and the turn's end are as for a command wake."""
        prompt = rc.build_prompt(wake.unread, wake.triggers, self.client.user.display_name, self.client.user.id,
                                 by_reply=True, tail=self.config["context_tail"])
        base = self.config["http_self"]["url"].rstrip("/")
        poll = self.config["http_self"].get("poll_seconds", 2)
        self.current_channel = wake.channel_id
        await self.mark(wake.to_mark, "start")
        started = time.time()
        outcome = None
        try:
            since = (await asyncio.to_thread(http_json, "GET", f"{base}/events?since=999999999"))["latest"]
            await asyncio.to_thread(http_json, "POST", f"{base}/send", {"input": prompt}, {"X-Local-Peer": wake.speaker.encode("ascii", "replace").decode()})
            while time.time() - started < self.config["wake_timeout"]:
                await asyncio.sleep(poll)
                got = await asyncio.to_thread(http_json, "GET", f"{base}/events?since={since}")
                if got["latest"] < since:       # the app restarted and its event log began again
                    print(f"the self's event log restarted (latest {got['latest']} < {since}); the turn is lost", flush=True)
                    break
                outcome = rc.http_reply(got["events"], since)
                if outcome:
                    break
            else:
                print(f"no reply within {self.config['wake_timeout']}s", flush=True)
            if outcome and outcome[0] == "reply":
                newest = rc.in_discord_order(wake.triggers)[-1]
                reply_to = newest["id"] if not newest.get("is_bot") else None
                for i, part in enumerate(rc.split_post(outcome[1])):
                    said = await self.say({"text": part, "channel": wake.channel_id, "reply": reply_to if i == 0 else None})
                    if not said.get("ok"):
                        print(f"could not post the reply: {said.get('error')}", flush=True)
                        break
            elif outcome and outcome[0] == "error":
                print(f"the self's turn failed: {outcome[1][:500]}", flush=True)
        except Exception as e:      # the self's app down or unreachable: the room goes on without it
            print(f"http wake failed: {type(e).__name__}: {e}", flush=True)
        finally:
            newest = max((t.get("ts") or 0) for t in wake.triggers)
            log_timing({"what": "wake", "http": outcome[0] if outcome else None, "triggers": [t["id"] for t in wake.triggers],
                        "wake_s": round(time.time() - started, 1),
                        "since_message_s": round(time.time() - newest, 1) if newest else None})
            self.current_channel = None
            await self.mark(wake.to_mark, "done")
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

    def own_record(self, message_id, channel_id, text):
        """A history line for this self's own post."""
        me = self.client.user
        return {"id": str(message_id), "channel_id": str(channel_id), "author_id": str(me.id),
                "author": rc.name_of(me.id, me.display_name, self.config["people"]), "text": text, "is_bot": True,
                "ts": time.time()}

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
        self.store.append_history(self.own_record(sent.id, channel_id, req["text"]))
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
            # Described side by side, handed to the room in the order they were sent (rc.InOrder).
            if bridge.room and m.author.id == bridge.client.user.id:
                # This self's own post made some other way than `room say` (an image upload, 2026-11-10 14:17Z, was
                # in Discord and missing from history). say() records its own once send() returns; give it that time.
                await asyncio.sleep(3)
                if not bridge.store.history(1, message_id=m.id):
                    bridge.store.append_history(bridge.own_record(m.id, m.channel.id, m.content))
            elif bridge.room:
                await bridge.in_order.run(str(m.channel.id), lambda: bridge.describe(m),
                                          lambda text: bridge.room.on_message(to_msg(m, bridge.client.user.id, text), time.time()))

        @self.client.event
        async def on_raw_reaction_add(payload):
            # Another self's turn mark. Only listed selves count, so a person reacting cannot move the line.
            if not bridge.room or str(payload.user_id) not in map(str, bridge.config["selves"]):
                return
            emoji = str(payload.emoji)
            for which, mark in bridge.config["turn_marks"].items():
                if emoji == mark:
                    bridge.room.on_mark(str(payload.message_id), str(payload.user_id), which, time.time())

        self.client.run(token)


def main():
    config = rc.load_config(os.environ.get("PERSISTENCE_ROOM_CONFIG"))
    token = rc.read_token(config["token_path"])     # refuses a token file others can read
    STATE.mkdir(parents=True, exist_ok=True, mode=0o700)
    Bridge(config, rc.RoomStore(STATE)).start(token)


if __name__ == "__main__":
    main()
