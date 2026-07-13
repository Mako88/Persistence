<#
.SYNOPSIS
    Installs the Persistence API server as an always-on Windows Service. RUN AS ADMINISTRATOR.

.DESCRIPTION
    Builds Release, registers a Windows Service that runs the API, and starts it. The service survives
    logout/reboot and owns the store + wakes with no front-end running. Config (persistence.json) is
    found via the assembly directory, so the service's working directory doesn't matter; the database
    directory is pinned to an absolute path here so it doesn't depend on it either.

    To restart with the latest code after changing it:
        Stop-Service PersistenceApi; dotnet build .\Persistence.sln -c Release; Start-Service PersistenceApi
    (For active iteration, prefer scripts/server.ps1 — a lighter background process with a one-liner restart.)

    Uninstall with scripts/uninstall-service.ps1.
#>
param(
    [string]$Name = 'PersistenceApi',
    [string]$Url = 'http://localhost:5000',
    [string]$Model = ''
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This must be run as Administrator (it registers a Windows Service)."
}

$root = Split-Path -Parent $PSScriptRoot

Write-Host "Building Release..."
dotnet build (Join-Path $root 'Persistence.sln') -c Release -v quiet --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$dll = Join-Path $root 'src\Persistence.Api\bin\Release\net10.0\Persistence.Api.dll'
$dotnet = (Get-Command dotnet).Source
$dbDir = Join-Path $root 'dbs'

if (Get-Service -Name $Name -ErrorAction SilentlyContinue) {
    Write-Host "Replacing existing service '$Name'..."
    Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
    sc.exe delete $Name | Out-Null
    Start-Sleep -Seconds 1
}

New-Service -Name $Name `
    -BinaryPathName "`"$dotnet`" `"$dll`"" `
    -DisplayName 'Persistence API' `
    -Description 'Persistence single-owner API server (store + turn pipeline + wakes).' `
    -StartupType Automatic | Out-Null

# Service environment (REG_MULTI_SZ): UiMode must be Api; pin an absolute DB directory; optional model.
$envVars = @("ASPNETCORE_URLS=$Url", "PERSISTENCE_UIMODE=Api", "PERSISTENCE_DATABASEDIRECTORY=$dbDir")
if ($Model) { $envVars += "PERSISTENCE_SELECTEDMODEL=$Model" }
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$Name" -Name Environment -Value $envVars -Type MultiString

Start-Service -Name $Name
Write-Host "Installed and started '$Name' -> $Url (StartupType=Automatic)."
