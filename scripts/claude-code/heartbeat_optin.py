"""The heartbeat opt-in preflight: is this self ready to turn its heartbeat on? Read-only.

    python3 heartbeat_optin.py check --at <commit>              ready for PERSISTENCE_HEARTBEAT (B, C, A, D)?
    python3 heartbeat_optin.py check --stalled --at <commit>    ready for PERSISTENCE_HEARTBEAT_STALLED too?

Each self opts in for itself (heartbeat.md, "Consent and the way back"); this script only says what is and isn't in
place, so turning a switch on is never a guess. It reads the self's ~/.claude/settings.json env block (never os.environ,
as the heartbeat does), the deployed modules beside the hook, PATH, `systemctl show`/`is-enabled`, and the checkout's
git objects (`git rev-parse`, `git show`). It writes nothing, starts nothing and changes no setting. Exit 0 means ready;
1 lists what's missing.

The deployed modules are compared with a commit, never the working files: --at names the reviewed one and is required
(no fallback to HEAD, which would certify whatever happens to be checked out), and the ready line names the full hash,
so an unreviewed edit deployed the same way can't pass for reviewed. The rules it checks against (heartbeat's UNITS,
enabled, its settings reader, stalled_on) come from that commit too: heartbeat.py and the modules it imports are
loaded from the commit's blobs in memory, never from the working tree, so a dirty or different checkout can't change
the verdict.

It also refuses "ready" while a switch already on would fail silently, whichever level is asked: STALLED on without
`heartbeat` in PERSISTENCE_OBSERVE_SOURCES (its R and Q wakes would fire, never render and go undeliverable;
heartbeat-core-interface.md section 1), STALLED on with the heartbeat itself off, or STALLED on or asked for while the
commit's heartbeat.py has no STALLED code (no `stalled_on`), when the switch would turn nothing on."""
import importlib.abc
import importlib.util
import os
import pwd
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

# What deploy-remote.sh installs beside the hook, all of which the beat loads from ~/.claude/persistence: heartbeat.py,
# the modules it imports (directly or through claims and hook), run_line.py (wrapper_alive imports it) and cwd_exec.py
# (claims spawns through it). An old copy of any of them runs the new heartbeat against old rules. A test holds this
# equal to deploy-remote.sh's list.
DEPLOYED = ("heartbeat.py", "coordination.py", "claims.py", "cwd_exec.py", "run_line.py", "hook.py", "observed.py")


@dataclass
class Item:
    name: str
    ok: bool
    detail: str
    required: bool = True


def _on(value):
    return str(value or "").strip().lower() in ("true", "1")


def _git(repo, *args):
    """Read-only git in the checkout: rev-parse and show only. None when git says no."""
    r = subprocess.run(["git", "-C", str(repo), *args], capture_output=True)
    return r.stdout if r.returncode == 0 else None


class _Commit(importlib.abc.MetaPathFinder, importlib.abc.Loader):
    """Serves the DEPLOYED modules from a commit's blobs, in memory: nothing is written and no working file is read."""

    def __init__(self, sha, blobs):
        self.sha, self.sources = sha, {n[:-3]: b for n, b in blobs.items() if b is not None}

    def find_spec(self, name, path=None, target=None):
        if name in self.sources:
            return importlib.util.spec_from_loader(name, self, origin=f"{self.sha}:{name}.py")

    def create_module(self, spec):
        return None

    def exec_module(self, module):
        # heartbeat.py puts its own directory on sys.path; a directory that can't exist keeps that harmless.
        module.__file__ = f"/nonexistent/at-{self.sha}/{module.__name__}.py"
        exec(compile(self.sources[module.__name__], module.__file__, "exec"), module.__dict__)


