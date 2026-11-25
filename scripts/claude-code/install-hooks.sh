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
#
# Root never reads the stage either (Ember's review of #93, msg 1575147022892408963). The stage dir must be no symlink,
# writable by no group or other, and owned by root or by the account that ran sudo ($SUDO_USER; deploy-remote.sh makes
# it as that account, mode 700); any other owner is refused. Each staged file is read by the stage's owner, as that
# owner, into a private directory root made for this run, so a staged file swapped for a symlink between a check and a
# read gets nothing the owner couldn't read already. (At 6a46981 root checked the file and then opened it by path; with
# a stage owned by nobody, 41 of 400 real runs put a root-only canary's bytes into the target's home.)
#
# Everything run as another account runs in a clean environment, not one left to sudoers: `env -i` with a fixed PATH,
# under a non-login, non-interactive bash that reads no startup files.
#
# Order: (1) the stage's files are read; (2) every user and every file is checked before anything changes: the hook
# folder, the file there, and for a file that will be replaced, previous/, its stamp dir and the backup's own name,
# which must not exist yet (a link there included). Any problem found is named, with all the others, and nothing is
# installed. (3) The files are installed, each in one step as its user: a temp file in the same folder, renamed over
# the old one (`mv -T` replaces a symlink, never follows it), after the old one is kept. If a step fails anyway, the run
# stops there and names the file that failed, what had landed, and every user and file it didn't reach. For the file
# that failed it reports what is there, looked at as that user after the failure, not what the step order implies
# (Ember's review of #93 at 735f69b, msg 1575154140798255107): whether the file now holds the new bytes, and whether a
# backup of the old one is at its name. A look that fails says unknown. A backup that landed is left where it is.
set -euo pipefail
homes=${1:?usage}
homes=${homes%/}
stage=${2:?usage}
stage=${stage%/}
stamp=${3:?usage}
read -r -a files <<< "${4:?usage}"
shift 4
users=("$@")
cd /  # sudo -u keeps the working directory; the user may not be able to enter root's

refuse() { echo "refused: $*; nothing installed" >&2; exit 1; }
# Runs a bash script as an account, with nothing of root's environment: as <account> <script> <args...>
as() { local who=$1; shift; sudo -u "$who" env -i PATH=/usr/bin:/bin bash --noprofile --norc -c "$@"; }

case $stamp in ''|*[!0-9TZ]*) refuse "the stamp '$stamp' isn't a UTC stamp like 20261125T140000Z";; esac
for f in "${files[@]}"; do case $f in ''|.*|*/*) refuse "the file name '$f' isn't a plain name";; esac; done
for u in "${users[@]}"; do case $u in ''|.*|-*|*/*) refuse "the user name '$u' isn't a plain name";; esac; done

