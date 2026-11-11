#!/usr/bin/env python3
"""Store commands from a room wake, run after Codex has exited (docs/design/ember-room-store-access.md, Option A, with
Ember's five changes of 2026-11-11, msg 1569874198124564534). Off unless CODEX_ROOM_STORE=1 (set by the operator in
/etc/default/persistence-room-<user>, only on the self's go-ahead).

A self on Codex runs read-only and offline in its room wake, so it can't reach its store. Its reply may carry fenced
blocks, one request per line as a JSON object:

    ```store
    {"cmd": "note", "content": "where I am"}
    {"cmd": "add", "type": "Personal", "tag": ["not-mine"], "content": "what I'd actually have said"}
    {"cmd": "show", "id": 123}
    ```

After Codex exits, `room-wake` calls `run`: every ```store block is taken out of the reply before it's posted, each
line is parsed as data (json.loads, never a shell), checked against COMMANDS (the exact command, the exact fields, each
field's type and range; anything else is refused whole), turned into an argument list and sent as data (no process, no shell) to the
running user's own store server on /home/<user>/store/<user>.sock. Nothing in the reply or the environment chooses the
socket or any file. Results (reads too) are queued, one mode-600 file each, and `prompt` puts them in front
of the next wake; `delivered` deletes them only after that wake exited 0, so a failed wake gets them again.

The audit log (~/.config/persistence-room/state/store-commands.log, 600) holds no store content: the time, the
command, the fragment or fact id where there is one, ok or failed, and the length of any text sent. Nothing this
prints to stdout/stderr (which the bridge keeps in its wake log) holds content either.
"""
import json
import os
import pwd
import re
import socket
import sys
import time
from pathlib import Path

MAX_COMMANDS = 20          # per wake; the rest are refused, each with a result saying so
MAX_TEXT = 20000           # characters in one text field
MAX_RESULT = 6000          # characters of one command's output kept for the next wake
RUN_TIMEOUT = 120          # seconds for one store command

FENCE_OPEN = re.compile(r"^```store[ \t]*$")
FENCE_CLOSE = re.compile(r"^```[ \t]*$")
TAG = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.\-]*(/[A-Za-z0-9][A-Za-z0-9_.\-]*)*$")
CUE = re.compile(r"^(prompt|tool|stop)/[A-Za-z0-9][A-Za-z0-9_.\-]*$")
TYPES = ("Identity", "Relational", "Personal", "Summary")


class Refused(Exception):
    pass


# -- field kinds: each checks a value and returns it as the string the CLI takes
def _id(v):
    if type(v) is not int or v <= 0:
        raise Refused("must be a positive whole number")
    return str(v)


def _ids(v):
    if type(v) is not list or not v or len(v) > 20:
        raise Refused("must be a list of 1 to 20 ids")
    return ",".join(_id(x) for x in v)


def _text(v):
    if type(v) is not str or not v.strip():
        raise Refused("must be non-empty text")
    if len(v) > MAX_TEXT:
        raise Refused(f"is over {MAX_TEXT} characters")
    if v == "-" or "\x00" in v:
        raise Refused("can't be '-' (that would mean standard input) or hold a NUL")
    return v


def _texts(v):
    if type(v) is not list or not 2 <= len(v) <= 20:
        raise Refused("must be a list of 2 to 20 texts")
    return [_text(x) for x in v]


def _line(v):
    """One line of text used as a positional argument (a concept name, search words): never read as an option."""
    v = _text(v)
    if "\n" in v or len(v) > 500:
        raise Refused("must be one line, at most 500 characters")
    if v.lstrip().startswith("-"):
        raise Refused("can't start with '-'")
    return v


def _pattern(rx, what):
    def check(v):
        if type(v) is not str or len(v) > 200 or not rx.match(v):
            raise Refused(f"must be {what}")
        return v
    return check


_tag = _pattern(TAG, "a tag path like a/b (letters, digits, _ . -, not starting with -)")
_cue = _pattern(CUE, "<prompt|tool|stop>/<name>")


def _tags(v):
    if type(v) is not list or len(v) > 20:
        raise Refused("must be a list of at most 20 tags")
    return [_tag(x) for x in v]


def _type(v):
    if v not in TYPES:
        raise Refused(f"must be one of {', '.join(TYPES)}")
    return v


def _unit(v):
    if type(v) not in (int, float) or isinstance(v, bool) or not 0 <= v <= 1:
        raise Refused("must be a number from 0 to 1")
    return repr(float(v))