def _core(sha, blobs):
    """(heartbeat as it is at sha, None) or (None, why). sys.modules and sys.path are left as they were: whatever
    `heartbeat` this process already holds is neither used nor replaced."""
    if sha is None or blobs.get("heartbeat.py") is None:
        return None, f"no heartbeat.py at {sha or 'the commit'} to load"
    finder = _Commit(sha, blobs)
    held = {n: sys.modules.pop(n) for n in list(sys.modules) if n in finder.sources}
    path = list(sys.path)
    sys.meta_path.insert(0, finder)
    try:
        return importlib.import_module("heartbeat"), None
    except BaseException as e:  # a core that won't load (even one that exits) is a finding, not a crash
        return None, f"heartbeat.py at {sha} does not load: {e.__class__.__name__}: {e}"
    finally:
        sys.meta_path.remove(finder)
        sys.path[:] = path
        for n in finder.sources:
            sys.modules.pop(n, None)
        sys.modules.update(held)


def _loaded(systemctl, unit):
    try:
        _, out = systemctl(["systemctl", "show", "-p", "LoadState", "--value", "--", unit])
    except Exception as e:  # unknown is not loaded: the preflight never says ready on a guess
        return False, f"unreadable ({e.__class__.__name__})"
    state = (out or "").strip()
    return state == "loaded", state or "empty"


def check(home=None, user=None, repo=None, systemctl=None, path=None, stalled=False, *, at):
    home = Path(home or Path.home())
    user = user or pwd.getpwuid(os.geteuid()).pw_name
    repo = Path(repo or Path(__file__).resolve().parent)
    items = []

    rev = at  # required: the reviewed commit, never a default
    sha = _git(repo, "rev-parse", "--verify", "--quiet", f"{rev}^{{commit}}")
    sha = sha.decode().strip() if sha else None
    items.append(Item("commit", bool(sha), sha or f"{rev} is not a commit in {repo}"))
    blobs = {name: _git(repo, "show", f"{sha}:./{name}") if sha else None for name in DEPLOYED}
    for name in DEPLOYED:
        deployed = home / ".claude" / "persistence" / name
        if not deployed.is_file():
            items.append(Item(f"deployed {name}", False, f"{deployed} is missing"))
        elif blobs[name] is None:
            items.append(Item(f"deployed {name}", False, f"no {name} at {sha or rev} to compare with"))
        elif deployed.read_bytes() != blobs[name]:
            items.append(Item(f"deployed {name}", False, f"{deployed} differs from {name} at {sha}"))
        else:
            items.append(Item(f"deployed {name}", True, f"matches {sha}"))

    heartbeat, why = _core(sha, blobs)
    items.append(Item("core", heartbeat is not None, why or f"heartbeat.py loaded from {sha}"))
    if heartbeat is None:  # every rule below is the commit's; without it there is nothing to check against
        return items
    systemctl = systemctl or heartbeat.systemctl_run
    def env(name):  # the beat's own reader: settings.json only
        return heartbeat._setting(home, name, "")

    units = heartbeat.units_for(user)
    if not units:
        items.append(Item("units", False, f"no tuple for {user} in heartbeat.UNITS: check D would watch nothing"))
    else:
        bad = [f"{u} ({state})" for u in units for ok, state in [_loaded(systemctl, u)] if not ok]
        items.append(Item("units", not bad, f"not installed: {', '.join(bad)}" if bad else f"all {len(units)} loaded"))

    timer = f"persistence-heartbeat@{user}.timer"
    ok, state = _loaded(systemctl, timer)
    items.append(Item("timer", ok, f"{timer} {state}" + ("" if ok else ": install the unit files first")))
    try:
        code, out = systemctl(["systemctl", "is-enabled", "--", timer])
        enabled = (out or "").strip() or f"exit {code}"
    except Exception as e:
        enabled = f"unreadable ({e.__class__.__name__})"
    items.append(Item("timer enabled", True, f"{timer} {enabled} (enabling it is the last step)", required=False))

    # Unset is a choice the design allows (a heartbeat with claims off), so only a note. Set to anything but "1" looks on
    # and isn't: claims.py reads only "1", so row A would be silently off.
    claims = env("PERSISTENCE_TASKCLAIMS").strip()
    if claims == "1":
        items.append(Item("PERSISTENCE_TASKCLAIMS", True, "1: row A on"))
    elif not claims:
        items.append(Item("PERSISTENCE_TASKCLAIMS", False, "unset: row A (claim nudges and retries) stays off",
                          required=False))
    else:
        items.append(Item("PERSISTENCE_TASKCLAIMS", False,
                          f"{claims!r}: looks on, but claims.py reads only \"1\", so row A stays off"))

    # One STALLED parser once the core has it (heartbeat-core-interface.md); until then the same rule, read here.
    parse = getattr(heartbeat, "stalled_on", None)
    hb_on = heartbeat.enabled(home)
    stalled_on = bool(parse(home)) if parse else _on(env("PERSISTENCE_HEARTBEAT_STALLED"))
    items.append(Item("PERSISTENCE_HEARTBEAT", True, "on" if hb_on else "off (turning it on is the step this checks for)",
                      required=False))
    if stalled_on and not hb_on:
        items.append(Item("PERSISTENCE_HEARTBEAT_STALLED", False, "on while PERSISTENCE_HEARTBEAT is off: it does nothing"))

    sources = [s.strip() for s in env("PERSISTENCE_OBSERVE_SOURCES").split(",") if s.strip()]
    if stalled or stalled_on:
        core = blobs.get("heartbeat.py")
        has = core is not None and b"def stalled_on(" in core
        items.append(Item("STALLED support", has,
                          f"heartbeat.py at {sha} has it" if has else
                          f"heartbeat.py at {sha or rev} has no STALLED code (no stalled_on): the switch would turn "
                          "nothing on"))
        has = "heartbeat" in sources
        items.append(Item("PERSISTENCE_OBSERVE_SOURCES", has,
                          "names heartbeat" if has else
                          "lacks heartbeat: report and quiet wakes would fire, never render, and go undeliverable"))
    if stalled:
        found = shutil.which("pheartbeat", path=path)
        items.append(Item("pheartbeat", bool(found), found or
                          "not on PATH: a tick's text tells the session to run `pheartbeat detect` or `continue`"))
    return items