if [ -L "$stage" ] || [ ! -d "$stage" ]; then refuse "the stage dir $stage is a symlink or not a directory"; fi
stage_owner=$(stat -c %U -- "$stage")
if (( 8#$(stat -c %a -- "$stage") & 8#022 )); then
  refuse "the stage dir $stage (owner $stage_owner) can be written by group or others"
fi
if [ "$stage_owner" != root ] && [ "$stage_owner" != "${SUDO_USER:-}" ]; then
  refuse "the stage dir $stage is owned by $stage_owner, which is neither root nor the account that ran sudo (${SUDO_USER:-none})"
fi

# (1) Read as the stage's owner, into root's own directory for this run (mktemp: new, mode 700, nobody else's).
snap=$(mktemp -d)
trap 'rm -rf -- "$snap"' EXIT
read_staged='if [ -L "$1" ] || [ ! -f "$1" ]; then echo "the staged $1 is a symlink or not a regular file" >&2; exit 3; fi
exec cat -- "$1"'
problems=()
for f in "${files[@]}"; do
  if ! err=$(as "$stage_owner" "$read_staged" read-staged "$stage/$f" 2>&1 > "$snap/$f"); then
    problems+=("refused $f: ${err:-reading it as $stage_owner failed} (read as $stage_owner)")
    rm -f -- "$snap/$f"
  fi
done

# Run by the user: user-side check|install <hook dir> <stamp> <file>, with the new bytes on stdin. Its last line is one
# word, new, same or replaced; a check it fails ends it with exit 3 and a line starting "refused:". `install` repeats
# every check first, then acts. It never writes through a symlink: not at the hook dir, the file, previous/, the stamp
# dir or the backup's name (the backup is made under a fresh temp name and hard-linked to its name, which fails if
# anything, a link included, is there already). A symlinked hook file is kept as the link.
read -r -d '' user_side <<'EOF' || true
set -euo pipefail
mode=$1 d=$2 stamp=$3 f=$4
umask 022
t=$d/$f prev=$d/previous sd=$d/previous/$stamp leaf=$d/previous/$stamp/$f
tmp= bk=
trap 'rm -f -- ${tmp:+"$tmp"} ${bk:+"$bk"}' EXIT
no() { echo "refused: $*"; exit 3; }
if [ -L "$d" ] || [ ! -d "$d" ]; then no "$d is a symlink or not a directory"; fi
if [ ! -w "$d" ] || [ ! -x "$d" ]; then no "$d can't be written by $(id -un)"; fi
new=-
if [ "$mode" = install ]; then tmp=$(mktemp "$d/.$f.XXXXXX"); cat > "$tmp"; chmod 644 "$tmp"; new=$tmp; fi
if [ ! -e "$t" ] && [ ! -L "$t" ]; then plan=new
elif [ -L "$t" ]; then plan=replaced
elif [ ! -f "$t" ]; then no "$t is not a regular file or a symlink"
elif [ ! -r "$t" ]; then no "$t can't be read, so it can't be compared or kept"
elif cmp -s -- "$new" "$t"; then plan=same
else plan=replaced; fi
if [ "$plan" = replaced ]; then
  for p in "$prev" "$sd"; do
    if [ -L "$p" ] || { [ -e "$p" ] && [ ! -d "$p" ]; }; then no "$p is a symlink or not a directory, so $f couldn't be kept first"; fi
    if [ -d "$p" ] && { [ ! -w "$p" ] || [ ! -x "$p" ]; }; then no "$p can't be written by $(id -un), so $f couldn't be kept first"; fi
  done
  if [ -e "$leaf" ] || [ -L "$leaf" ]; then no "$leaf is already there (a file or a link), so $f's copy would land on it"; fi
fi
if [ "$mode" = check ] || [ "$plan" = same ]; then echo "$plan"; exit 0; fi
if [ "$plan" = replaced ]; then
  [ -d "$prev" ] || mkdir -m 755 -- "$prev"
  [ -d "$sd" ] || mkdir -m 700 -- "$sd"
  bk=$(mktemp "$sd/.$f.XXXXXX")
  cp -P -p --remove-destination -- "$t" "$bk"
  ln -P -T -- "$bk" "$leaf"
  rm -f -- "$bk"; bk=
fi
mv -f -T -- "$tmp" "$t"; tmp=
echo "$plan"
EOF

# Run by the user after an install step failed: inspect <target> <backup name>, with the new bytes on stdin. Prints
# two lines, "target: ..." and "backup: ...", each what it saw: a kind (stat, not following a link), absent only when
# stat said there is no such file, and otherwise unknown with stat's own words. The target is compared with the new
# bytes only if it is a regular file. It changes nothing.
read -r -d '' inspect <<'EOF' || true
set -u
kind() {
  local k
  if k=$(LC_ALL=C stat -c %F -- "$1" 2>&1); then echo "$k"
  elif [[ $k == *"No such file or directory" ]]; then echo absent
  else echo "unknown (${k//$'\n'/ })"; fi
}
k=$(kind "$1")
case $k in
  "regular file"|"regular empty file")
    cmp -s - "$1"; rc=$?
    case $rc in
      0) t="the new version: the rename happened";;
      1) t="a regular file, not the new version";;
      *) t="unknown (a regular file that couldn't be compared with the new version)";;
    esac;;
  absent|unknown*) t=$k;;
  *) t="a $k, not the new version";;
