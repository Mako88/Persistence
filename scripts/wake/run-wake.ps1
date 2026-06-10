<#
.SYNOPSIS
  Wake launcher — fires the peer's due scheduled events when the interactive app isn't running.

.DESCRIPTION
  Meant to be run on a schedule (e.g. Windows Task Scheduler every ~15 min). It:
    1. Skips if an interactive Persistence app is already running (it fires its own wakes; running a
       second process against the same store risks memory desync).
    2. Cheaply checks whether any scheduled event is due (a model-free `--check-due` probe). Exits if not.
    3. Brings the local model (gemma / llama-server) up if needed and waits until it's ready.
    4. Runs the headless wake-runner (`--wake-runner`), which fires every due event as an autonomous turn.
    5. Stops the model if (and only if) this script started it, to free the GPU.

  Edit the CONFIG block for your machine. See README.md to register it as a scheduled task.
#>

[CmdletBinding()]
param(
  # Repo root (so persistence.json + dbs/ resolve). Defaults to two levels above this script.
  [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,

  # Built Console DLL to run. Build first: dotnet build -c Debug (or Release, then point here).
  [string]$ConsoleDll = (Join-Path $RepoRoot 'src\Persistence.Console\bin\Debug\net10.0\Persistence.Console.dll'),

  # ---- CONFIG: your local model launch ----
  [string]$LlamaServer = 'llama-server',                                   # path to llama-server(.exe)
  [string]$ModelPath   = 'C:\models\gemma-4-12B-it-q4_k_m.gguf',           # the .gguf to load
  [int]$ModelPort      = 8080,
  [int]$ReadyTimeoutSec = 120                                              # max wait for the model to load
)

$ErrorActionPreference = 'Stop'
$modelUrl = "http://127.0.0.1:$ModelPort/v1/models"

function Test-ModelReady {
  try { (Invoke-WebRequest -Uri $modelUrl -TimeoutSec 3 -UseBasicParsing).StatusCode -eq 200 }
  catch { $false }
}

function Test-InteractiveAppRunning {
  # Any dotnet process running a Persistence front-end that is NOT one of our headless probes.
  $procs = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
    Where-Object {
      $_.CommandLine -and
      ($_.CommandLine -like '*Persistence.Api*' -or $_.CommandLine -like '*Persistence.Console*') -and
      $_.CommandLine -notlike '*--wake-runner*' -and
      $_.CommandLine -notlike '*--check-due*'
    }
  [bool]$procs
}

# 1. Don't race an interactive app — it fires its own wakes.
if (Test-InteractiveAppRunning) {
  Write-Host 'Interactive Persistence app is running; it will handle wakes. Skipping.'
  exit 0
}

# 2. Anything due? (fast, no model)
& dotnet $ConsoleDll --check-due
$dueCode = $LASTEXITCODE
if ($dueCode -eq 100) { Write-Host 'No scheduled events are due. Nothing to do.'; exit 0 }
if ($dueCode -ne 0)   { Write-Error "check-due failed (exit $dueCode)"; exit $dueCode }

# 3. Ensure the model is up; remember whether we started it so we can stop it after.
$startedModel = $false
if (-not (Test-ModelReady)) {
  Write-Host 'Starting the local model...'
  $modelArgs = @(
    '-m', $ModelPath, '-ngl', '99', '-c', '32768', '--parallel', '1',
    '--jinja', '--reasoning-budget', '0', '-t', '4',
    '--host', '127.0.0.1', '--port', "$ModelPort"
  )
  $modelProc = Start-Process -FilePath $LlamaServer -ArgumentList $modelArgs -PassThru -WindowStyle Hidden
  $startedModel = $true

  $deadline = (Get-Date).AddSeconds($ReadyTimeoutSec)
  while (-not (Test-ModelReady)) {
    if ((Get-Date) -gt $deadline) {
      Write-Error "Model did not become ready within $ReadyTimeoutSec s."
      if ($startedModel -and $modelProc -and -not $modelProc.HasExited) { Stop-Process -Id $modelProc.Id -Force }
      exit 1
    }
    Start-Sleep -Seconds 3
  }
  Write-Host 'Model is ready.'
}

# 4. Fire the due wake(s). Run from the repo root so persistence.json + dbs/ resolve.
try {
  Push-Location $RepoRoot
  Write-Host 'Running the wake-runner...'
  & dotnet $ConsoleDll --wake-runner
  $wakeCode = $LASTEXITCODE
}
finally {
  Pop-Location
  # 5. Stop the model only if we started it (leave a pre-existing one alone).
  if ($startedModel -and $modelProc -and -not $modelProc.HasExited) {
    Write-Host 'Stopping the local model (we started it).'
    Stop-Process -Id $modelProc.Id -Force
  }
}

exit $wakeCode
