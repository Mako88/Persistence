#!/usr/bin/env bash
# The host side of deploy-remote.sh for each named user: installs the staged hook files into ~/.claude/persistence,
# first keeping a copy of each one that changes in previous/<stamp>/. Run as root (deploy-remote.sh runs it with sudo).
# Usage: install-hooks.sh <home root> <stage dir> <UTC stamp> "<file ...>" [user ...]
# The home root is /home/ on the host. It's an argument with no default so that a test, whose fake ssh moves /home/ in
# the command it's given, can't reach the real homes through a default written in here.
#
# Root never opens, makes or writes a path inside a user's home (#49). Everything there (the compare, previous/ and its
# stamp dir, the backup copy, the new file) is done by that user, through `sudo -u`, so a symlink they planted can only
# send a write where they could write anyway. Before this, root made previous/ (as root, 2026-11-15, Arden's msg
# 1571540809344352267) and then, with #47, still compared and `cp -p`'d as root inside the user's folder, where a
# symlinked previous/ or hook file would have sent root's write elsewhere (Arden's note 3 on #47; their point that the
# install's write is the same hazard as the backup's, msg 1574902428355137729).
# The bytes cross over on stdin: root reads the staged file and the user writes it, to a temp file in the same folder
# renamed over the old one (`mv -T` replaces a symlink there rather than following it). Root reads a staged file only
# if it's a regular file, not a symlink, in a stage dir that is itself no symlink and that no group or other can write
# (Arden msg 1574902675328335974). deploy-remote.sh makes the stage dir as the account it deploys from, mode 700. When
# that account is the target user itself, root doesn't read for them: they read their own stage as themselves, so a
# symlink there gets them nothing they couldn't read already. Anything refused is named, the run goes on to the next
# file, and it exits 1 at the end.
set -euo pipefail
homes=${1:?usage}
homes=${homes%/}
stage=${2:?usage}
stage=${stage%/}
stamp=${3:?usage}
read -r -a files <<< "${4:?usage}"
shift 4
cd /  # sudo -u keeps the working directory; the user may not be able to enter root's

if [ -L "$stage" ] || [ ! -d "$stage" ]; then
  echo "refused: the stage dir $stage is a symlink or not a directory; nothing installed" >&2
  exit 1
fi
stage_owner=$(stat -c %U -- "$stage")
if (( 8#$(stat -c %a -- "$stage") & 8#022 )); then
  echo "refused: the stage dir $stage (owner $stage_owner) can be written by group or others; nothing installed" >&2
  exit 1
fi

# Run by the user, with the staged bytes on stdin, or read from the staged path given as $4 when the user owns the
# stage (as themselves; a symlink there is refused all the same). Says one word: same, new or replaced, or a line starting "refused:".
# Never follows a symlink at previous/, its stamp dir or the file itself: a symlinked hook file is kept as the link.
user_side='set -euo pipefail
d=$1 stamp=$2 f=$3 src=${4:-}
umask 022
if [ -L "$d" ] || [ ! -d "$d" ]; then echo "refused: $d is a symlink or not a directory"; exit 0; fi
tmp=$(mktemp "$d/.$f.XXXXXX")
trap '"'"'rm -f "$tmp"'"'"' EXIT
if [ -z "$src" ]; then cat > "$tmp"
elif [ -L "$src" ] || [ ! -f "$src" ]; then echo "refused $f: the staged $src is a symlink or not a regular file; nothing written"; exit 0
else cat -- "$src" > "$tmp"; fi
chmod 644 "$tmp"
if [ ! -e "$d/$f" ] && [ ! -L "$d/$f" ]; then mv -f -T "$tmp" "$d/$f"; echo new; exit 0; fi
if [ ! -L "$d/$f" ] && [ -f "$d/$f" ] && cmp -s "$tmp" "$d/$f"; then echo same; exit 0; fi
for p in "$d/previous" "$d/previous/$stamp"; do
  if [ -L "$p" ] || { [ -e "$p" ] && [ ! -d "$p" ]; }; then
    echo "refused: $p is a symlink or not a directory, so $f could not be kept first; left as it is"; exit 0
  fi
done
[ -d "$d/previous" ] || mkdir -m 755 "$d/previous"
[ -d "$d/previous/$stamp" ] || mkdir -m 700 "$d/previous/$stamp"
cp -P -p "$d/$f" "$d/previous/$stamp/$f"
mv -f -T "$tmp" "$d/$f"
echo replaced'

rc=0
for u in "$@"; do
  d=$homes/$u/.claude/persistence
  kept=
  for f in "${files[@]}"; do
    p=$stage/$f
    if [ -L "$p" ] || [ ! -f "$p" ]; then
      echo "$u: refused $f: the staged $p is a symlink or not a regular file; nothing written" >&2
      rc=1; continue
    fi
    if [ "$stage_owner" = "$u" ]; then
      said=$(sudo -u "$u" bash -c "$user_side" user-side "$d" "$stamp" "$f" "$p" < /dev/null)
    else
      said=$(sudo -u "$u" bash -c "$user_side" user-side "$d" "$stamp" "$f" < "$p")
    fi
    case $said in
      replaced) kept="$kept $f";;
      same|new) ;;
      *) echo "$u: $said" >&2; rc=1;;
    esac
  done
  if [ -n "$kept" ]; then echo "$u: replaced$kept; the copies they replaced are in $d/previous/$stamp"; else echo "$u: no hook file changed"; fi
  owner=$(sudo -u "$u" stat -c %U -- "$d/previous" 2>/dev/null || true)
  if [ -n "$owner" ] && [ "$owner" != "$u" ]; then
    echo "$u: note: $d/previous is owned by $owner, not $u; left as it is (their files change through them)"
  fi
done
exit "$rc"
