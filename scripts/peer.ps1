<#
.SYNOPSIS
  Stand up (or tear down) a Persistence peer as its own container + data volume (ADR-0007).

.DESCRIPTION
  Each peer runs its own runtime container (its body) with the API inside it, and its own data volume
  (its persistent self: db/ + vault/). This wraps `docker compose` so one peer is one command. All peers
  share the one `persistence` Compose project (grouping under it in Docker Desktop, next to the shared
  infra), each as its own service peer-<name>: container persistence-peer-<name>, volume
  persistence-peer-<name>-data, on the shared persistence-lab network.

  Who a peer IS — provider/model/key, budgets, cost limits, identity DB — lives in its own config file at
  container/peer/configs/<name>.json (gitignored; it holds the API key). The file is mounted into the
  container and the app hot-reloads it on edit — so tweak the file, no restart needed.

.EXAMPLE
  ./scripts/peer.ps1 -Name ember -Port 8092
  # builds the image (first run), starts persistence-peer-ember reading container/peer/configs/ember.json

.EXAMPLE
  ./scripts/peer.ps1 -Name ember -Port 8092 -Down    # stop + remove the container (keeps the volume)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [int]$Port,
    [switch]$Down,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$baseCompose = Join-Path $PSScriptRoot '..\container\peer\docker-compose.yml'

# All peers share the one 'persistence' Compose project, so they group under it in Docker Desktop
# alongside the shared infra (computer/searxng). Compose converges services by name within a project, so
# each peer needs a UNIQUE service name — a single shared 'peer' service would clobber the previous peer.
# So we render a per-peer compose file (service 'peer-<name>') from the base template. It's written next
# to the base so its relative build paths still resolve, and is gitignored.
$env:COMPOSE_PROJECT_NAME = 'persistence'
# Each peer's compose file defines only its own service, so the shared-infra + other-peer containers in
# the project look like "orphans" to it. They're not — don't offer to remove them.
$env:COMPOSE_IGNORE_ORPHANS = 'true'
$env:PEER_NAME = $Name

$composeFile = Join-Path $PSScriptRoot "..\container\peer\docker-compose.$Name.generated.yml"
(Get-Content -Raw $baseCompose) -replace '(?m)^(\s{2})peer:\s*$', ('$1peer-' + $Name + ':') |
    Set-Content -Path $composeFile -Encoding utf8

if ($Down) {
    # Stop THIS peer only, by container name.
    #
    # Emphatically not `docker compose down`: that is scoped to the *project*, and every peer shares the
    # one `persistence` project — so `down` on a single peer's compose file tears down every other peer
    # AND the shared infra (computer, searxng) with it. Found the hard way. Volumes survive, so it's
    # recoverable, but it takes the whole lab offline when you asked for one peer.
    docker stop "persistence-peer-$Name" *> $null
    docker rm "persistence-peer-$Name" *> $null
    Remove-Item $composeFile -ErrorAction SilentlyContinue
    Write-Host "Stopped persistence-peer-$Name (its data volume persistence-peer-$Name-data is kept)."
    return
}

if (-not $Port) { throw 'Port is required when starting a peer (e.g. -Port 8092).' }
$env:PEER_PORT = "$Port"

# Each peer reads its own config file (provider/model/key/budgets). Make sure it exists before starting.
$configFile = Join-Path $PSScriptRoot "..\container\peer\configs\$Name.json"
if (-not (Test-Path $configFile)) {
    throw "Missing config for '$Name': $configFile. Create it (see the other configs for the shape) - it holds the provider/model/API key."
}

# Ensure the shared external lab network exists (the peer compose attaches to it, doesn't create it).
docker network inspect persistence-lab *> $null
if ($LASTEXITCODE -ne 0) { docker network create persistence-lab | Out-Null }

if ($NoBuild) {
    docker compose -f $composeFile up -d
} else {
    docker compose -f $composeFile up -d --build
}

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Peer '$Name' is up:  project persistence | service peer-$Name | container persistence-peer-$Name"
    Write-Host "Config:   container/peer/configs/$Name.json  (edit it - hot-reloads, no restart)"
    Write-Host "API:      http://localhost:$Port"
    Write-Host "1:1 TUI:  dotnet run --project src/Persistence.Console -- --client http://localhost:$Port --as John"
    Write-Host "Hub:      add this peer to HubPeers in persistence.json, then launch the 'hub' profile"
    Write-Host "          (dotnet run --project src/Persistence.Console --launch-profile hub)"
}
