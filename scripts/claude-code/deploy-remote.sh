#!/usr/bin/env bash
# Publishes the store CLI for linux-x64 and installs it on a server reached as `ssh <host>`, with the hook
# for each user who runs Claude Code there.
# Usage: deploy-remote.sh [--shared-store] [--units <unit,...|none>] --claim <claim id> <host> [user ...]
# Account-only by default (2026-11-25: a deploy meant for ubuntu only republished the shared /opt/persistence/store and
# restarted Ember's two store units onto d00f8e1 without their say; #14953). Each user named gets the store in its own
# build, /opt/persistence/store.<user>-<commit>, and its own store unit (persistence-store.service for ubuntu,
# persistence-store-<user>.service otherwise) gets a drop-in own-build.conf pointing there; only that unit is restarted,
# and the running binary is read back from /proc. The shared /opt/persistence/store and every other unit are left alone.
# A stopped unit (inactive or failed) is never started (Ember's review of 4664fc2): its build and drop-in change, it is
# checked still stopped with its next start naming the new build, and starting it is a separate, explicit step.
# Unit states (Ember's review of c699bb4): only `active` is restarted, `inactive|failed` stay stopped, and any other
# ActiveState (activating, deactivating, reloading, refreshing, maintenance, or one this script doesn't know) refuses
# under the lock before anything is installed (exit 6). A unit recorded active is read again immediately before its
# restart: if it has stopped or gone into transition it is neither restarted nor started, and the run exits 7.
# own-build.conf sets PERSISTENCE_STORE_EXE to the own build as well: the server ends itself when the file that names
# changes (Server.cs), so a unit left watching the shared binary would restart at the next shared publication.
# Refused, with nothing installed (exit 6): a unit whose own ExecStart doesn't start the shared binary, another drop-in
# of the unit that sets ExecStart or PERSISTENCE_STORE_EXE (it would fight own-build.conf; its self moves it aside), or a build directory of that
# name holding a different commit. No user named and no --shared-store: refused (exit 2), as there is nothing it may touch.
# --shared-store is the old behaviour, asked for explicitly: republish the shared build and restart the units that run
# it (only those; a unit on its own build is no longer restarted). Use it only with every affected self's agreement.
# Naming the units (Ember 1575198901068242967): a run that would change more than one unit, and every --shared-store
# run, needs --units naming exactly the units whose next run it changes (comma-separated; `none` for a shared
# publication that changes none). For --shared-store that is every persistence-store* unit, loaded in any state or only
# installed (list-unit-files), whose ExecStart or PERSISTENCE_STORE_EXE names the shared binary; of those only the
# running ones are restarted, the stopped ones are checked still stopped (Ember's review of 4664fc2). Without it, refused (exit 2); if the set named isn't the set it would restart, refused (exit 6),
# both before anything is installed. For --shared-store the set is read on the host under the lock, so a unit that moved
# on or off the shared build since you looked refuses rather than being restarted unnamed. A one-user run may name its
# one unit; it doesn't have to.
# Verified after the restart: each restarted unit runs the binary it should (/proc), each stopped unit named is still
# stopped, and every other persistence-store* unit kept the MainPID, start time and state it had before the install
# (read-only; exit 7 if one changed).
# Releasing the host's deploy authorization (below): deploy-remote.sh --release --claim <claim id> <host>
# Publishing on Windows loses the execute bit and the folder's permissions, so they are set after unpacking
# (a 203/EXEC "Permission denied" took both stores down for a minute on 2026-11-09).
# The guard, after the 2026-11-14 duplicate deploy claims: two sessions held a claim for one deploy. Only ea465313
# deployed, once, but the second session was cleared to as well.
# 1. The caller holds the live claim for the deploy: claimed by this very session, subject "deploy" (`ptask claim
#    --subject deploy`, or `pwake --subject deploy` from the room). A self has one such claim at a time.
# 2. The host has one deploy authorization at a time, across every self (Ember 1571158094472290428: claims are per
#    state directory, so Arden and I could each hold one): /opt/persistence/.deploy.claim names the claim it is bound
#    to. A deploy binds it if it's free, keeps it if it names the caller's claim, and rebinds it if it names a claim
#    of the caller's own self that is closed. Anything else refuses, so a second claim can't deploy after the first
#    has finished (Arden 1571042268406882415) until the first's self releases it (--release, by the claim's holder or
#    once that claim is closed). Read first, then compared again under the lock, so a change in between refuses.
# 3. The install and restart run under an exclusive flock on the host; a held lock refuses, it doesn't wait.
set -euo pipefail
usage="usage: deploy-remote.sh [--release | --shared-store] [--units <unit,...|none>] --claim <claim id> <host> [user ...]"
release= shared= named= named_given=
while [ $# -gt 0 ] && [ "$1" != --claim ]; do
  case "$1" in
    --release) release=1; shift;;
    --shared-store) shared=1; shift;;
    --units) [ -n "${2:-}" ] || { echo "$usage" >&2; exit 2; }; named_given=1; named=$2; shift 2;;
    *) echo "$usage" >&2; exit 2;;
  esac
