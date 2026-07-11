# The peer's computer

A sandboxed Docker workspace the peer reaches **only** through Persistence's `shell` command
(`docker exec` into the `computer` container). It gives the peer web search, a real browser, scripting,
and a persistent place to keep files — its "lab."

## What's inside

- **`computer`** — the peer's box. Node + Python + a curated toolset on `PATH`:
  - `web_search "query" [count]` — keyless search via the private SearXNG instance (compact results).
  - `fetch_url <url>` — a page's main content as clean markdown (token-efficient; static/article pages).
  - `agent-browser` — Vercel's CLI for general/JS browsing (`agent-browser open <url>`, `snapshot`,
    `get text @e1`). No API key needed for navigation/extraction.
  - `python`/`python3`, `node`, `bash`, `git`, plus `cd/ls/cat/grep/...` — write and run scripts.
  - **`dotnet`** — the .NET 10 SDK + ASP.NET Core runtime, baked into the image, so the peer can
    `git clone` Persistence and `dotnet build`/`dotnet test` locally. (`.NET` runs in invariant-
    globalization mode, so no ICU package is needed; a hand-rolled `dotnet` wrapper is unnecessary —
    `/usr/local/bin/dotnet` already points at it.)
  - **`sudo`** for installing more libraries. Tarball extraction uses `--no-same-owner` image-wide
    (`TAR_OPTIONS`), so installs no longer fail with the "Cannot change ownership to uid 2001" wall
    that Docker user-namespace remapping causes.
  - `/work` is the persistent workspace (a named volume) — files survive restarts and wake-ups.
- **`searxng`** — keyless metasearch (Google/Bing/DuckDuckGo/Wikipedia/…), JSON enabled, private network only.

## Security model

The container, **not** Persistence's allowlist, is the real boundary (the allowlist permits
interpreters, so it's curation + a tightening hook, not a sandbox). The `computer` service runs
non-privileged, `cap_drop: ALL`, `no-new-privileges`, with pid/memory limits and **no host bind-mounts**
(only the `work` volume). Egress to the internet is allowed (the web tools need it); nothing is
published to the host.

## One computer per participant

Each peer (database) gets its **own** box + workspace volume, so their files never mix. Every computer
shares the one `persistence-computer` image and the `lab` network (so they all reach SearXNG); only the
`container_name` and the `work` volume differ. `docker-compose.yml` ships two: `persistence-computer`
(the shared default) and `persistence-claude-computer` (the `claude.db` peer's box, on its own
`claude-work` volume). Add a service per participant the same way.

Bind a participant to its box with the per-profile **`ContainerName`** in `persistence.json` — it
overrides the shared `Container.Name` while that profile is active:

```jsonc
{ "Name": "claude", "Provider": "LocalClaude", "DatabasePath": "claude.db",
  "ContainerName": "persistence-claude-computer", ... }
```

Peers without a `ContainerName` use the shared `Container.Name`.

To let a participant run **any** program (skip the allowlist curation entirely — the container's own
isolation is still the boundary), set `"ContainerAllowAll": true` on its profile, or
`Container.AllowAllCommands` / `PERSISTENCE_CONTAINER_ALLOWALLCOMMANDS=true` for the shared default.
Handy for leaving a peer working unattended without allowlist rejections blocking it.

## Run it

```bash
cd container
docker compose up -d --build      # first build pulls Chrome for agent-browser — a few minutes
docker exec persistence-claude-computer web_search "substrate independence" 3   # smoke test
```

Then enable the computer for Persistence (in `persistence.json`):

```jsonc
"Container": { "Enabled": true, "Name": "persistence-computer" }
```

(or `PERSISTENCE_CONTAINER_ENABLED=true`). `Container.Name` is the default box; a profile's
`ContainerName` overrides it per participant. The peer can then run `exec(command="web_search \"...\"")`
(or the identical `shell`), `read_file(path="notes.md", offset=0, limit=200)`,
`write_file(path="out.txt", content="...", append=true)`, `agent-browser open https://...`, write scripts
in `/work`, and so on. `exec`/`shell` are gated by `Container.Allowlist`; `read_file`/`write_file` are
first-class file ops that bypass it.

## Notes

- `agent-browser install` (Chrome download) runs best-effort at image build; if it was flaky, re-run
  `docker exec persistence-computer agent-browser install`.
- Change SearXNG's `secret_key` in `searxng/settings.yml` if you ever expose the instance.