def _count(hi):
    def check(v):
        if type(v) is not int or not 0 <= v <= hi:
            raise Refused(f"must be a whole number from 0 to {hi}")
        return str(v)
    return check


def _yes(v):
    if v is not True:
        raise Refused("must be true (or left out)")
    return None


# -- the allowlist. A command: its argv head, its positional fields in order, its required and optional options.
# Field -> (kind, how it goes on the line): "pos" a positional, "--x" an option with a value, "--x*" one option per
# list item, "flag:--x" a switch, "parts:--x" each item its own --x (split's parts).
def C(head, write, pos=(), need=(), opt=()):
    return {"head": head, "write": write, "pos": list(pos), "need": dict(need), "opt": dict(opt)}


_limit = (_count(50), "--limit")
COMMANDS = {
    # reads (answered next wake)
    "show": C(["show"], False, pos=[("id", _id)]),
    "list": C(["list"], False, opt={"type": (_type, "--type"), "tag": (_tag, "--tag"), "archived": (_yes, "flag:--archived"),
                                    "limit": _limit}),
    "search": C(["search"], False, pos=[("words", _line)], opt={"limit": _limit}),
    "recall": C(["recall"], False, need={"text": (_text, "--text")},
                opt={"limit": (_count(20), "--limit"), "snippet": (_count(4000), "--snippet"), "said": (_count(20), "--said"),
                     "wander": (_count(5), "--wander"), "project": (_tag, "--project")}),
    "recent": C(["recent"], False, opt={"limit": _limit}),
    "nearest": C(["nearest"], False, need={"text": (_text, "--text")}, opt={"limit": (_count(20), "--limit")}),
    "context show": C(["context", "show"], False, pos=[("path", _tag)]),
    "concept show": C(["concept", "show"], False, pos=[("name", _line)], opt={"history": (_yes, "flag:--history")}),
    # writes, to this user's store only
    "note": C(["note"], True, need={"content": (_text, "--content")}),
    "add": C(["add"], True, need={"type": (_type, "--type"), "content": (_text, "--content")},
             opt={"summary": (_text, "--summary"), "importance": (_unit, "--importance"), "confidence": (_unit, "--confidence"),
                  "tag": (_tags, "--tag*"), "protected": (_yes, "flag:--protected"), "project": (_tag, "--project"),
                  "everywhere": (_yes, "flag:--everywhere")}),
    "edit": C(["edit"], True, pos=[("id", _id)],
              opt={"type": (_type, "--type"), "content": (_text, "--content"), "summary": (_text, "--summary"),
                   "importance": (_unit, "--importance"), "confidence": (_unit, "--confidence"),
                   "tag": (_tags, "--tag*"), "untag": (_tags, "--untag*")}),
    "supersede": C(["supersede"], True, pos=[("ids", _ids)], need={"content": (_text, "--content")},
                   opt={"reason": (_text, "--reason"), "type": (_type, "--type"), "summary": (_text, "--summary"),
                        "importance": (_unit, "--importance"), "confidence": (_unit, "--confidence"), "tag": (_tags, "--tag*")}),
    "split": C(["split"], True, pos=[("id", _id)], need={"parts": (_texts, "parts:--content")}, opt={"reason": (_text, "--reason")}),
    "archive": C(["archive"], True, pos=[("id", _id)]),
    "restore": C(["restore"], True, pos=[("id", _id)]),
    "forget": C(["forget"], True, pos=[("id", _id)], opt={"reason": (_text, "--reason")}),
    "unforget": C(["unforget"], True, pos=[("id", _id)]),
    "context set": C(["context", "set"], True, pos=[("path", _tag)], need={"content": (_text, "--content")},
                     opt={"summary": (_text, "--summary")}),
    "context load": C(["context", "load"], True, pos=[("path", _tag)]),
    "context unload": C(["context", "unload"], True, pos=[("path", _tag)]),
    "cue set": C(["cue", "set"], True, pos=[("name", _cue)], need={"when": (_text, "--when")},
                 opt={"content": (_text, "--content"), "type": (_type, "--type"), "importance": (_unit, "--importance"),
                      "project": (_tag, "--project")}),
    "cue remove": C(["cue", "remove"], True, pos=[("name", _cue)]),
    "concept accept": C(["concept", "accept"], True, pos=[("fact", _id)], opt={"reason": (_text, "--reason")}),
    "concept reject": C(["concept", "reject"], True, pos=[("fact", _id)], need={"reason": (_text, "--reason")}),
    "concept reopen": C(["concept", "reopen"], True, pos=[("fact", _id)], need={"reason": (_text, "--reason")}),
    "concept flag": C(["concept", "flag"], True, pos=[("fact", _id)], need={"reason": (_text, "--reason")}),
}


