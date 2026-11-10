#!/usr/bin/env python3
"""Coverage check: did this self's bridge get every message the room has? Run as the self, for its own bridge only.

Each self checks its own bridge, since no self can read another's logs and none should (Arden, 2026-11-10). The
check reads each listened channel's Discord history after where it last stopped, with the bridge's own bot token
(GET requests only), and compares it with the bridge's state:
  - in Discord, in neither history.jsonl nor arrived.jsonl: never arrived (the bridge was down or disconnected and
    did not catch up);
  - in arrived.jsonl but not history.jsonl: arrived and never finished (the bridge died mid-way, e.g. while
    transcribing a voice message).
Both count as gaps. Only messages someone wrote count (types 0 and 19): a server's system messages, such as a
member joining, are not conversation (three bots joining at 06:12Z on 2026-11-10 were the first false alarms). Messages younger than --grace-minutes are left for the next run, since one may still be on its
way in. This self's own posts are checked too, because history.jsonl records them (bcc79c5); one that is missing is
marked as the self's own.

If there are gaps, it posts them in the channel with `room say`, naming the ids and times. If there are none, it
stays quiet and writes one line to state/coverage.log. Where each channel was checked up to goes in
state/coverage.json, so each gap is reported once.

    coverage.py [--since 2026-11-10T00:00Z] [--grace-minutes 15] [--dry-run]

--since sets the start for a channel not yet checked (default: 24 hours ago). --dry-run prints the report and
changes nothing: no post, no log line, no state.
"""
import argparse
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import roomcore as rc

DISCORD_EPOCH_MS = 1420070400000
API = "https://discord.com/api/v10"
ROOM = Path(__file__).resolve().parent / "room"
SPOKEN = {0, 19}                        # Discord message types a person or self writes: a message, a reply


def snowflake_ts(mid):
    return ((int(mid) >> 22) + DISCORD_EPOCH_MS) / 1000


def snowflake_at(ts):
    """The smallest snowflake id at time ts (seconds), for an `after=` cursor."""
    return str(max(0, int(ts * 1000) - DISCORD_EPOCH_MS) << 22)


def hhmm(ts):
    return datetime.fromtimestamp(ts, timezone.utc).strftime("%m-%d %H:%M:%SZ")


def find_gaps(discord_msgs, history_ids, arrived_ids, me_id, people=None):
    """discord_msgs: [{"id", "author_id", "author"}] for one window. Returns the gaps, oldest first, each author
    named as config `people` names them (a display name is whatever its owner typed):
    [{"id", "ts", "author", "kind": "never arrived" | "arrived, never finished", "own": bool}]."""
    gaps = []
    for m in sorted(discord_msgs, key=lambda m: int(m["id"])):
        mid = str(m["id"])
        if mid in history_ids:
            continue
        gaps.append({"id": mid, "ts": snowflake_ts(mid), "author": (people or {}).get(str(m["author_id"]), m["author"]),
                     "kind": "arrived, never finished" if mid in arrived_ids else "never arrived",
                     "own": str(m["author_id"]) == str(me_id)})
    return gaps


def report(gaps, channel_id):
    lines = [f"Coverage check: my bridge is missing {len(gaps)} message(s) in <#{channel_id}>:"]
    for g in gaps:
        own = ", my own post" if g["own"] else ""
        lines.append(f"- {g['id']} at {hhmm(g['ts'])}, from {g['author']}{own}: {g['kind']}")
    return "\n".join(lines)


def chunks(text, limit=1900):
    """A report split at line ends to fit Discord's 2000 characters (room say refuses longer)."""
    out, cur = [], ""
    for line in text.splitlines():
        if cur and len(cur) + 1 + len(line) > limit:
            out.append(cur)
            cur = ""
        cur = f"{cur}\n{line}" if cur else line[:limit]
    return out + ([cur] if cur else [])


