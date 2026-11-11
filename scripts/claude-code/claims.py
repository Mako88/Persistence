"""Task claims: the claims core (docs/design/task-claims.md, revision 4, sha256 8440c4dd…6b33).

A claim is the hand-off's task record (`hook.TASK_FIELDS`) extended by `CLAIM_FIELDS`: one JSON file per claim,
`state/tasks/<uuid>.json`, written under `state/tasks/.lock` by write-then-rename, with one history entry per write.
A claim exists only because a session (`ptask`) or the harness made it; nothing here reads room text, a reply, a
note or a transcript for tasks, and no free text of a claim reaches argv, the environment or a file name.

This module is the core only: the record, its transitions and history, the locks, pause expiry, exact-id liveness,
pending-by-structure, the canonical and bound `cwd`, the `cwd-exec` launch, the one setting, and `ptask`. The
run-due spawn claim and everything the heartbeat does with claims are not wired here yet.

`PERSISTENCE_TASKCLAIMS` (settings.json only, off by default) gates nothing in this module: claims are recorded
whether it is on or off (section 5). Nothing in hook.py calls this module except `hook.py ptask`.

Standard library only, Linux for the descriptor binding."""
import ctypes
import fcntl
import json
import os
import stat
import subprocess
import sys
import threading
import uuid
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hook  # noqa: E402  TASK_FIELDS stays the single list of hand-off keys

TASK_FIELDS = hook.TASK_FIELDS
CLAIM_FIELDS = ("id", "dispatch", "status", "cwd", "created", "updated", "pause_until", "nudged", "reported", "history", "outcome")
KEYS = set(TASK_FIELDS) | set(CLAIM_FIELDS)
ENTRY_FIELDS = ("at", "action", "from_status", "to_status", "from_owner", "to_owner", "by", "firing")
STATUSES = ("open", "claimed", "paused", "done", "abandoned")
TERMINAL = ("done", "abandoned")
DISPATCHES = ("handoff", "none")
FIXED_ACTORS = ("harness", "pause-expiry", "heartbeat")
CWD_EXEC = Path(__file__).resolve().with_name("cwd_exec.py")


class Refused(Exception):
    """A claims write that the table, the lock or the record refuses. The file is left byte for byte."""


class LockOrderError(RuntimeError):
    """A lock taken against the one global order (section 4, "Locks")."""


def full_id(value):
    """A full UUID in its canonical text form, or None: never a prefix, never free text."""
    try:
        return str(uuid.UUID(str(value))) if value and str(uuid.UUID(str(value))) == str(value) else None
    except ValueError:
        return None


# Locks. Arden's sentence (task-claims.md section 4, heartbeat.md rev 3 "Lock order"): a path takes only the locks
# it needs, and whenever it holds more than one of state/heartbeat.lock, state/wakes.lock, state/tasks/.lock and
# state/fired.lock it takes them in that order, never waits for an earlier one while holding a later one, and takes
# no other lock while holding state/fired.lock; no path holds any of them while it launches a process or model or
# writes a report to the journal. The order is checked when each lock is taken, so a wrong path fails instead of
# deadlocking.
LOCK_ORDER = ("heartbeat", "wakes", "tasks", "fired")
LOCK_FILES = {"heartbeat": "heartbeat.lock", "wakes": "wakes.lock", "tasks": "tasks/.lock", "fired": "fired.lock"}
LOCK_OBSERVER = None  # tests: called with ("acquire" | "release", name)
_held = threading.local()


def held_locks():
    return list(getattr(_held, "names", ()))


@contextmanager
def lock(state, name):
    names = held_locks()
    if "fired" in names:
        raise LockOrderError(f"{name}.lock taken while holding fired.lock, which is a leaf")
    if names and LOCK_ORDER.index(name) <= LOCK_ORDER.index(names[-1]):
        raise LockOrderError(f"{name}.lock taken while holding {names[-1]}.lock: the order is {' -> '.join(LOCK_ORDER)}")
    path = Path(state) / LOCK_FILES[name]
    path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(str(path), os.O_RDWR | os.O_CREAT | os.O_CLOEXEC, 0o600)
    try:
        fcntl.flock(fd, fcntl.LOCK_EX)
        _held.names = names + [name]
        if LOCK_OBSERVER:
            LOCK_OBSERVER(("acquire", name))
        try:
            yield
        finally:
            _held.names = names
            if LOCK_OBSERVER:
                LOCK_OBSERVER(("release", name))
    finally:
        os.close(fd)  # closing the descriptor releases the flock


