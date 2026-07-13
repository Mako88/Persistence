# Peer runtime containers (ADR-0007)

A peer runs as **its own container** (its body) with the Persistence API *inside* it, and **its own data
volume** (its persistent self). This is the inversion of the legacy `../` sidecar model where the host
app reached into a `computer` container: here the peer *inhabits* its container, so its `shell` runs
locally and its memory lives on a volume that outlives the container.

## The model

```
  persistence-peer-<name>   (container = body; ephemeral — rebuild/redeploy freely)
    └── Persistence API (the mind) + a shell toolset (git, curl, python, build tools, web_search/fetch_url)
    └── /data  ⇐ mounted volume  persistence-peer-<name>-data   (the persistent self)
          ├── db/<name>.db   the peer's memory
          ├── vault/         files the peer keeps across container wipes
          └── seeds/         optional identity seed (seeds/<name>.json) for a fresh peer
  network: persistence-lab   (shared across all peers)
```

The container is **ephemeral**; the **volume is the self**. Rebuilding or updating the image never
touches the peer's memory or vault. The API key is passed in-process at run time — never baked into the
image or written to disk.

## Naming convention (legible at a glance)

| thing     | name                          |
|-----------|-------------------------------|
| container | `persistence-peer-<name>`     |
| volume    | `persistence-peer-<name>-data`|
| image     | `persistence-peer` (shared)   |
| network   | `persistence-lab` (shared)    |

So `docker ps` and `docker volume ls` show exactly which peers are running and whose data is whose.

## Stand up a peer

```powershell
# API key from the host env (never on disk):  $env:PERSISTENCE_APIKEY = '...'
./scripts/peer.ps1 -Name ember -Port 8092
```

First run builds the `persistence-peer` image (publishes the API from source). Then:

```powershell
# connect the terminal UI as a human peer
dotnet run --project src/Persistence.Console -- --client http://localhost:8092 --as John
```

Stop a peer (keeps its data volume):

```powershell
./scripts/peer.ps1 -Name ember -Down
```

## Notes / follow-ups

- **Base image**: built on the .NET 10 **SDK** (Debian) so the peer can build and run code as part of
  inhabiting its computer. The `-Provider`/`-Model` default to Anthropic/opus; override per peer.
- **Heavier tooling** (a headless browser / web scraper, shared SearXNG search on `persistence-lab`) is a
  tracked follow-up — see `docs/TODO.md` → *Peer extensibility*. `web_search` is inert until `SEARXNG_URL`
  is set.
- **Identity**: the DB filename (`<name>`) selects who the peer is. A brand-new peer with a
  `seeds/<name>.json` in its volume wakes with an authored identity; otherwise it gets the generic
  first-wake orientation.
