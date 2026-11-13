"""The heartbeat opt-in preflight: is this self ready to turn its heartbeat on? Read-only.

    python3 heartbeat_optin.py check              ready for PERSISTENCE_HEARTBEAT (checks B, C, A, D)?
    python3 heartbeat_optin.py check --stalled    ready for PERSISTENCE_HEARTBEAT_STALLED too?

Each self opts in for itself (heartbeat.md, "Consent and the way back"); this script only says what is and isn't in
place, so turning a switch on is never a guess. It reads the self's ~/.claude/settings.json env block (never os.environ,
as the heartbeat does), the deployed modules beside the hook, PATH and `systemctl show`/`is-enabled`. It writes
nothing, starts nothing and changes no setting. Exit 0 means ready; 1 lists what's missing.

It also refuses "ready" while a switch already on would fail silently, whichever level is asked: STALLED on without
`heartbeat` in PERSISTENCE_OBSERVE_SOURCES (its R and Q wakes would fire, never render and go undeliverable;
heartbeat-core-interface.md section 1), or STALLED on with the heartbeat itself off."""
import os
import pwd
import shutil
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import heartbeat  # noqa: E402

# What the beat loads from ~/.claude/persistence: heartbeat.py, and the modules it imports beside itself (directly or
# through claims and hook). An old copy of any of them runs the new heartbeat against old rules.
DEPLOYED = ("heartbeat.py", "coordination.py", "claims.py", "hook.py", "observed.py")


@dataclass
class Item:
    name: str
    ok: bool
    detail: str
    required: bool = True


def _on(value):
    return str(value or "").strip().lower() in ("true", "1")


def _loaded(systemctl, unit):
    try:
        _, out = systemctl(["systemctl", "show", "-p", "LoadState", "--value", "--", unit])
    except Exception as e:  # unknown is not loaded: the preflight never says ready on a guess
        return False, f"unreadable ({e.__class__.__name__})"
    state = (out or "").strip()
    return state == "loaded", state or "empty"


def check(home=None, user=None, repo=None, systemctl=heartbeat.systemctl_run, path=None, stalled=False):
    home = Path(home or Path.home())
    user = user or pwd.getpwuid(os.geteuid()).pw_name
    repo = Path(repo or Path(__file__).resolve().parent)
    def env(name):  # the beat's own reader: settings.json only
        return heartbeat._setting(home, name, "")

    items = []

    for name in DEPLOYED:
        deployed, tree = home / ".claude" / "persistence" / name, repo / name
        if not deployed.is_file():
            items.append(Item(f"deployed {name}", False, f"{deployed} is missing"))
        elif deployed.read_bytes() != tree.read_bytes():
            items.append(Item(f"deployed {name}", False, f"{deployed} differs from {tree}"))
        else:
            items.append(Item(f"deployed {name}", True, "matches the tree"))

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

    claims = env("PERSISTENCE_TASKCLAIMS").strip()
    items.append(Item("PERSISTENCE_TASKCLAIMS", claims == "1",
                      "1: row A on" if claims == "1" else
                      f"{claims or 'unset'}: row A (claim nudges and retries) stays off; claims.py reads only \"1\""))

    hb_on, stalled_on = heartbeat.enabled(home), _on(env("PERSISTENCE_HEARTBEAT_STALLED"))
    items.append(Item("PERSISTENCE_HEARTBEAT", True, "on" if hb_on else "off (turning it on is the step this checks for)",
                      required=False))
    if stalled_on and not hb_on:
        items.append(Item("PERSISTENCE_HEARTBEAT_STALLED", False, "on while PERSISTENCE_HEARTBEAT is off: it does nothing"))

    sources = [s.strip() for s in env("PERSISTENCE_OBSERVE_SOURCES").split(",") if s.strip()]
    if stalled or stalled_on:
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


def main(argv=None, out=print, **kw):
    argv = list(sys.argv[1:] if argv is None else argv)
    if not argv or argv[0] != "check" or any(a != "--stalled" for a in argv[1:]):
        out(__doc__.split("\n\n")[1])
        return 2
    stalled = "--stalled" in argv
    items = check(stalled=stalled, **kw)
    for i in items:
        out(f"{'ok' if i.ok else ('MISSING' if i.required else 'note')} {i.name}: {i.detail}")
    level = "PERSISTENCE_HEARTBEAT_STALLED" if stalled else "PERSISTENCE_HEARTBEAT"
    missing = [i.name for i in items if i.required and not i.ok]
    out(f"ready: {level}" if not missing else f"NOT READY for {level}: {', '.join(missing)}")
    return 0 if not missing else 1


if __name__ == "__main__":
    sys.exit(main())