# Liveness by exact id (section 4): the owner is running when some process's argv has --session-id or --resume as
# one entry and the owner's full id as the next, as FragmentStore.ProcessHoldsSession reads it. The cases are one
# table, tests/cases/process-holds-session.json.
def holds_session(argv, session):
    return any(argv[i] in ("--session-id", "--resume") and argv[i + 1] == session for i in range(len(argv) - 1))


def process_table(proc="/proc"):
    """Every readable process's argv, or None when liveness can't be told (no /proc)."""
    if not os.path.isdir(proc):
        return None
    table = []
    for name in os.listdir(proc):
        if not name.isdigit():
            continue
        try:
            with open(os.path.join(proc, name, "cmdline"), "rb") as f:
                raw = f.read()
        except OSError:
            continue
        table.append(raw.decode("utf-8", "replace").split("\0")[:-1] if raw.endswith(b"\0") else raw.decode("utf-8", "replace").split("\0"))
    return table


def running(session, table):
    """True, False, or None when the process table is unknown."""
    if table is None:
        return None
    return any(holds_session(argv, session) for argv in table)


def pending_for(record, items):
    """A wake is pending for a claim only by structure: its item's `session` is the owner's full id, or its `claim` is
    the claim's id. No substring search over reasons."""
    return any((record.get("owner") and item.get("session") == record["owner"]) or item.get("claim") == record["id"]
               for item in items)


def valid_record(record):
    """Valid by key set, not order."""
    return isinstance(record, dict) and set(record) == KEYS


def enabled(home=None):
    """PERSISTENCE_TASKCLAIMS from this self's ~/.claude/settings.json env block only, never os.environ (hook.py's
    setting() prefers the environment, so a unit's Environment= could split it). On only when it is "1"."""
    try:
        env = json.loads((Path(home or Path.home()) / ".claude" / "settings.json").read_text(encoding="utf-8")).get("env") or {}
    except (OSError, ValueError, AttributeError):
        return False
    return str(env.get("PERSISTENCE_TASKCLAIMS", "")).strip() == "1" if isinstance(env, dict) else False


# The cwd (section 1): stored as the canonical real path computed at creation; at each use resolved beneath the
# self's home as a descriptor with no symlink followed at any depth, checked with fstat, and launched from that
# descriptor through cwd-exec. Any failure refuses that use: "no valid cwd". Nothing falls back to another path.
RESOLVE_NO_SYMLINKS, RESOLVE_BENEATH = 0x04, 0x08
SYS_OPENAT2 = 437  # the same number on every architecture since Linux 5.6
DIR_FLAGS = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC


class _OpenHow(ctypes.Structure):
    _fields_ = [("flags", ctypes.c_uint64), ("mode", ctypes.c_uint64), ("resolve", ctypes.c_uint64)]


try:
    _libc = ctypes.CDLL(None, use_errno=True)
    _libc.syscall.restype = ctypes.c_long
except OSError:
    _libc = None


def _openat2(dirfd, path):
    how = _OpenHow(DIR_FLAGS, 0, RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS)
    fd = _libc.syscall(ctypes.c_long(SYS_OPENAT2), ctypes.c_int(dirfd), ctypes.c_char_p(os.fsencode(path)),
                       ctypes.byref(how), ctypes.c_size_t(ctypes.sizeof(how)))
    if fd < 0:
        err = ctypes.get_errno()
        raise OSError(err, os.strerror(err), path)
    return fd


def _have_openat2():
    if _libc is None or not sys.platform.startswith("linux"):
        return False
    try:
        root = os.open("/", DIR_FLAGS)
    except OSError:
        return False
    try:
        os.close(_openat2(root, "."))
        return True
    except OSError:
        return False
    finally:
        os.close(root)


HAVE_OPENAT2 = _have_openat2()


def _openat_walk(dirfd, rel):
    """The fallback: component by component, each fd the parent of the next, O_NOFOLLOW at every depth; `..`, `.` and
    empty components refused."""
    if rel == ".":
        return os.dup(dirfd)
    parent = os.dup(dirfd)
    try:
        for name in rel.split("/"):
            if name in ("", ".", ".."):
                raise OSError(f"component {name!r} refused")
            child = os.open(name, DIR_FLAGS | os.O_NOFOLLOW, dir_fd=parent)
            os.close(parent)
            parent = child
        result, parent = parent, None
        return result
    finally:
        if parent is not None:
            os.close(parent)


