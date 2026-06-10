# The peer's "computer" image: web tools + scripting + a real browser, all reached through the
# allowlisted `shell` command. Node base (for the agent-browser CLI), plus Python for scripting and
# token-efficient page extraction.
FROM node:22-bookworm-slim

# System deps: python (scripting + trafilatura), curl/jq (web_search), and the shared libraries a
# headless Chrome needs (for agent-browser's general/JS browsing).
RUN apt-get update && apt-get install -y --no-install-recommends \
      python3 python3-pip curl jq ca-certificates git poppler-utils \
      fonts-liberation libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 \
      libxkbcommon0 libxcomposite1 libxdamage1 libxfixes3 libxrandr2 libgbm1 \
      libasound2 libpangocairo-1.0-0 libpango-1.0-0 libcairo2 libatspi2.0-0 \
    && rm -rf /var/lib/apt/lists/*

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