esac
echo "target: $t"
k=$(kind "$2")
case $k in absent|unknown*) echo "backup: $k";; *) echo "backup: there (a $k)";; esac
EOF
observe() {  # observe <user> <hook dir> <file>: what is there now, looked at as the user
  local u=$1 d=$2 f=$3 o t b
  local leaf=$d/previous/$stamp/$f
  if o=$(as "$u" "$inspect" inspect "$d/$f" "$leaf" < "$snap/$f" 2>&1); then
    t=$(sed -n 's/^target: //p' <<< "$o") b=$(sed -n 's/^backup: //p' <<< "$o")
  else
    o=${o//$'\n'/ }
    t= b=
  fi
  : "${t:=unknown (looking as $u failed${o:+: $o})}" "${b:=unknown (looking as $u failed${o:+: $o})}"
  echo "$u: $f, seen as $u after the failure: $d/$f is $t; the backup's name $leaf: $b"
}

# (2) Check every user and file before anything changes.
for u in "${users[@]}"; do
  d=$homes/$u/.claude/persistence
  for f in "${files[@]}"; do
    [ -f "$snap/$f" ] || continue  # its read was refused, above
    if ! out=$(as "$u" "$user_side" user-side check "$d" "$stamp" "$f" < "$snap/$f" 2>&1); then
      problems+=("$u: $f: ${out:-the check failed with no message}")
    fi
  done
done
if (( ${#problems[@]} )); then
  printf '%s\n' "${problems[@]}" >&2
  echo "nothing installed: ${#problems[@]} problem(s) found before any change" >&2
  exit 1
fi

# (3) Install, stopping at the first failure.
summary() {  # summary <user> <hook dir>: what landed for that user, new, replaced and same counted apart
  local s=
  [ -n "$new" ] && s="$s; new:$new"
  [ -n "$replaced" ] && s="$s; replaced:$replaced (the copies they replaced are in $2/previous/$stamp)"
  [ -n "$same" ] && s="$s; same:$same"
  if [ -z "$new$replaced" ]; then s="no hook file changed$s"; else s=${s#; }; fi
  echo "$1: $s"
}
for ((i = 0; i < ${#users[@]}; i++)); do
  u=${users[i]}
  d=$homes/$u/.claude/persistence
  new= replaced= same=
  for ((j = 0; j < ${#files[@]}; j++)); do
    f=${files[j]}
    if out=$(as "$u" "$user_side" user-side install "$d" "$stamp" "$f" < "$snap/$f" 2>&1) \
       && case ${out##*$'\n'} in new|same|replaced) true;; *) false;; esac; then
      case ${out##*$'\n'} in new) new="$new $f";; same) same="$same $f";; replaced) replaced="$replaced $f";; esac
      continue
    fi
    echo "STOPPED at $u: $f: ${out:-failed with no message}" >&2
    observe "$u" "$d" "$f" >&2
    summary "$u" "$d" >&2
    left=$(for ((k = j + 1; k < ${#files[@]}; k++)); do printf ' %s' "${files[k]}"; done)
    [ -n "$left" ] && echo "not attempted, $u:$left" >&2
    for ((k = i + 1; k < ${#users[@]}; k++)); do echo "not attempted, ${users[k]}: ${files[*]}" >&2; done
    exit 1
  done
  summary "$u" "$d"
  owner=$(as "$u" 'stat -c %U -- "$1"' owner "$d/previous" 2>/dev/null || true)
  if [ -n "$owner" ] && [ "$owner" != "$u" ]; then
    echo "$u: note: $d/previous is owned by $owner, not $u; left as it is (their files change through them)"
  fi
done