done
[ "${1:-}" = --claim ] && [ -n "${2:-}" ] || { echo "$usage" >&2; exit 2; }
[ -z "$release" ] || { [ -z "$shared" ] && [ -z "$named_given" ]; } || { echo "--release takes no other flag; $usage" >&2; exit 2; }
claim=$2
shift 2
host=${1:?$usage}
shift
users=("$@")
if [ -z "$release" ] && [ -z "$shared" ] && [ ${#users[@]} -eq 0 ]; then
  echo "deploy refused: no user named. A deploy touches only the named accounts' own store builds and units;" \
    "republishing the shared /opt/persistence/store and restarting every unit on it needs --shared-store" >&2
  exit 2
fi
for u in "${users[@]}"; do
  [[ $u =~ ^[a-z_][a-z0-9_-]*$ ]] || { echo "deploy refused: '$u' isn't a user name" >&2; exit 2; }
done
# The units named with --units, as a sorted space-separated set ("" for none); ".service" may be left off.
if [ -n "$named_given" ]; then
  if [ "$named" = none ]; then named=; else
    named=$(printf %s "$named" | tr , '\n' | sed '/^$/d; /\.service$/!s/$/.service/' | sort -u | tr '\n' ' ')
    named=${named% }
    for n in $named; do
      [[ $n =~ ^persistence-store[a-z0-9_-]*\.service$ ]] || { echo "deploy refused: '$n' isn't a persistence-store unit" >&2; exit 2; }
    done
  fi
fi
unit_of() { if [ "$1" = ubuntu ]; then echo persistence-store.service; else echo "persistence-store-$1.service"; fi; }
if [ -n "$shared" ] && [ -z "$named_given" ]; then
  echo "deploy refused: --shared-store restarts every unit running the shared build, so it needs --units naming them" \
    "(or --units none); ask each of their selves first" >&2
  exit 2
fi
if [ -z "$release" ] && [ -z "$shared" ]; then
  # Account-only: the units it restarts are the named users' own, known here.
  own=$(for u in "${users[@]}"; do unit_of "$u"; done | sort -u | tr '\n' ' '); own=${own% }
  if [ ${#users[@]} -gt 1 ] && [ -z "$named_given" ]; then
    echo "deploy refused: this run would restart more than one unit ($own); name them with --units to mean it" >&2
    exit 2
  fi
  if [ -n "$named_given" ] && [ "$named" != "$own" ]; then
    echo "deploy refused: --units names '${named:-none}' but this run would restart '$own'; nothing installed" >&2
    exit 6
  fi
fi
repo=$(cd "$(dirname "$0")/../.." && pwd)
claims=("python3" "$repo/scripts/claude-code/claims.py")
if "${claims[@]}" holds "$claim" deploy; then held=1
elif [ -n "$release" ] && "${claims[@]}" closed "$claim"; then held=
else echo "deploy refused: this session doesn't hold claim $claim for the deploy (see above)" >&2; exit 3; fi
auth=/opt/persistence/.deploy.claim
# The authorization reads as free only when the file is absent (Arden's (3) on #37, the same class as A): a file that
# exists but is empty, blank or unreadable can't be told from a binding, so it refuses (exit 5) and is left for its
# self to look at. The script never writes either state. Run on the host, before the lock and again under it.
read_auth="if [ ! -e $auth ]; then :; elif [ -f $auth ] && grep -q '[^[:space:]]' $auth; then cat $auth; else echo \"refused: the host's deploy authorization $auth exists but is empty or unreadable; nothing reads it as free\" >&2; exit 5; fi"
bound=$(ssh "$host" "$read_auth") || exit $?
bound_claim=${bound%% *}
expect=$(printf %s "$bound" | sha256sum | cut -c1-64)
# Under the flock: the authorization still reads as it did above, or this run refuses (exit 5).
same="exec 9</opt/persistence/.deploy.lock
  if ! flock -n 9; then echo \"deploy refused: another deploy holds /opt/persistence/.deploy.lock: \$(cat /opt/persistence/.deploy.holder 2>/dev/null)\" >&2; exit 4; fi
  cur=\$($read_auth)
  if [ \"\$(printf %s \"\$cur\" | sha256sum | cut -c1-64)\" != $expect ]; then echo \"deploy refused: the host's deploy authorization changed while this run checked it: \$cur\" >&2; exit 5; fi"
if [ -n "$release" ]; then
  [ "$bound_claim" = "$claim" ] || { echo "release refused: the host's deploy authorization is ${bound:-free}, not claim $claim" >&2; exit 5; }
  ssh "$host" "set -e
  sudo touch /opt/persistence/.deploy.lock
  $same
  sudo rm -f $auth
  echo \"released the host's deploy authorization from claim $claim\""
  exit 0
fi
if [ -n "$bound" ] && [ "$bound_claim" != "$claim" ] && ! "${claims[@]}" closed "$bound_claim" >/dev/null 2>&1; then
  echo "deploy refused: the host's deploy authorization is bound to another live or unknown claim: $bound" >&2
  echo "Its holder frees it with: deploy-remote.sh --release --claim $bound_claim $host" >&2
  exit 5
fi
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

dotnet publish "$repo/src/Persistence.Store" -c Release -r linux-x64 --self-contained true -o "$work/pub" >/dev/null
# The commit the store was published from, for observe's `deployed` source (observed-now-sources.md); "-dirty" if the
# tree had changes.
echo "$(git -C "$repo" rev-parse HEAD)$(git -C "$repo" diff --quiet HEAD -- src || echo -dirty)" > "$work/pub/DEPLOYED"
tar -czf "$work/store.tgz" -C "$work/pub" .
deployed=$(cat "$work/pub/DEPLOYED")
tag=${deployed:0:7}${deployed:40}  # the short commit, and -dirty when the tree had changes
# Each run stages in its own new directory (mkdir fails if it exists), so a run refused at the lock can't have
# overwritten what the running one copied.
stage=/tmp/persistence-deploy.$claim.$$
units=/etc/systemd/system
shared_exe=/opt/persistence/store/persistence-store
# Every persistence-store* unit's MainPID and start time, one line each, read-only; taken under the lock before the
# install and again after the restarts, and compared for every unit this run didn't restart.
# Every persistence-store* unit there is, loaded (any state) or only installed (list-unit-files), templates aside
# (Ember's review of 4664fc2: an inactive unit configured to run or watch the shared binary counts too).
all_units="{ systemctl list-units --type=service --all --no-legend 'persistence-store*' | awk '{print \$1}' | sed 's/^[^a-z]*//'; systemctl list-unit-files --type=service --no-legend 'persistence-store*' | awk '{print \$1}'; } | grep -v '@\\.service\$' | sort -u"
# A unit is stopped when systemd says inactive or failed; starting a stopped unit is a separate, explicit step this
# script never takes.
stopped="case \"\$(systemctl show -p ActiveState --value \$s)\" in inactive|failed) true;; *) false;; esac"
# Under the lock, before anything is installed, each unit \$s is sorted (Ember's review of c699bb4): active ones join
# \$restarting, stopped ones \$left; any other state refuses, since restarting a unit that is deactivating would start
# something that was being stopped, and one that is activating or reloading is in someone else's hands.
classify="st=\$(systemctl show -p ActiveState --value \$s)
    case \"\$st\" in
      active) restarting=\"\${restarting:+\$restarting }\$s\";;
      inactive|failed) left=\"\${left:+\$left }\$s\";;
      *) echo \"deploy refused: \$s is \$st/\$(systemctl show -p SubState --value \$s), neither active nor stopped; run again once it has settled. Nothing installed\" >&2; exit 6;;
    esac"
# Immediately before restarting \$s, recorded active under the lock: its ActiveState/SubState must match the pattern
# $1, or it is neither restarted nor started and the run exits 7, saying $2 about where that leaves it.
recheck() {
  echo "now=\$(systemctl show -p ActiveState --value \$s)/\$(systemctl show -p SubState --value \$s)
    case \"\$now\" in
      $1) ;;
      *) echo \"\$s: was active under the lock and is now \${now%%/*}; not restarted or started (substate \${now#*/}; $2)\" >&2; exit 7;;
    esac"
}
# Account-only: nothing this run does makes a unit leave `active` before its restart, so only `active` passes.
recheck_own=$(recheck 'active/*' "its build and drop-in are in place, so its next start runs them; the units after it in this run were not installed")
# --shared-store: publication itself ends every server watching the shared binary (Server.cs) and Restart=always
# brings it back after RestartSec, so between publication and the restart a unit can be activating/auto-restart (or
# auto-restart-queued) by this run's own doing; that one is restarted as named. Anything else (deactivating,
# inactive, failed, activating/start) is someone else's change and refuses.
recheck_shared=$(recheck 'active/*|activating/auto-restart|activating/auto-restart-queued' "the shared build is already republished; the units after it in this run were not restarted")
snapshot="$all_units | while read -r s; do echo \"\$s pid=\$(systemctl show -p MainPID --value \$s) since=\$(systemctl show -p ActiveEnterTimestamp --value \$s) state=\$(systemctl show -p ActiveState --value \$s)\"; done"
others_unchanged="after=\$($snapshot)
  changed=
  while read -r s rest; do
    [ -n \"\$s\" ] || continue
    case \" \$restarting \" in *\" \$s \"*) continue;; esac
    now=\$(printf '%s\\n' \"\$after\" | awk -v s=\"\$s\" '\$1 == s' | cut -d' ' -f2-)
    if [ \"\$now\" != \"\$rest\" ]; then echo \"\$s: NOT left alone: was \$rest, now \${now:-gone}\"; changed=1; fi
  done <<< \"\$before\"
  if [ -n \"\$changed\" ]; then exit 7; fi
  echo \"every other persistence-store unit kept its MainPID and start time\""
# Account-only, on the host: each named user's unit and build. Checked in full under the lock before anything is
# installed or bound, so a refusal changes nothing; then installed, restarted and read back one unit at a time.
each_unit="for u in ${users[*]:-}; do
    if [ \$u = ubuntu ]; then unit=persistence-store.service; else unit=persistence-store-\$u.service; fi
    b=/opt/persistence/store.\$u-$tag d=$units/\$unit.d"
preflight="$each_unit
    base=\$(grep -m1 '^ExecStart=/' $units/\$unit 2>/dev/null || true)
    case \"\$base \" in
      'ExecStart=$shared_exe '*) ;;
      *) echo \"deploy refused: $units/\$unit doesn't start $shared_exe (\${base:-no unit file or no ExecStart}); nothing installed\" >&2; exit 6;;
    esac
    other=\$(grep -lE '^ExecStart|^Environment=.*PERSISTENCE_STORE_EXE=' \$d/*.conf 2>/dev/null | grep -v '/own-build.conf\$' || true)
    if [ -n \"\$other\" ]; then echo \"deploy refused: another drop-in of \$unit sets ExecStart or PERSISTENCE_STORE_EXE (\$other); its self moves it aside first, so own-build.conf is the one in force. Nothing installed\" >&2; exit 6; fi
    if [ -e \$b ] && [ \"\$(cat \$b/DEPLOYED 2>/dev/null)\" != $deployed ]; then echo \"deploy refused: \$b exists and doesn't hold $deployed; nothing installed\" >&2; exit 6; fi
  done
  restarting= left=
  $each_unit
    s=\$unit
    $classify
  done
  for s in \$left; do echo \"\$s: \$(systemctl show -p ActiveState --value \$s) before this run; its build and drop-in change, it is not started\"; done"
install_own="$each_unit
    base=\$(grep -m1 '^ExecStart=/' $units/\$unit)
    if [ -e \$b ]; then echo \"\$u: \$b already holds $deployed, reused\"; else
      sudo mkdir \$b
      sudo tar -xzf $stage/store.tgz -C \$b
      sudo chown -R root:root \$b
      sudo chmod -R a+rX \$b
      sudo chmod 755 \$b/persistence-store
    fi
    was=\$(grep -h '^ExecStart=/' \$d/own-build.conf 2>/dev/null || true)
    sudo mkdir -p \$d
    printf '[Service]\\nEnvironment=PERSISTENCE_STORE_EXE=%s\\nExecStart=\\n%s\\n' \$b/persistence-store \"\$(printf %s \"\$base\" | sed \"s#^ExecStart=$shared_exe #ExecStart=\$b/persistence-store #\")\" | sudo tee \$d/own-build.conf >/dev/null
    echo \"\$u: \$unit now starts \$b/persistence-store (own-build.conf; was: \${was:-the shared build})\"
    sudo systemctl daemon-reload
    case \" \$restarting \" in *\" \$unit \"*) s=\$unit; $recheck_own; sudo systemctl restart \$unit;; esac
  done
  sleep 5
  bad=
  $each_unit
    case \" \$restarting \" in
      *\" \$unit \"*)
        pid=\$(systemctl show -p MainPID --value \$unit)
        exe=\$(sudo readlink /proc/\$pid/exe 2>/dev/null || true)
        if [ \"\$exe\" = \$b/persistence-store ]; then echo \"\$unit: running \$exe\"; else echo \"\$unit: NOT verified: it runs \${exe:-nothing readable}, not \$b/persistence-store\"; bad=1; fi;;
      *)
        s=\$unit
        if $stopped && [ \"\$(systemctl show -p MainPID --value \$unit)\" = 0 ]; then echo \"\$unit: still \$(systemctl show -p ActiveState --value \$unit), not started\"; else echo \"\$unit: NOT verified: it was stopped and is now \$(systemctl show -p ActiveState --value \$unit)\"; bad=1; fi
        case \"\$(systemctl show -p ExecStart --value \$unit) \" in
          *\"path=\$b/persistence-store \"*) echo \"\$unit: its next start runs \$b/persistence-store\";;
          *) echo \"\$unit: NOT verified: its ExecStart doesn't name \$b/persistence-store\"; bad=1;;
        esac;;
    esac
  done
  [ -z \"\$bad\" ] || exit 7
  $others_unchanged"
# --shared-store: the shared build, and the units whose ExecStart (drop-ins applied) runs it or whose
# PERSISTENCE_STORE_EXE names it: the server ends itself when that file changes (Server.cs) and Restart=always brings it
# back, so republishing restarts those units too, whether or not this script asks systemd to.
# Every unit counts, running or stopped (Ember's review of 4664fc2): republishing changes what a stopped unit runs at its
# next start, so it is named too. Only the running ones are restarted; the stopped ones are checked still stopped.
preflight_shared="on=\$(for s in \$($all_units); do
    case \"\$(systemctl show -p ExecStart --value \$s) \$(systemctl show -p Environment --value \$s) \" in
      *'path=$shared_exe '*|*' PERSISTENCE_STORE_EXE=$shared_exe '*) echo \$s;;
    esac
  done | sort -u | tr '\\n' ' ')
  on=\${on% }
  if [ \"\$on\" != '$named' ]; then
    echo \"deploy refused: --units names '${named:-none}' but the units that run or watch the shared build, running or stopped, are '\${on:-none}'; nothing installed\" >&2
    exit 6
  fi
  restarting= left=
  for s in \$on; do
    $classify
  done"
install_shared="sudo tar -xzf $stage/store.tgz -C /opt/persistence/store
  sudo chown -R root:root /opt/persistence/store
  sudo chmod -R a+rX /opt/persistence/store
  sudo chmod 755 $shared_exe
  echo \"--shared-store: republished /opt/persistence/store ($deployed); the units named, which run or watch it: \${on:-none}; restarting the running ones: \${restarting:-none}; left stopped: \${left:-none}\"
  for s in \$restarting; do $recheck_shared; sudo systemctl restart \$s; done
  sleep 5
  [ -z \"\$restarting\" ] || systemctl is-active \$restarting
  for s in \$left; do
    if $stopped && [ \"\$(systemctl show -p MainPID --value \$s)\" = 0 ]; then echo \"\$s: still \$(systemctl show -p ActiveState --value \$s), not started\"; else echo \"\$s: NOT verified: it was stopped and is now \$(systemctl show -p ActiveState --value \$s)\"; exit 7; fi
  done
  $others_unchanged"
# hook.py imports coordination.py (the locks and the fired-wake ledger) and, for ptask and run-due, claims.py and
# cwd_exec.py; run-due launches every firing through run_line.py and refuses to fire without it; observe reads its opted-in
# sources from observed.py; persistence-heartbeat@.service runs heartbeat.py from the same place (a beat with the switch
# off exits at its first line, so deploying it turns nothing on): all seven go beside it. A file that differs from the one
# it replaces is copied first to ~/.claude/persistence/previous/<UTC stamp>/ and named in the output, so nothing is
# overwritten silently and the way back is a copy, not a guess. install-hooks.sh does this part on the host, doing
# everything in a peer's home as that peer (#49; the stage is read as its owner, never by root, #93). Each user's ledger-since is then
# written if it isn't there yet (coordination.py, seed-ledger-since: once, never rewritten), so the heartbeat knows where its ledger begins;
# a seeding that fails says "ledger-since NOT written" and the deploy goes on (no file: every firing judged as before).
# Every user named gets every file: a self whose heartbeat is live gets its running heartbeat.py replaced, so name only
# the selves whose code was reviewed for this deploy. Name only Claude Code selves: this installs into and seeds
# ~/.claude/persistence. A Codex self (Ember) keeps its hook and state under ~/.codex/persistence and runs no
# persistence-heartbeat@ unit, so naming one would install files and seed a ledger-since nothing of theirs reads; their
# files change through them (#14953).
py=(hook.py coordination.py claims.py cwd_exec.py run_line.py observed.py heartbeat.py concepts.py)
ssh "$host" "mkdir -m 700 $stage"
scp -q "$work/store.tgz" "${py[@]/#/$repo/scripts/claude-code/}" "$repo/scripts/claude-code/install-hooks.sh" "$host:$stage/"
if [ -n "$shared" ]; then pre=$preflight_shared install=$install_shared; else pre=$preflight install=$install_own; fi
# Arden's B: the claim is checked again once the host's lock is held, right before anything is installed. The host
# says LOCKED (lock held, authorization unchanged) and waits; this side re-runs `holds` in the caller's own claims and
# answers go or stop. Anything but go, an EOF included, refuses there with nothing installed.
coproc REMOTE { ssh "$host" "set -e
  trap 'rm -rf $stage' EXIT
  sudo touch /opt/persistence/.deploy.lock
  $same
  echo LOCKED
  read -r go || true
  if [ \"\$go\" != go ]; then echo \"deploy refused: claim $claim was no longer held once the host's lock was taken\" >&2; exit 3; fi
  $pre
  before=\$($snapshot)
  if [ \"\${cur%% *}\" != $claim ]; then
    echo \"$claim \$(id -un) ${CLAUDE_CODE_SESSION_ID:-} \$(date -u +%Y-%m-%dT%H:%M:%SZ)\" | sudo tee $auth >/dev/null
    echo \"the host's deploy authorization is now bound to claim $claim\${cur:+ (was: \$cur)}\"
  fi
  echo \"claim $claim, session ${CLAUDE_CODE_SESSION_ID:-}, since \$(date -u +%Y-%m-%dT%H:%M:%SZ)\" | sudo tee /opt/persistence/.deploy.holder >/dev/null
  stamp=\$(date -u +%Y%m%dT%H%M%SZ)
  sudo bash $stage/install-hooks.sh /home/ $stage \$stamp \"${py[*]}\" ${users[*]:-}
  for u in ${users[*]:-}; do
    d=/home/\$u/.claude/persistence
    if seeded=\$(sudo -u \$u env PERSISTENCE_STATE_DIR=\$d/state python3 -I \$d/coordination.py seed-ledger-since); then
      echo \"\$u: \$seeded\"
    else
      echo \"\$u: ledger-since NOT written (seed-ledger-since failed, above); its heartbeat judges every firing as before\"
    fi
  done
  $install"; }
exec {from}<&"${REMOTE[0]}" {to}>&"${REMOTE[1]}"
remote=$REMOTE_PID
if IFS= read -r line <&"$from" && [ "$line" = LOCKED ]; then
  if "${claims[@]}" holds "$claim" deploy >/dev/null; then echo go >&"$to"; else echo stop >&"$to"; fi
fi
exec {to}>&-
cat <&"$from"
rc=0
wait "$remote" || rc=$?
exit "$rc"
