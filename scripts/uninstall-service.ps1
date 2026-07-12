<#
.SYNOPSIS
    Removes the Persistence API Windows Service installed by install-service.ps1. RUN AS ADMINISTRATOR.
#>
param([string]$Name = 'PersistenceApi')

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This must be run as Administrator."
}

if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
    Write-Host "No service named '$Name'."
    return
}

Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
sc.exe delete $Name | Out-Null
Write-Host "Removed service '$Name'."