def static_cwd_problem(path, home_real):
    """Why a cwd string fails "valid when" without touching the filesystem, or None."""
    if not isinstance(path, str) or not path:
        return "empty"
    if any(ord(ch) < 32 or ord(ch) == 127 for ch in path):
        return "control characters"
    if not os.path.isabs(path):
        return "not absolute"
    if os.path.normpath(path) != path:
        return "not normalised"
    if path != home_real and not path.startswith(home_real.rstrip("/") + "/"):
        return "outside home"
    return None


def launch(fd, argv, **popen):
    """Start argv in the directory `fd` through cwd-exec: only that descriptor crosses (pass_fds, close_fds), and
    cwd-exec closes it before exec. The harness's copy is closed here, whatever happens."""
    try:
        return subprocess.Popen([sys.executable, "-I", str(CWD_EXEC), str(fd), *argv], pass_fds=(fd,), close_fds=True, **popen)
    finally:
        os.close(fd)


def _parse_time(value):
    if isinstance(value, datetime):
        moment = value
    else:
        text = str(value).strip()
        moment = datetime.fromisoformat(text[:-1] + "+00:00" if text.endswith("Z") else text)
    if moment.tzinfo is None:
        raise ValueError("a time needs Z or an offset")
    return moment.astimezone(timezone.utc)


def spell_start(record):
    """The history sequence number that began the current spell: the last transition into `claimed` from another
    status, or the last take (heartbeat.md's report key uses it)."""
    for i in range(len(record["history"]) - 1, -1, -1):
        h = record["history"][i]
        if h["to_status"] == "claimed" and (h["from_status"] != "claimed" or h["action"] == "take"):
            return i
    return None


_UNSET = object()