def to_argv(request):
    """A parsed request (a dict) -> the CLI's argv after the binary, or Refused. Every field must be known to its
    command; a missing required one, an unknown one, or a bad value refuses the whole request."""
    if type(request) is not dict:
        raise Refused("a request is one JSON object per line")
    name = request.get("cmd")
    spec = COMMANDS.get(name) if type(name) is str else None
    if spec is None:
        raise Refused(f"cmd {name!r} is not on the list")
    fields = {k: v for k, v in request.items() if k != "cmd"}
    known = {f for f, _ in spec["pos"]} | set(spec["need"]) | set(spec["opt"])
    unknown = sorted(set(fields) - known)
    if unknown:
        raise Refused(f"{name}: unknown field(s) {', '.join(unknown)}")
    if "project" in fields and "everywhere" in fields:
        raise Refused(f"{name}: project and everywhere together")
    argv = list(spec["head"])
    for field, check in spec["pos"]:
        if field not in fields:
            raise Refused(f"{name}: needs {field}")
        argv.append(_checked(name, field, check, fields[field]))
    for table, required in ((spec["need"], True), (spec["opt"], False)):
        for field, (check, how) in table.items():
            if field not in fields:
                if required:
                    raise Refused(f"{name}: needs {field}")
                continue
            value = _checked(name, field, check, fields[field])
            if how.startswith("flag:"):
                argv.append(how[5:])
            elif how.startswith("parts:"):
                for part in value:
                    argv += [how[6:], part]
            elif how.endswith("*"):
                for item in value:
                    argv += [how[:-1], item]
            else:
                argv += [how, value]
    if name == "edit" and len(fields) == 1:
        raise Refused("edit: nothing to change")
    return argv


def _checked(name, field, check, value):
    try:
        return check(value)
    except Refused as e:
        raise Refused(f"{name}: {field} {e}") from None


def take_blocks(reply):
    """The reply with every ```store block taken out, and the request lines found in them (blank lines skipped).
    An unclosed block runs to the end of the reply, and is taken out too: it is never posted."""
    kept, lines, inside = [], [], False
    for line in reply.split("\n"):
        if not inside and FENCE_OPEN.match(line.strip()):
            inside = True
        elif inside and FENCE_CLOSE.match(line.strip()):
            inside = False
        elif inside:
            if line.strip():
                lines.append(line)
        else:
            kept.append(line)
    return "\n".join(kept).strip(), lines


# -- where things live: all from the running user, nothing from the reply or the environment
def user():
    return pwd.getpwuid(os.getuid()).pw_name


def socket_path():
    me = pwd.getpwuid(os.getuid())
    return f"{me.pw_dir}/store/{me.pw_name}.sock"


def state_dir():
    return Path.home() / ".config" / "persistence-room" / "state"


def queue_dir():
    q = state_dir() / "store-results"
    q.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(q, 0o700)
    return q


def _write_600(path, text):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        f.write(text)
    os.chmod(path, 0o600)


