# The peer-runtime image (ADR-0007): the peer's *mind* (the Persistence API) and its *body* (a shell
# with a curated toolset) in one container. Unlike the legacy `computer` sidecar the host reached into,
# here the API runs inside the container the peer inhabits — so its `shell` runs locally (no docker exec),
# and its memory (the SQLite DB) + a persistent `vault/` live on a mounted volume, not in this image.
#
# The container is ephemeral (rebuild/redeploy freely); the volume is the persistent self. Nothing
# durable is baked in here.

# ── build: publish the API ────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the project graph first for layer caching, then publish.
COPY . .
RUN dotnet publish src/Persistence.Api/Persistence.Api.csproj -c Release -o /app

# ── runtime: the peer's body + mind ─────────────────────────────────────────────────────────────────
# The SDK base (not aspnet-only) so the peer can build and run code as part of inhabiting its computer.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# A curated default toolset for the peer's shell: version control, HTTP + JSON, scripting, and the
# build essentials so it can actually write and compile things. Heavier tools (a headless browser /
# web scraper) are a tracked follow-up — see docs/TODO.md "Peer extensibility".
RUN apt-get update && apt-get install -y --no-install-recommends \
      git curl jq ca-certificates sudo \
      python3 python3-pip \
      build-essential \
      poppler-utils \
    && rm -rf /var/lib/apt/lists/*

# Token-efficient static page extraction for the fetch_url tool (matches the computer image).
RUN pip3 install --no-cache-dir --break-system-packages trafilatura lxml_html_clean

# Curated shell tools on PATH (web_search / fetch_url), shared with the computer image.
COPY container/computer/tools/ /usr/local/bin/
RUN chmod +x /usr/local/bin/web_search /usr/local/bin/fetch_url

# The peer's mind.
COPY --from=build /app /opt/persistence

# The peer inhabits this container: shell runs locally here, memory + vault live on the /data volume.
ENV PERSISTENCE_UIMODE=Api \
    PERSISTENCE_CONTAINER_ENABLED=true \
    PERSISTENCE_CONTAINER_LOCAL=true \
    PERSISTENCE_CONTAINER_ALLOWALLCOMMANDS=true \
    PERSISTENCE_CONTAINER_WORKINGDIR=/data/vault \
    ASPNETCORE_URLS=http://+:8080

# The volume mounts here at runtime; create the tree so a fresh peer has its home even before first write.
RUN mkdir -p /data/db /data/vault /data/seeds
WORKDIR /data/vault

EXPOSE 8080
ENTRYPOINT ["dotnet", "/opt/persistence/Persistence.Api.dll"]
