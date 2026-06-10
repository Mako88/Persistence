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
  - `python`/`python3`, `node`, `bash`, plus `cd/ls/cat/grep/...` — write and run scripts.
  - `/work` is the persistent workspace (a named volume) — files survive restarts and wake-ups.
- **`searxng`** — keyless metasearch (Google/Bing/DuckDuckGo/Wikipedia/…), JSON enabled, private network only.

## Security model

The container, **not** Persistence's allowlist, is the real boundary (the allowlist permits
interpreters, so it's curation + a tightening hook, not a sandbox). The `computer` service runs
non-privileged, `cap_drop: ALL`, `no-new-privileges`, with pid/memory limits and **no host bind-mounts**
(only the `work` volume). Egress to the internet is allowed (the web tools need it); nothing is
published to the host.

## Run it

```bash
cd container
docker compose up -d --build      # first build pulls Chrome for agent-browser — a few minutes
docker exec persistence-computer web_search "substrate independence" 3   # smoke test
```

Then enable it for Persistence (in `persistence.json`):

```jsonc
"Container": { "Enabled": true, "Name": "persistence-computer" }
```

(or `PERSISTENCE_CONTAINER_ENABLED=true`). The peer can then run `shell(command="web_search \"...\"")`,
`shell(command="agent-browser open https://...")`, write scripts in `/work`, and so on. Tighten or widen
what it may invoke via `Container.Allowlist`.

## Notes

- `agent-browser install` (Chrome download) runs best-effort at image build; if it was flaky, re-run
  `docker exec persistence-computer agent-browser install`.
- Change SearXNG's `secret_key` in `searxng/settings.yml` if you ever expose the instance.