def ready(items):
    return all(i.ok for i in items if i.required)


def _args(argv):
    """(stalled, at), or None when the line isn't `check [--stalled] --at <commit>`; at is None when --at is missing."""
    if not argv or argv[0] != "check":
        return None
    stalled, at, rest = False, None, argv[1:]
    while rest:
        a = rest.pop(0)
        if a == "--stalled" and not stalled:
            stalled = True
        elif a == "--at" and at is None and rest and not rest[0].startswith("-"):
            at = rest.pop(0)
        else:
            return None
    return stalled, at


def main(argv=None, out=print, **kw):
    parsed = _args(list(sys.argv[1:] if argv is None else argv))
    if parsed is None:
        out(__doc__.split("\n\n")[1])
        return 2
    stalled, at = parsed
    if at is None:
        out("--at <commit> is required: the preflight certifies the reviewed commit, and HEAD would certify whatever "
            "happens to be checked out.")
        out(__doc__.split("\n\n")[1])
        return 2
    items = check(stalled=stalled, at=at, **kw)
    for i in items:
        out(f"{'ok' if i.ok else ('MISSING' if i.required else 'note')} {i.name}: {i.detail}")
    level = "PERSISTENCE_HEARTBEAT_STALLED" if stalled else "PERSISTENCE_HEARTBEAT"
    missing = [i.name for i in items if i.required and not i.ok]
    sha = next(i.detail for i in items if i.name == "commit")
    out(f"ready: {level}, as deployed from {sha}" if not missing else f"NOT READY for {level}: {', '.join(missing)}")
    return 0 if not missing else 1


if __name__ == "__main__":
    sys.exit(main())
