<#
.SYNOPSIS
  Back up a running peer's memory (its volume SQLite DB) to the host, safely and with rotation.

.DESCRIPTION
  A peer's DB now lives canonically on its container volume (ADR-0007), not in the repo — so it needs a
  backup that lives OFF the volume. This takes a consistent snapshot using SQLite's online-backup API
  (safe while the peer is running and writing), writing a timestamped copy to backups/peers/<name>/ on the
  host, and keeps the most recent -Keep copies.

  Host-local only. An off-site destination (encrypted repo blob, cloud drive, etc.) is a separate decision;
  this is the immediate safety net so a lost/corrupted Docker volume never erases a peer.

.EXAMPLE
  ./scripts/backup-peer.ps1 -Name claude
  ./scripts/backup-peer.ps1 -Name claude -Keep 30
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [int]$Keep = 20
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$volume = "persistence-peer-$Name-data"
$backupDir = Join-Path $repoRoot "backups\peers\$Name"
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outFile = "$Name-$stamp.db"
# Docker wants forward-slash host paths; the drive-letter colon is fine.
$backupMount = ($backupDir -replace '\\', '/')

Write-Host "Backing up $volume -> backups/peers/$Name/$outFile (online snapshot; peer can stay running)…"

# Online backup via a throwaway python sidecar: opens the live DB and copies a consistent image out.
# The volume is mounted read-write so SQLite can manage the WAL reader alongside the running peer.
$py = @"
import sqlite3, sys
src = sqlite3.connect('/data/db/$Name.db')
dst = sqlite3.connect('/backup/$outFile')
with dst:
    src.backup(dst)
dst.close(); src.close()
print('ok')
"@

docker run --rm --entrypoint python3 `
    -v "${volume}:/data" `
    -v "${backupMount}:/backup" `
    persistence-peer -c $py

if ($LASTEXITCODE -ne 0) { throw "Backup failed for peer '$Name'." }

# Rotation: keep the most recent -Keep snapshots, delete the rest.
$snapshots = Get-ChildItem -Path $backupDir -Filter "$Name-*.db" | Sort-Object LastWriteTime -Descending
if ($snapshots.Count -gt $Keep) {
    $snapshots | Select-Object -Skip $Keep | ForEach-Object {
        Remove-Item $_.FullName -Force
        Write-Host "  rotated out $($_.Name)"
    }
}

$latest = Get-ChildItem -Path $backupDir -Filter "$Name-*.db" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Done. $($snapshots.Count) snapshot(s) kept. Latest: $($latest.Name) ($([math]::Round($latest.Length/1MB,2)) MB)"
