<#
.SYNOPSIS
  Stand up (or tear down) a Persistence peer as its own container + data volume (ADR-0007).

.DESCRIPTION
  Each peer runs its own runtime container (its body) with the API inside it, and its own data volume
  (its persistent self: db/ + vault/). This wraps `docker compose` so one peer is one command, with the
  legible naming convention: container persistence-peer-<name>, volume persistence-peer-<name>-data, all
  on the shared persistence-lab network.

  The model API key is read from the -ApiKey parameter or the host's PERSISTENCE_APIKEY environment
  variable and passed to the container in-process — it is never written to disk or baked into the image.

.EXAMPLE
  ./scripts/peer.ps1 -Name ember -Port 8092
  # builds the image (first run), starts persistence-peer-ember, API at http://localhost:8092

.EXAMPLE
  ./scripts/peer.ps1 -Name ember -Port 8092 -Provider OpenAI -Model gpt-5.4 -ApiKey $env:OPENAI_KEY
  # Ember's substrate: OpenAI gpt-5.4 (streaming is on by default). -ApiKey must be the OpenAI key.

.EXAMPLE
  ./scripts/peer.ps1 -Name ember -Port 8092 -Down    # stop + remove the container (keeps the volume)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [int]$Port,
    [string]$ApiKey,
    [string]$Provider = 'Anthropic',
    [string]$Model = 'claude-opus-4-8',
    [switch]$Down,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$composeFile = Join-Path $PSScriptRoot '..\container\peer\docker-compose.yml'

# One compose project per peer → isolated container/volume names, shared external-style lab network.
$env:COMPOSE_PROJECT_NAME = "persistence-$Name"
$env:PEER_NAME = $Name

if ($Down) {
    docker compose -f $composeFile down
    Write-Host "Stopped persistence-peer-$Name (its data volume persistence-peer-$Name-data is kept)."
    return
}

if (-not $Port) { throw 'Port is required when starting a peer (e.g. -Port 8092).' }
$env:PEER_PORT = "$Port"

$key = if ($ApiKey) { $ApiKey } else { $env:PERSISTENCE_APIKEY }
if (-not $key) {
    Write-Warning 'No API key supplied (-ApiKey or PERSISTENCE_APIKEY). Cloud model calls will fail until one is set.'
}
$env:PERSISTENCE_APIKEY = $key
$env:PERSISTENCE_PROVIDER = $Provider
$env:PERSISTENCE_MODEL = $Model

$buildArg = if ($NoBuild) { @() } else { @('--build') }
docker compose -f $composeFile up -d @buildArg

# Don't leave the key sitting in this shell's environment after we've handed it to compose.
$env:PERSISTENCE_APIKEY = $null

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Peer '$Name' is up:  container persistence-peer-$Name  ·  volume persistence-peer-$Name-data"
    Write-Host "API:    http://localhost:$Port"
    Write-Host "Connect the TUI:   dotnet run --project src/Persistence.Console -- --client http://localhost:$Port --as John"
}
