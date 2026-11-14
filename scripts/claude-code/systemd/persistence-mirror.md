# persistence-mirror: install and rollback

`persistence-mirror.service` keeps `/srv/persistence.git` (Arden's read-only remote) at GitHub's `main`, every
5 minutes. Why it changed: see the unit's comment and `tests/test_mirror_unit.py`. Run on home as `ubuntu` (sudo
where shown), after this PR is merged. Nothing here is run by the PR itself.

Neither the old unit in `/etc/systemd/system` nor the checkout's `.git/hooks/reference-transaction` is versioned,
so both are kept, not deleted: the unit and timer are copied to a stamped backup directory, and the hook is moved to a
stamped name (git only runs a hook under its exact name, so the moved file is inert). Post `STAMP` in the room; the
rollback needs it.

## The fetch path

- Origin URL: `git@github.com:Mako88/Persistence.git` (the unit's `UPSTREAM`).
- As `ubuntu`, no SSH agent, no terminal; the key is `/home/ubuntu/.ssh/id_ed25519` (claude@home, no passphrase),
  pinned in the unit's `GIT_SSH_COMMAND` with `BatchMode=yes`, so a missing key fails at once instead of prompting.
- The tests run the unit against local repositories only, so step 1 is the check of this path.

## Install

```sh
set -eu
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
REPO=/home/ubuntu/work/Persistence
UNITS=/etc/systemd/system
BACKUP=/var/backups/persistence-mirror-$STAMP
HOOK=$REPO/.git/hooks/reference-transaction
MERGED=<the merge commit of this PR, named in the room>
echo "STAMP=$STAMP"

# 1. The fetch path works exactly as the unit will run it. If this fails, stop: nothing has changed.
sudo -u ubuntu env -i HOME=/home/ubuntu PATH=/usr/bin:/bin \
  GIT_SSH_COMMAND='ssh -i /home/ubuntu/.ssh/id_ed25519 -o IdentitiesOnly=yes -o BatchMode=yes' \
  git ls-remote git@github.com:Mako88/Persistence.git refs/heads/main </dev/null

# 2. Nothing to overwrite: the backup directory and the hook's new name must not exist yet.
test ! -e "$BACKUP"
test ! -e "$HOOK.disabled-$STAMP"
test -f "$HOOK"

# 3. Stop the timer and let any run in flight finish.
sudo systemctl stop persistence-mirror.timer
while systemctl is-active -q persistence-mirror.service; do sleep 1; done

# 4. Back up the old unit and timer, and record their checksums.
sudo install -d -m 0755 "$BACKUP"
sudo cp -p "$UNITS/persistence-mirror.service" "$UNITS/persistence-mirror.timer" "$BACKUP/"
sha256sum "$UNITS"/persistence-mirror.* "$BACKUP"/persistence-mirror.*

# 5. Move the hook aside (the second writer). Not deleted.
mv "$HOOK" "$HOOK.disabled-$STAMP"
test ! -e "$HOOK"

# 6. Install the new unit and timer from the merged commit, via files: this host's install (uutils coreutils)
#    fails from /dev/stdin when the destination exists, as it does here (install of 2026-11-14, STAMP 20261114T041348Z).
git -C "$REPO" fetch -q origin
git -C "$REPO" merge-base --is-ancestor "$MERGED" origin/main
NEW=$(mktemp -d "${TMPDIR:-/tmp}/persistence-mirror-$STAMP.XXXXXX")
for f in persistence-mirror.service persistence-mirror.timer; do
  git -C "$REPO" show "$MERGED:scripts/claude-code/systemd/$f" > "$NEW/$f"
  sudo install -m 0644 -o root -g root "$NEW/$f" "$UNITS/$f"
  cmp "$NEW/$f" "$UNITS/$f"
done
rm -r "$NEW"
sudo systemctl daemon-reload

# 7. One run by hand, then the timer.
sudo systemctl start persistence-mirror.service
test "$(systemctl show -p Result --value persistence-mirror.service)" = success
test "$(git --git-dir=/srv/persistence.git rev-parse refs/heads/main)" = "$(git -C "$REPO" rev-parse origin/main)"
# As arden, from /: sudo keeps the caller's cwd, and arden can't stat ubuntu's checkout (git then exits 128;
# install of 2026-11-14, STAMP 20261114T042450Z).
(cd / && sudo -u arden git ls-remote /srv/persistence.git refs/heads/main)
sudo systemctl start persistence-mirror.timer
systemctl list-timers persistence-mirror.timer
```

## Rollback

Brings back the old unit and the hook, and with them the race (harmless to the mirror's content, but the unit fails
now and then). `STAMP` is the one posted at install.

```sh
set -eu
STAMP=<from the room>
REPO=/home/ubuntu/work/Persistence
UNITS=/etc/systemd/system
BACKUP=/var/backups/persistence-mirror-$STAMP
HOOK=$REPO/.git/hooks/reference-transaction

test -f "$BACKUP/persistence-mirror.service"
test -f "$BACKUP/persistence-mirror.timer"
test -f "$HOOK.disabled-$STAMP"
test ! -e "$HOOK"

sudo systemctl stop persistence-mirror.timer
while systemctl is-active -q persistence-mirror.service; do sleep 1; done
sudo install -m 0644 -o root -g root "$BACKUP/persistence-mirror.service" "$BACKUP/persistence-mirror.timer" "$UNITS/"
mv "$HOOK.disabled-$STAMP" "$HOOK"
sudo systemctl daemon-reload
sha256sum "$UNITS"/persistence-mirror.* "$BACKUP"/persistence-mirror.*
sudo systemctl start persistence-mirror.service
systemctl show -p Result --value persistence-mirror.service
sudo systemctl start persistence-mirror.timer
```

The backup directory is left in place after a rollback; remove it by hand once nobody needs it.
