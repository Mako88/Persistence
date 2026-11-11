#!/usr/bin/env bash
# Publishes the store CLI for linux-x64 and installs it on a server reached as `ssh <host>`, with the hook
# for each user who runs Claude Code there. Usage: deploy-remote.sh <host> [user ...]
# Publishing on Windows loses the execute bit and the folder's permissions, so they are set after unpacking
# (a 203/EXEC "Permission denied" took both stores down for a minute on 2026-11-09).
set -euo pipefail
host=${1:?usage: deploy-remote.sh <host> [user ...]}
shift
users=("$@")
repo=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

dotnet publish "$repo/src/Persistence.Store" -c Release -r linux-x64 --self-contained true -o "$work/pub" >/dev/null
tar -czf "$work/store.tgz" -C "$work/pub" .
# hook.py imports coordination.py (the locks and the fired-wake ledger) and, for ptask and run-due, claims.py and
# cwd_exec.py: all four go beside it.
py=(hook.py coordination.py claims.py cwd_exec.py)
scp -q "$work/store.tgz" "${py[@]/#/$repo/scripts/claude-code/}" "$host:/tmp/"
ssh "$host" "set -e
  sudo tar -xzf /tmp/store.tgz -C /opt/persistence/store
  sudo chown -R root:root /opt/persistence/store
  sudo chmod -R a+rX /opt/persistence/store
  sudo chmod 755 /opt/persistence/store/persistence-store
  for u in ${users[*]:-}; do for f in ${py[*]}; do sudo install -o \$u -g \$u -m 644 /tmp/\$f /home/\$u/.claude/persistence/\$f; done; done
  rm /tmp/store.tgz ${py[*]/#//tmp/}
  for s in \$(systemctl list-units --type=service --no-legend 'persistence-store*' | awk '{print \$1}'); do sudo systemctl restart \$s; done
  sleep 5
  systemctl is-active \$(systemctl list-units --type=service --all --no-legend 'persistence-store*' | awk '{print \$1}')"
