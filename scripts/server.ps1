<#
.SYNOPSIS
    Dev helper for the Persistence API server (the single owner of the store).

.DESCRIPTION
    Runs the API server in the background so front-ends (the Console client) can connect, and makes it a
    one-liner to restart with the latest build while iterating:

        pwsh scripts/server.ps1 restart      # stop -> build -> start (the common one)
        pwsh scripts/server.ps1 start        # start in the background (latest build as-is)
        pwsh scripts/server.ps1 stop
        pwsh scripts/server.ps1 status
        pwsh scripts/server.ps1 logs         # tail the server log

    The server needs UiMode=Api (forced here). Pass -Model to pick a profile from persistence.json
    (e.g. -Model claude for the LocalClaude peer); otherwise the configured SelectedModel is used.
    For a true always-on service that survives logout/reboot, see scripts/install-service.ps1.
#>
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'restart', 'status', 'logs')]
    [string]$Action = 'status',

    [string]$Url = 'http://localhost:5000',
    [string]$Model = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $root 'src\Persistence.Api\bin\Debug\net10.0\Persistence.Api.dll'
$port = ([Uri]$Url).Port
$logDir = Join-Path $root '.logs'
$log = Join-Path $logDir 'api-server.log'
$errLog = Join-Path $logDir 'api-server.err.log'

function Get-ServerPid {
    (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue).OwningProcess |
        Select-Object -First 1
}

function Stop-Server {
    $serverPid = Get-ServerPid
    if ($serverPid) {
        Stop-Process -Id $serverPid -Force
        Start-Sleep -Milliseconds 500
        Write-Host "Stopped server (pid $serverPid)."
    }
    else {
        Write-Host "No server listening on port $port."
    }
}

function Start-Server {
    if (Get-ServerPid) { Write-Host "Server already running on port $port."; return }
    if (-not (Test-Path $dll)) { throw "Not built yet: $dll  (run: pwsh scripts/server.ps1 restart)" }

    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $env:ASPNETCORE_URLS = $Url
    $env:PERSISTENCE_UIMODE = 'Api'                       # the server has no TUI
    if ($Model) { $env:PERSISTENCE_SELECTEDMODEL = $Model }

    Start-Process dotnet -ArgumentList "`"$dll`"" `
        -RedirectStandardOutput $log -RedirectStandardError $errLog -WindowStyle Hidden | Out-Null

    Start-Sleep -Seconds 1
    $serverPid = Get-ServerPid
    Write-Host "Started API server -> $Url  (pid $($serverPid ?? '?'); logs: $log)"
}

switch ($Action) {
    'start' { Start-Server }
    'stop' { Stop-Server }
    'restart' {
        Stop-Server
        Write-Host "Building (latest changes)..."
        dotnet build (Join-Path $root 'Persistence.sln') -v quiet --nologo
        if ($LASTEXITCODE -ne 0) { throw "Build failed - not restarting." }
        Start-Server
    }
    'status' {
        $serverPid = Get-ServerPid
        if ($serverPid) { Write-Host "Running (pid $serverPid) on $Url." }
        else { Write-Host "Not running." }
    }
    'logs' {
        if (Test-Path $log) { Get-Content $log -Tail 40 } else { Write-Host "No log yet at $log." }
    }
}