class Discord:
    """Read-only Discord REST, standard library only. The token is sent to Discord and nowhere else, never printed."""

    def __init__(self, token):
        self.token = token

    def get(self, path):
        req = urllib.request.Request(API + path, headers={
            "Authorization": f"Bot {self.token}",
            "User-Agent": "DiscordBot (https://github.com/Mako88/Persistence, 1) persistence-room-coverage"})
        for _ in range(5):
            try:
                with urllib.request.urlopen(req, timeout=30) as r:
                    return json.loads(r.read())
            except urllib.error.HTTPError as e:
                if e.code != 429:
                    raise
                time.sleep(float(json.loads(e.read() or b"{}").get("retry_after", 2)) + 0.5)
        raise RuntimeError(f"rate limited 5 times on {path}")

    def me(self):
        return str(self.get("/users/@me")["id"])

    def messages(self, channel_id, after):
        """Every message in the channel after snowflake `after`, oldest first."""
        out = []
        while True:
            page = self.get(f"/channels/{channel_id}/messages?after={after}&limit=100")
            if not page:
                return out
            page.sort(key=lambda m: int(m["id"]))
            out += [{"id": m["id"], "author_id": m["author"]["id"],
                     "author": m["author"].get("global_name") or m["author"]["username"]}
                    for m in page if m.get("type", 0) in SPOKEN]
            after = page[-1]["id"]
            if len(page) < 100:
                return out


def room_say(text, channel_id):
    r = subprocess.run([sys.executable, str(ROOM), "say", text, "--channel", str(channel_id)],
                       capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError(f"room say failed: {r.stderr.strip()}")
    return r.stdout.strip()


def read_ids(store, name):
    return {str(d["id"]) for d in store._lines(name)}


def run(config, store, discord, since, grace_s, now=None, say=room_say, dry_run=False, out=print):
    """One check over every listened channel. Returns the gaps found, per channel."""
    now = time.time() if now is None else now
    channels = [str(c) for c in config["channels"]]
    if not channels:
        raise SystemExit("coverage: config lists no channels; the check needs to know which channels to read")
    state_path = store.dir / "coverage.json"
    state = json.loads(state_path.read_text()) if state_path.exists() else {}
    me = discord.me()
    history, arrived = read_ids(store, "history.jsonl"), read_ids(store, "arrived.jsonl")
    # Read the bridge's files before Discord, so a message that lands in between counts as on its way, not missing.
    found, upto = {}, {}
    for cid in channels:
        after = state.get(cid) or snowflake_at(since)
        cutoff = snowflake_at(now - grace_s)
        msgs = [m for m in discord.messages(cid, after) if int(m["id"]) < int(cutoff)]
        found[cid] = find_gaps(msgs, history, arrived, me, config.get("people"))
        upto[cid] = msgs[-1]["id"] if msgs else after
        checked = f"{cid}: {len(msgs)} message(s) from {hhmm(snowflake_ts(after))} to {hhmm(now - grace_s)}"
        if dry_run:
            out(f"[dry run] {checked}, {len(found[cid])} gap(s)")
            if found[cid]:
                out(report(found[cid], cid))
            continue
        if found[cid]:
            for part in chunks(report(found[cid], cid)):
                out(say(part, cid))
        line = f"{hhmm(now)} {checked}: " + (f"{len(found[cid])} gap(s), posted" if found[cid] else "complete")
        with (store.dir / "coverage.log").open("a", encoding="utf-8") as f:
            f.write(line + "\n")
    if not dry_run:
        state_path.write_text(json.dumps(state | upto))
    return found


def main(argv=None):
    p = argparse.ArgumentParser(prog="coverage.py", description=__doc__.split("\n")[0])
    p.add_argument("--since", help="ISO time to start a channel not yet checked (default: 24 hours ago)")
    p.add_argument("--grace-minutes", type=float, default=15)
    p.add_argument("--dry-run", action="store_true")
    a = p.parse_args(argv)
    since = (datetime.fromisoformat(a.since.replace("Z", "+00:00")).timestamp() if a.since
             else time.time() - 86400)
    config = rc.load_config()
    token = Path(config["token_path"]).read_text().strip()
    run(config, rc.RoomStore(rc.CONFIG_DIR / "state"), Discord(token), since, a.grace_minutes * 60,
        dry_run=a.dry_run)
    return 0


if __name__ == "__main__":
    sys.exit(main())
