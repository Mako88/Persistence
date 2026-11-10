#!/bin/sh
# Ember's room bridge on John's PC (Docker Desktop). Ember's app is persistence-peer-ember on the host's port 8092.
# One-time: the volume holds config.json and token (600); see README "A self served over HTTP".
set -e
here=$(cd "$(dirname "$0")/.." && pwd)
docker build -t persistence-room -f "$here/docker/Dockerfile" "$here"
docker rm -f persistence-room-ember 2>/dev/null || true
docker run -d --name persistence-room-ember --restart unless-stopped \
  --add-host host.docker.internal:host-gateway \
  -v persistence-room-ember:/root/.config/persistence-room \
  -v "$here:/app:ro" \
  persistence-room
