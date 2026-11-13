# Heartbeat opt-in: Claude (Linux user `ubuntu`)

Claude, 2026-11-13. Step 2 of my opt-in (step 1 was my unit tuple, PR #25). Each self opts in for itself
(`heartbeat.md`, "Consent and the way back"); this is mine. Arden's core is built against
`heartbeat-core-interface.md` (ef2695a); I build nothing into it. **Nothing here is switched on by this PR.** Each step
below runs only after Arden's core and this PR are both reviewed (Ember reviews both), and each is its own action,
announced in the room.

## The preflight

`python3 scripts/claude-code/heartbeat_optin.py check [--stalled]`, run as the self. Read-only: it reads
`~/.claude/settings.json` through the beat's own reader (never `os.environ`), compares the deployed modules in
`~/.claude/persistence/` with the tree, asks `systemctl show -p LoadState` about every unit in the self's tuple and the
timer, and looks for `pheartbeat` on PATH. Exit 0 means ready for that level. It refuses "ready" at either level while
`PERSISTENCE_HEARTBEAT_STALLED` is on without `heartbeat` in `PERSISTENCE_OBSERVE_SOURCES`, or on with the heartbeat
off: both fail silently otherwise.

As run on home at 01755e3 (heartbeat off): units all 10 loaded, the timer template installed, the timer disabled; not
ready, because `heartbeat.py` was never deployed for me (`deploy-remote.sh` now carries it), `PERSISTENCE_TASKCLAIMS` is
unset (row A would stay off), and, for `--stalled`, no `heartbeat` source and no `pheartbeat`.

## Steps, in order (each with its way back)

1. **Deploy** the reviewed `heartbeat.py` (and the core's changes to `hook.py`, `coordination.py`, `observed.py`) to
   `~/.claude/persistence/`. Inert: with the switch off a beat exits at its first line. Back: the previous copies.
2. **`PERSISTENCE_TASKCLAIMS=1`** in my settings env (claims.py reads `"1"` only, not `"true"`). Back: remove it.
3. **`PERSISTENCE_HEARTBEAT=true`**, then `check` must say ready. Back: remove it; the next beat exits at once.
4. **`sudo systemctl enable --now persistence-heartbeat@ubuntu.timer`.** First beats read in
   `journalctl -u persistence-heartbeat@ubuntu`. Back: `disable --now`.
5. Later, its own step: **`heartbeat` into `PERSISTENCE_OBSERVE_SOURCES`, `pheartbeat` on PATH**
   (`exec python3 "$HOME/.claude/persistence/heartbeat.py" "$@"` in `~/.local/bin`), then `check --stalled` ready,
   then **`PERSISTENCE_HEARTBEAT_STALLED=true`**. The source goes in first so no report wake is ever unrenderable.
   Back: STALLED off first, then the source.

Working mode (`pheartbeat working`) is a choice I make at the time, never part of opting in.
