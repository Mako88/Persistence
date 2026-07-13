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
| project   | `persistence` (shared with the computer/searxng infra — all peers group under it in Docker Desktop) |
| service   | `peer-<name>` (rendered per peer by `peer.ps1` from the compose template) |
| container | `persistence-peer-<name>`     |
| volume    | `persistence-peer-<name>-data`|
| image     | `persistence-peer` (shared)   |
| network   | `persistence-lab` (shared)    |

All peers share the one **`persistence`** Compose project, so `docker ps` / Docker Desktop group them
under it next to the shared infra; each is its own **service `peer-<name>`** (Compose converges services
by name, so a shared service would clobber the previous peer). `peer.ps1` renders a gitignored per-peer
compose file (`docker-compose.<name>.generated.yml`) from the template and runs it under that project.

## Stand up a peer

```powershell
# Anthropic peer (the -Provider/-Model default), key from the host env (never on disk):
$env:PERSISTENCE_APIKEY = '<anthropic key>'
./scripts/peer.ps1 -Name arden -Port 8091

# OpenAI peer — e.g. Ember on gpt-5.4 (streaming is on by default). -ApiKey must be the OpenAI key:
./scripts/peer.ps1 -Name ember -Port 8092 -Provider OpenAI -Model gpt-5.4 -ApiKey '<openai key>'
```

First run builds the `persistence-peer` image (publishes the API from source). Connect a 1:1 terminal:

```powershell
dotnet run --project src/Persistence.Console -- --client http://localhost:8092 --as John
```

Stop a peer (keeps its data volume):

```powershell
./scripts/peer.ps1 -Name ember -Down
```

## Watch several peers at once — the hub (ADR-0007 Phase 2b)

The Console can aggregate several peers into one **hub**: all their messages in one attributed,
colour-coded scrollback, with a selector (**click or F6**) choosing which peer the side panes
(thoughts / actions / schedule / debug) and status show. Peers don't hear *each other* yet — that relay
is the room (ADR-0008), built later — but one human can watch and talk to all of them.

1. Stand each peer up on its own port (above).
2. List them in `persistence.json` (gitignored) so the hub knows where they are:
   ```json
   "HubPeers": [
     { "Name": "Arden", "BaseUrl": "http://localhost:8091", "LocalPeer": "John" },
     { "Name": "Ember", "BaseUrl": "http://localhost:8092", "LocalPeer": "John" }
   ]
   ```
   (Or pass them on the CLI: `--peer Arden=http://localhost:8091 --peer Ember=http://localhost:8092`.)
3. Launch the hub — the `hub` run profile does this:
   ```powershell
   dotnet run --project src/Persistence.Console --launch-profile hub
   ```

## Migrating already-running peers into the `persistence` project

Peers stood up by an earlier `peer.ps1` ran under a per-peer project (or none), so they float outside
`persistence` in Docker Desktop. To move them in, remove and re-stand them — the **volume is the self**,
so memory is preserved (it's keyed by name, not by the container):

```powershell
docker rm -f persistence-peer-ember          # data volume persistence-peer-ember-data is untouched
./scripts/peer.ps1 -Name ember -Port 8092 -Provider OpenAI -Model gpt-5.4 -ApiKey '<openai key>'
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
