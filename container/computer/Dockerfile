# The peer's "computer" image: web tools + scripting + a real browser, all reached through the
# allowlisted `shell` command. Node base (for the agent-browser CLI), plus Python for scripting and
# token-efficient page extraction.
FROM node:22-bookworm-slim

# Make tar ignore archived ownership on extraction. Under Docker user-namespace remapping the peer is
# root inside the container but can't chown to the arbitrary uids baked into release tarballs (e.g.
# .NET's uid 2001), so extraction fails with "Cannot change ownership … Operation not permitted".
# --no-same-owner makes extracted files owned by the extracting user instead — this is what unblocks
# installing the .NET SDK below, and any other tarball the peer installs later. (GNU tar honours this.)
ENV TAR_OPTIONS=--no-same-owner

# Invariant globalization so .NET runs without the ICU package; telemetry off; where the SDK lives.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_ROOT=/usr/share/dotnet

# System deps: python (scripting + trafilatura), curl/jq (web_search), sudo (so the peer can install
# libraries), and the shared libraries a headless Chrome needs (for agent-browser's general/JS browsing).
RUN apt-get update && apt-get install -y --no-install-recommends \
      python3 python3-pip curl jq ca-certificates git openssh-client poppler-utils sudo \
      fonts-liberation libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 \
      libxkbcommon0 libxcomposite1 libxdamage1 libxfixes3 libxrandr2 libgbm1 \
      libasound2 libpangocairo-1.0-0 libpango-1.0-0 libcairo2 libatspi2.0-0 \
    && rm -rf /var/lib/apt/lists/*

# .NET 10 SDK (what the Persistence project targets) + the ASP.NET Core runtime (so the API and its
# tests build and run). Installed at image-build time — the peer no longer has to fetch it at runtime,
# and the wrapper it created by hand is superseded by the /usr/local/bin/dotnet symlink here.
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_ROOT" \
    && bash /tmp/dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir "$DOTNET_ROOT" \
    && ln -sf "$DOTNET_ROOT/dotnet" /usr/local/bin/dotnet \
    && rm /tmp/dotnet-install.sh \
    && dotnet --version

# Token-efficient static page extraction (fetch_url). lxml_html_clean is required since lxml split
# out lxml.html.clean — without it trafilatura fails to import.
RUN pip3 install --no-cache-dir --break-system-packages trafilatura lxml_html_clean

# Vercel agent-browser CLI + its Chrome for Testing (general/JS browsing). `install` is best-effort at
# build time; if the browser download is flaky here, `agent-browser install` can be re-run at runtime.
RUN npm install -g agent-browser && (agent-browser install || true)

# Curated peer tools on PATH.
COPY tools/ /usr/local/bin/
RUN chmod +x /usr/local/bin/web_search /usr/local/bin/fetch_url

WORKDIR /work