class Claims:
    def __init__(self, state=None, home=None, now=None, wakes_path=None):
        self.state = Path(state or hook.STATE)
        self.home = Path(home or Path.home())
        self.home_real = os.path.realpath(str(self.home))
        self.now = now or (lambda: datetime.now(timezone.utc))
        self.dir = self.state / "tasks"
        self.closed = self.dir / "closed"
        self.wakes_path = Path(wakes_path) if wakes_path else self.state.parent / "wakes.json"

    def lock(self, name):
        return lock(self.state, name)

    def stamp(self):
        return self.now().astimezone(timezone.utc).isoformat(timespec="seconds")

    # Files.
    def _path(self, claim_id):
        if not full_id(claim_id):
            raise Refused(f"{claim_id!r} is not a claim id")
        return self.dir / f"{claim_id}.json"

    def get(self, claim_id):
        """A claim from tasks/ or closed/, read without the lock and without writing (`ptask show`)."""
        path = self._path(claim_id)
        for where in (path, self.closed / path.name):
            if where.exists():
                return self._load(where)
        raise Refused(f"no claim {claim_id}")

    def _load(self, path):
        try:
            record = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as e:
            raise Refused(f"claim file {path.name} can't be read: {e}")
        if not valid_record(record):
            raise Refused(f"claim file {path.name} doesn't have exactly the claim keys")
        return record

    def all(self):
        """Every claim in tasks/ (not closed/), oldest first."""
        out = []
        for path in sorted(self.dir.glob("*.json")) if self.dir.is_dir() else []:
            if full_id(path.stem):
                try:
                    out.append(self._load(path))
                except Refused:
                    continue
        return sorted(out, key=lambda r: r["created"] or "")

    def by_owner(self, session):
        """The non-terminal claim owned by this exact session id, if any (hand-off's task_file, by owner)."""
        return next((r for r in self.all() if r["owner"] == session and r["status"] not in TERMINAL), None)

    def _write(self, record):
        self.dir.mkdir(parents=True, exist_ok=True)
        path = self._path(record["id"])
        tmp = path.with_name(f".{record['id']}.{os.getpid()}.{threading.get_ident()}.tmp")
        with open(tmp, "w", encoding="utf-8") as f:
            f.write(json.dumps(record, indent=1))
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, path)

    def _append(self, record, action, to_status, by, to_owner=_UNSET, firing=None, **extra):
        at = self.stamp()
        entry = {"at": at, "action": action, "from_status": record["status"] if action not in ("create",) and record["history"] else None,
                 "to_status": to_status, "from_owner": record["owner"],
                 "to_owner": record["owner"] if to_owner is _UNSET else to_owner, "by": by, "firing": firing, **extra}
        record["history"].append(entry)
        record["status"], record["owner"], record["updated"] = to_status, entry["to_owner"], at
        return entry

    @staticmethod
    def _actor(by, fixed=()):
        if by in fixed or full_id(by):
            return by
        raise Refused(f"{by!r} is not a session's full id")

    # Pause expiry: the one executor, run first in every claims write, under tasks/.lock.
    def settle_pauses(self, now=None):
        with self.lock("tasks"):
            return self._settle_locked()

    def _settle_locked(self):
        settled, now = 0, self.now()
        for record in self.all():
            if record["status"] != "paused" or not record["pause_until"]:
                continue
            try:
                until = _parse_time(record["pause_until"])
            except ValueError:
                continue
            if until > now:
                continue
            self._append(record, "expire", "claimed", "pause-expiry", pause_until=record["pause_until"])
            record["pause_until"] = record["nudged"] = record["reported"] = None
            self._write(record)
            settled += 1
        return settled

    @contextmanager
    def _locked(self, claim_id):
        with self.lock("tasks"):
            self._settle_locked()
            yield self._load(self._path(claim_id)) if self._path(claim_id).exists() else self._missing(claim_id)

    def _missing(self, claim_id):
        if (self.closed / f"{claim_id}.json").exists():
            raise Refused(f"claim {claim_id} is closed")
        raise Refused(f"no claim {claim_id}")

    def _expect(self, record, action, *statuses):
        if record["status"] not in statuses:
            raise Refused(f"claim {record['id']} is {record['status']}; {action} needs {' or '.join(statuses)}")

    def _held_by(self, record):
        alive = running(record["owner"], process_table())
        state = "liveness unknown" if alive is None else ("which is running" if alive else "which is not running")
        return f"claim {record['id']} is {record['status']}, held by {record['owner']}, {state}"

    # Creation.
    def canonical_cwd(self, given):
        problem = static_cwd_problem(given, self.home_real) if isinstance(given, str) and given else "empty"
        if problem and problem != "outside home":
            raise Refused(f"no valid cwd: {problem}")
        real = os.path.realpath(given)
        problem = static_cwd_problem(real, self.home_real)
        if problem:
            raise Refused(f"no valid cwd: {problem}")
        os.close(self.open_cwd(real))
        return real

    def create(self, task, owner, cwd, by, dispatch="none", status="claimed", origin_session=None,
               room_channel=None, room_from=None, room_upto=None):
        if dispatch not in DISPATCHES or status not in ("open", "claimed"):
            raise Refused(f"a claim is created open or claimed, dispatch handoff or none")
        if status == "claimed" and not full_id(owner):
            raise Refused(f"{owner!r} is not a session's full id")
        if status == "open" and owner is not None:
            raise Refused("an open claim has no owner")
        self._actor(by, ("harness",))
        record = dict.fromkeys(TASK_FIELDS)
        record.update(task=str(task), origin_session=origin_session, room_channel=room_channel, room_from=room_from,
                      room_upto=room_upto, owner=None)
        record.update(id=str(uuid.uuid4()), dispatch=dispatch, status=None, cwd=self.canonical_cwd(cwd),
                      created=self.stamp(), updated=None, pause_until=None, nudged=None, reported=None, history=[], outcome=None)
        with self.lock("tasks"):
            self._settle_locked()
            self._append(record, "claim" if status == "claimed" else "create", status, by, to_owner=owner if status == "claimed" else None)
            self._write(record)
        return record

    def claim(self, task, session, cwd):
        """`ptask claim "<task>"`: a claimed record, dispatch none, owned by the calling session."""
        if not full_id(session):
            raise Refused("ptask claim needs this session's full id (CLAUDE_CODE_SESSION_ID)")
        return self.create(task, session, cwd, by=session, origin_session=session)

    # Transitions (section 1). Anything else is refused, under the lock.
    def claim_open(self, claim_id, owner, by=None, firing=None):
        by = by or owner
        if not full_id(owner):
            raise Refused(f"{owner!r} is not a session's full id")
        self._actor(by, ("harness",))
        with self._locked(claim_id) as record:
            if record["status"] != "open":
                raise Refused(self._held_by(record) if record["owner"] else f"claim {claim_id} is {record['status']}; claim needs open")
            self._append(record, "claim", "claimed", by, to_owner=owner, firing=firing)
            record["nudged"] = record["reported"] = None
            self._write(record)
            return record

    def pause(self, claim_id, by, until=None, why=""):
        self._actor(by)
        until_text = None
        if until is not None:
            try:
                until_text = _parse_time(until).isoformat(timespec="seconds")
            except ValueError as e:
                raise Refused(f"--until: {e}")
        with self._locked(claim_id) as record:
            self._expect(record, "pause", "claimed")
            self._append(record, "pause", "paused", by, why=str(why or ""))
            record["pause_until"] = until_text
            self._write(record)
            return record

    def resume(self, claim_id, by):
        self._actor(by)
        with self._locked(claim_id) as record:
            self._expect(record, "resume", "paused")
            self._append(record, "resume", "claimed", by)
            record["pause_until"] = record["nudged"] = record["reported"] = None
            self._write(record)
            return record

    def touch(self, claim_id, by):
        """A history entry, not a transition: not a new spell, so nudged and reported stay (Arden)."""
        self._actor(by)
        with self._locked(claim_id) as record:
            self._expect(record, "touch", "claimed", "paused")
            self._append(record, "touch", record["status"], by)
            self._write(record)
            return record

    def done(self, claim_id, by, outcome=""):
        return self._close(claim_id, by, "done", outcome, ("claimed", "paused"))

    def abandon(self, claim_id, by, outcome=""):
        return self._close(claim_id, by, "abandoned", outcome, ("open", "claimed", "paused"))

    def _close(self, claim_id, by, to, outcome, allowed):
        self._actor(by)
        with self._locked(claim_id) as record:
            self._expect(record, "done" if to == "done" else "abandon", *allowed)
            self._append(record, "done" if to == "done" else "abandon", to, by)
            record["outcome"], record["pause_until"] = str(outcome or ""), None
            self._write(record)
            return record

    def take(self, claim_id, taker, table=_UNSET, items=_UNSET, expected_owner=None, resolver=None):
        """claimed -> claimed with a new owner, only if the owner holds no process by exact id, no wake is pending for
        the owner or the claim id, and the taker names itself. `expected_owner` makes it a compare-and-swap: of two
        takers who saw the same owner, one wins. The cwd is kept, and bound to a descriptor first."""
        if not full_id(taker):
            raise Refused("a take names its taker: this session's full id (CLAUDE_CODE_SESSION_ID)")
        with self.lock("wakes"):
            if items is _UNSET:
                items = self._read_wakes()
            with self._locked(claim_id) as record:
                self._expect(record, "take", "claimed")
                if expected_owner is not None and record["owner"] != expected_owner:
                    raise Refused(self._held_by(record) + f"; it changed hands since you looked (was {expected_owner})")
                alive = running(record["owner"], process_table() if table is _UNSET else table)
                if alive is None:
                    raise Refused(f"claim {claim_id}: can't tell whether its owner {record['owner']} is running")
                if alive:
                    raise Refused(f"claim {claim_id} is held by {record['owner']}, which is running")
                if pending_for(record, items):
                    raise Refused(f"claim {claim_id}: a wake is pending for its owner {record['owner']} or for the claim")
                os.close(self.open_cwd(record["cwd"], resolver=resolver))
                self._append(record, "take", "claimed", taker, to_owner=taker)
                record["nudged"] = record["reported"] = None
                self._write(record)
                return record

    def _read_wakes(self):
        try:
            items = json.loads(self.wakes_path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return []
        except (OSError, ValueError) as e:
            raise Refused(f"the wake queue can't be read, so a pending wake can't be ruled out: {e}")
        return items if isinstance(items, list) else []

    # The cwd at use.
    def open_cwd(self, path, resolver=None):
        """The stored path, resolved beneath home as a verified directory descriptor (O_CLOEXEC), or Refused."""
        problem = static_cwd_problem(path, self.home_real)
        if problem:
            raise Refused(f"no valid cwd: {problem}")
        rel = os.path.relpath(path, self.home_real)
        use_openat2 = HAVE_OPENAT2 if resolver is None else resolver == "openat2"
        if use_openat2 and not HAVE_OPENAT2:
            raise Refused("no valid cwd: openat2 unavailable")
        try:
            home = os.open(self.home_real, DIR_FLAGS)
        except OSError as e:
            raise Refused(f"no valid cwd: home can't be opened ({e.strerror})")
        try:
            fd = _openat2(home, rel) if use_openat2 else _openat_walk(home, rel)
        except OSError as e:
            raise Refused(f"no valid cwd: {e.strerror or e}")
        finally:
            os.close(home)
        st = os.fstat(fd)
        if not stat.S_ISDIR(st.st_mode) or st.st_uid != os.geteuid():
            os.close(fd)
            raise Refused("no valid cwd: not a directory owned by this self")
        return fd


# ptask: the session's own command. `claim` and `take` are refused in a room session (section 2.2).
USAGE = """ptask claim [--cwd <dir>] "<task>"     claim a task for this session (not from a room session: use pwake)
ptask list                               this self's claims not yet closed
ptask show <id>                          one claim, whole
ptask pause <id> [--until <UTC time>] "<why>"
ptask resume <id> | touch <id>
ptask done <id> ["<outcome>"] | abandon <id> ["<outcome>"]
ptask take <id>                          take a claim whose owner isn't running and has no wake pending"""


def _describe(record, now):
    status = record["status"]
    if status == "paused" and record["pause_until"]:
        passed = _parse_time(record["pause_until"]) <= now
        status = f"paused (until {record['pause_until']}{', passed' if passed else ''})"
    owner = (record["owner"] or "")[:8] or "unowned"
    return f"{record['id']}  {status}  owner {owner}  updated {record['updated']}"


def cli(args, env=None, state=None, home=None, cwd=None, out=print, err=None, now=None):
    env = os.environ if env is None else env
    err = err or (lambda text: print(text, file=sys.stderr))
    c = Claims(state, home=home, now=now)
    session = env.get("CLAUDE_CODE_SESSION_ID", "")
    room = env.get("PERSISTENCE_ROOM_CHANNEL")
    if not args or args[0] in ("-h", "--help", "help"):
        out(USAGE)
        return 0 if args else 2
    verb, rest = args[0], list(args[1:])
    try:
        if verb in ("claim", "take") and room:
            raise Refused(f"a room session can't own a claim (its id is resumed by the bridge); use pwake, which "
                          f"starts a working session that the harness claims for")
        if verb == "claim":
            where = cwd or os.getcwd()
            if rest[:1] == ["--cwd"] and len(rest) > 1:
                where, rest = rest[1], rest[2:]
            task = " ".join(rest).strip()
            if not task:
                raise Refused('ptask claim needs the task: ptask claim "<task>"')
            record = c.claim(task, session, where)
            out(f"claimed {record['id']} in {record['cwd']}")
        elif verb == "list":
            moment = c.now()
            lines = [_describe(r, moment) for r in c.all() if r["status"] not in TERMINAL]
            out("\n".join(lines) if lines else "(no claims open)")
        elif verb == "show" and len(rest) == 1:
            record = c.get(rest[0])
            out(_describe(record, c.now()))
            out(json.dumps(record, indent=1, ensure_ascii=False))
        elif verb == "pause" and rest:
            claim_id, rest = rest[0], rest[1:]
            until = None
            if rest[:1] == ["--until"] and len(rest) > 1:
                until, rest = rest[1], rest[2:]
            record = c.pause(claim_id, session, until=until, why=" ".join(rest))
            out(f"paused {record['id']}" + (f" until {record['pause_until']}" if record["pause_until"] else ""))
        elif verb in ("resume", "touch") and len(rest) == 1:
            record = getattr(c, verb)(rest[0], session)
            out(f"{verb}d {record['id']}: {record['status']}" if verb == "resume" else f"touched {record['id']}")
        elif verb in ("done", "abandon") and rest:
            record = getattr(c, verb)(rest[0], session, outcome=" ".join(rest[1:]))
            out(f"{record['status']} {record['id']}")
        elif verb == "take" and len(rest) == 1:
            seen = c.get(rest[0])
            record = c.take(rest[0], session, expected_owner=seen["owner"])
            out(f"took {record['id']} from {seen['owner']}")
        else:
            err(USAGE)
            return 2
    except Refused as e:
        err(f"ptask {verb}: {e}")
        return 1
    return 0


def main(argv=None):
    return cli(sys.argv[1:] if argv is None else argv)


if __name__ == "__main__":
    sys.exit(main())
