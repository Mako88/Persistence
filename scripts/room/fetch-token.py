#!/usr/bin/env python3
"""fetch-token.py <one-time secret link> [dest]: a bot token from a onetimesecret.com or 1ty.me link, straight into its file.

The token never passes through a session: anything a Claude Code session prints or runs with `!` is kept in its
transcript and copied into the store. This writes the secret to `dest` (default ~/.config/persistence-room/token)
with mode 600 and prints only its length and shape. Opening the link burns it, so a second fetch finds nothing.
Standard library only.
"""
import json
import os
import re
import sys
import urllib.parse
import urllib.request
from pathlib import Path


def main(argv):
    if not argv:
        print(__doc__.strip().splitlines()[0], file=sys.stderr)
        return 2
    link = urllib.parse.urlparse(argv[0])
    dest = Path(argv[1]).expanduser() if len(argv) > 1 else Path.home() / ".config" / "persistence-room" / "token"
    if dest.exists():
        print(f"{dest} exists already; move it aside first (a fetch burns the link)", file=sys.stderr)
        return 1
    if link.scheme == "https" and link.netloc == "1ty.me":
        dest.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        DEST_PAGE[0] = dest.with_name(dest.name + ".page")
        value = from_1ty(argv[0])
        if value is None:
            return 1
        return write(dest, value)
    found = re.search(r"/secret/([A-Za-z0-9]+)", link.path)
    if link.scheme != "https" or not found:
        print("not a onetimesecret (https://<host>/secret/<key>) or 1ty.me link", file=sys.stderr)
        return 2
    # The apex domain's API answers 405 (measured 2026-11-10) and a secret lives in one region: try the link's own
    # host, then each region. A region that does not hold the secret answers 404 and burns nothing.
    hosts = [link.netloc] + [f"{r}.onetimesecret.com" for r in ("us", "eu", "ca", "uk", "nz") if f"{r}.onetimesecret.com" != link.netloc]
    value, errors = None, []
    for host in hosts:
        api = f"https://{host}/api/v1/secret/{found.group(1)}"
        try:
            with urllib.request.urlopen(urllib.request.Request(api, data=b"", method="POST"), timeout=20) as reply:
                value = json.loads(reply.read().decode("utf-8")).get("value")
            break
        except Exception as e:  # report the failure, never the body
            errors.append(f"{host}: {getattr(e, 'code', type(e).__name__)}")
    if value is None:
        print(f"fetch failed ({'; '.join(errors)}): the link may be used up or mistyped", file=sys.stderr)
        return 1
    if not value or not value.strip():
        print("the link held nothing (already opened?)", file=sys.stderr)
        return 1
    return write(dest, value.strip())


TOKEN = re.compile(r"[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{20,}")  # a Discord bot token's shape


def from_1ty(url):
    """1ty.me: a GET shows only a "View Note" form and burns nothing; a POST to the same URL returns the note inside
    the page (Arden, 2026-11-10). The page is never printed: only the one token-shaped string is taken from it."""
    try:
        with urllib.request.urlopen(urllib.request.Request(url, data=b"", method="POST"), timeout=20) as reply:
            page = reply.read().decode("utf-8", "replace")
    except Exception as e:
        print(f"fetch failed: {getattr(e, 'code', type(e).__name__)}", file=sys.stderr)
        return None
    found = set(TOKEN.findall(page))
    if len(found) != 1:
        # The POST burned the link: keep the page (600, never printed) rather than lose the note with it.
        kept = DEST_PAGE[0]
        fd = os.open(kept, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(page)
        print(f"the page held {len(found)} token-shaped strings, not one; the page is kept at {kept} (600) to read by hand", file=sys.stderr)
        return None
    return found.pop()


DEST_PAGE = [None]  # where a page whose token could not be picked out is kept: beside the token file


def write(dest, value):
    dest.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    fd = os.open(dest, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        f.write(value + "\n")
    parts = value.split(".")
    shape = "3 dot-separated parts, as a Discord bot token has" if len(parts) == 3 else f"{len(parts)} part(s): not a Discord bot token's shape"
    print(f"wrote {dest} (mode 600): {len(value)} characters, {shape}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
