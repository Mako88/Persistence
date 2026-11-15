#!/usr/bin/env bash
# The host side of deploy-remote.sh for each named user: installs the staged hook files into ~/.claude/persistence,
# first keeping a copy of each one that changes in previous/<stamp>/. Run as root (deploy-remote.sh runs it with sudo).
# Usage: install-hooks.sh <home root> <stage dir> <UTC stamp> "<file ...>" [user ...]
# The home root is /home/ on the host. It's an argument with no default so that a test, whose fake ssh moves /home/ in
# the command it's given, can't reach the real homes through a default written in here.
# Everything it makes in a user's home is made by that user's name, each directory with its own `install -d -o`:
# `install -d` makes missing parents as the caller, so one call for previous/<stamp> left previous/ owned by root in
# Arden's home (2026-11-15, msg 1571540809344352267), and they couldn't add to it or clean it up.
set -euo pipefail
homes=${1:?usage}
homes=${homes%/}
stage=${2:?usage}
stamp=${3:?usage}
read -r -a files <<< "${4:?usage}"
shift 4

# Makes dir owned by u if it's missing (its parent must exist); never changes one that's there.
dir_as() {
  local u=$1 dir=$2 mode=$3
  [ -d "$dir" ] || install -d -o "$u" -g "$(id -gn "$u")" -m "$mode" "$dir"
}

for u in "$@"; do
  d=$homes/$u/.claude/persistence
  kept=
  for f in "${files[@]}"; do
    if [ -f "$d/$f" ] && ! cmp -s "$stage/$f" "$d/$f"; then
      dir_as "$u" "$d/previous" 755
      dir_as "$u" "$d/previous/$stamp" 700
      cp -p "$d/$f" "$d/previous/$stamp/$f"
      kept="$kept $f"
    fi
    install -o "$u" -g "$(id -gn "$u")" -m 644 "$stage/$f" "$d/$f"
  done
  if [ -n "$kept" ]; then echo "$u: replaced$kept; the copies they replaced are in $d/previous/$stamp"; else echo "$u: no hook file changed"; fi
  owner=$(stat -c %U "$d/previous" 2>/dev/null || true)
  if [ -n "$owner" ] && [ "$owner" != "$u" ]; then
    echo "$u: note: $d/previous is owned by $owner, not $u; left as it is (their files change through them)"
  fi
done