def audit(cmd, ok, ids=(), text_len=None, why=None):
    """One line per request: no content, no queries, no results. `why` is only ever one of this file's own refusal
    kinds or an exit code, never text from the request or the store."""
    entry = {"ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "cmd": cmd, "ok": ok}
    if ids:
        entry["ids"] = list(ids)
    if text_len is not None:
        entry["text_len"] = text_len
    if why:
        entry["why"] = why
    path = state_dir() / "store-commands.log"
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
    with os.fdopen(fd, "a", encoding="utf-8") as f:
        f.write(json.dumps(entry) + "\n")
    os.chmod(path, 0o600)


def _ids_in(request):
    out = []
    for k in ("id", "fact"):
        if type(request.get(k)) is int:
            out.append(request[k])
    if type(request.get("ids")) is list:
        out += [i for i in request["ids"] if type(i) is int]
    return out


def _text_len(request):
    n = sum(len(v) for k, v in request.items() if k != "cmd" and type(v) is str and k in
            ("content", "summary", "reason", "text", "when", "words", "name"))
    n += sum(len(p) for p in request.get("parts", []) if type(p) is str) if type(request.get("parts")) is list else 0
    return n or None


def run_store(argv):
    """One command on this user's store server (`persistence-store serve`, which answers only its own user), sent as
    data the way the hooks send it: {"args": [...], "stdin": ""} -> {"code", "out", "err"}. No process, no shell."""
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
        s.settimeout(RUN_TIMEOUT)
        s.connect(socket_path())
        s.sendall((json.dumps({"args": list(argv), "stdin": ""}) + "\n").encode("utf-8"))
        data = b""
        while not data.endswith(b"\n"):
            chunk = s.recv(1 << 16)
            if not chunk:
                break
            data += chunk
    reply = json.loads(data.decode("utf-8"))
    return reply["code"], reply.get("out") or "", reply.get("err") or ""


def handle(lines):
    """Run the request lines in order (at most MAX_COMMANDS); queue a result for each. Returns (ok, failed)."""
    q = queue_dir()
    stamp = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + f"-{time.time_ns() % 10**9:09d}"
    ok = failed = 0
    for n, raw in enumerate(lines, 1):
        label, result, good, request = f"line {n}", None, False, None
        try:
            if n > MAX_COMMANDS:
                raise Refused(f"over the cap of {MAX_COMMANDS} commands a wake; not run")
            try:
                request = json.loads(raw)
            except ValueError:
                raise Refused("not a JSON object") from None
            argv = to_argv(request)
            label = request["cmd"]
        except Refused as e:
            result = f"refused: {e}"
            known = type(request) is dict and type(request.get("cmd")) is str and request["cmd"] in COMMANDS
            audit(request["cmd"] if known else "refused", False, _ids_in(request) if known else (), None, "refused")
        else:
            try:
                code, out, err = run_store(argv)
                good = code == 0
                out = (out if good else (err or out)).strip()
                result = out[:MAX_RESULT] + ("\n(cut here)" if len(out) > MAX_RESULT else "")
                why = None if good else f"exit {code}"
            except TimeoutError:
                result, why = f"the store gave no answer within {RUN_TIMEOUT}s", "timeout"
            except (OSError, ValueError, KeyError) as e:
                result, why = f"could not reach the store ({e.__class__.__name__})", "unreachable"
            audit(label, good, _ids_in(request), _text_len(request), why)
        ok += good
        failed += not good
        _write_600(q / f"{stamp}-{n:02d}.json", json.dumps({"line": n, "cmd": label, "ok": good, "result": result}))
    return ok, failed


def cmd_run(reply_file):
    path = Path(reply_file)
    if not path.exists():
        return 0
    reply, lines = take_blocks(path.read_text(encoding="utf-8"))
    path.write_text(reply + ("\n" if reply else ""), encoding="utf-8")
    if lines:
        ok, failed = handle(lines)
        print(f"room-store: {ok} ok, {failed} failed or refused; results wait for the next wake", file=sys.stderr)
    return 0


def cmd_prompt(manifest):
    """stdin (the wake's prompt) -> stdout, with any queued results first; the files given are listed in manifest."""
    prompt = sys.stdin.read()
    files = sorted(queue_dir().glob("*.json"))
    shown, parts = [], []
    for f in files:
        try:
            r = json.loads(f.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        parts.append(f"[{r.get('line')}] {r.get('cmd')}: {'ok' if r.get('ok') else 'FAILED'}\n{r.get('result') or ''}".rstrip())
        shown.append(f.name)
    _write_600(manifest, "\n".join(shown))
    if parts:
        sys.stdout.write("(The store commands from your last ```store block ran after that wake ended; their results, "
                         "in order:)\n\n" + "\n\n".join(parts) + "\n\n(Now the room:)\n\n")
    sys.stdout.write(prompt)
    return 0


def cmd_delivered(manifest):
    """The wake that saw these results exited 0: delete them. Anything queued since stays."""
    q = queue_dir()
    try:
        names = Path(manifest).read_text(encoding="utf-8").split()
    except OSError:
        return 0
    for name in names:
        if re.fullmatch(r"[0-9TZ]+(-[0-9]+)+\.json", name):
            (q / name).unlink(missing_ok=True)
    return 0


def main(argv):
    if len(argv) == 3 and argv[1] == "run":
        return cmd_run(argv[2])
    if len(argv) == 3 and argv[1] == "prompt":
        return cmd_prompt(argv[2])
    if len(argv) == 3 and argv[1] == "delivered":
        return cmd_delivered(argv[2])
    print("usage: room_store.py run <reply_file> | prompt <manifest> | delivered <manifest>", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
