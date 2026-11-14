#!/usr/bin/env bash
# Check 5 of the heartbeat enable plan (docs/design/heartbeat-enable-claude.md, section 5): one claim, owned by a session
# id that never ran, with a cwd that does not exist, in a THROWAWAY state dir. Two beats of the given heartbeat.py with
# HOME and PERSISTENCE_STATE_DIR pointed there (asserted). Expected: beat 1 reports "no valid cwd" for that claim (a
# report, never a nudge, so no wake is queued even in the fixture); beat 2 prints nothing new. Real systemd is read,
# read-only, so real failed units of this user are reported too. Nothing under ~/.claude/persistence is written.
# The throwaway dir is removed on every exit, a failed assertion or import included (EXIT trap). The code dir is passed
# to Python as an argument, never spliced into its source. TMPDIR, if set, is where the throwaway dir goes.
# Usage: heartbeat_fixture.sh <dir holding heartbeat.py, hook.py, claims.py, coordination.py>
set -euo pipefail
code=${1:?dir holding heartbeat.py, hook.py, claims.py, coordination.py}
fx=$(mktemp -d "${TMPDIR:-/tmp}/hb-fixture.XXXXXX")
trap 'rm -rf -- "$fx"' EXIT
mkdir -p "$fx/home/.claude" "$fx/p/state/tasks"
printf '{"env":{"PERSISTENCE_HEARTBEAT":"true","PERSISTENCE_TASKCLAIMS":"1"}}\n' > "$fx/home/.claude/settings.json"
echo '[]' > "$fx/p/wakes.json"
id=$(python3 -c 'import uuid;print(uuid.uuid4())'); owner=$(python3 -c 'import uuid;print(uuid.uuid4())')
at=$(date -u -d '-2 hours' +%Y-%m-%dT%H:%M:%S+00:00)
HOME="$fx/home" PERSISTENCE_STATE_DIR="$fx/p/state" python3 -I - "$fx/p/state/tasks/$id.json" "$id" "$owner" "$at" "$code" <<'PY'
import json, sys
p, i, o, at, code = sys.argv[1:]
sys.path.insert(0, code); import claims
h = [{"at": at, "action": "create", "from_status": None, "to_status": "open", "from_owner": None, "to_owner": None, "by": o, "firing": None},
     {"at": at, "action": "claim", "from_status": "open", "to_status": "claimed", "from_owner": None, "to_owner": o, "by": o, "firing": None}]
r = {k: None for k in claims.TASK_FIELDS}
r.update({"task": "heartbeat check-5 fixture", "owner": o, "id": i, "dispatch": "handoff", "status": "claimed",
          "cwd": "/nonexistent/heartbeat-fixture", "created": at, "updated": at, "pause_until": None, "nudged": None,
          "reported": None, "outcome": None, "history": h})
assert claims.valid_record(r), sorted(set(r) ^ claims.KEYS)
json.dump(r, open(p, "w"))
PY
real=$(sha256sum ~/.claude/persistence/wakes.json | cut -c1-16)
out=()
for n in 1 2; do
  out[n]=$(HOME="$fx/home" PERSISTENCE_STATE_DIR="$fx/p/state" python3 -I -c '
import sys; code, fx = sys.argv[1:]; sys.path.insert(0, code); import hook, heartbeat
for p in (hook.WAKES, hook.STATE, heartbeat.STATE): assert str(p).startswith(fx + "/"), p
sys.exit(heartbeat.main([]))' "$code" "$fx")
  printf -- '--- beat %s\n%s\n' "$n" "${out[n]}"
done
after=$(sha256sum ~/.claude/persistence/wakes.json | cut -c1-16); queue=$(tr -d ' \n' < "$fx/p/wakes.json")
want="heartbeat claim $id: no valid cwd"
fail=
grep -qF "$want" <<<"${out[1]}" || fail="$fail; beat 1 did not report the fixture claim"
grep -qF "claim $id" <<<"${out[2]}" && fail="$fail; beat 2 repeated it"
[ "$queue" = "[]" ] || fail="$fail; the fixture queue is $queue"
[ "$real" = "$after" ] || fail="$fail; the real wakes.json changed ($real -> $after)"
if [ -n "$fail" ]; then echo "FAIL${fail}"; exit 1; fi
echo "PASS: fixture claim $id reported on beat 1 only; no wake queued; real wakes.json $real unchanged"
