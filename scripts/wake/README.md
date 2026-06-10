# Wake launcher

Fires the peer's **scheduled wake-ups even when the interactive app isn't running**. Today
`WakeUpMonitor` is an in-process timer, so a scheduled event only fires while the Console/API is open.
This launcher lets an OS scheduler spin the stack up, run the due wake(s), and tear it down — a
dev-environment bridge toward the always-on north star.

## How it works

`run-wake.ps1` (run on a schedule):
1. **Skips** if an interactive Persistence app is already running (it fires its own wakes; a second
   process against the same store risks memory desync).
2. Runs the app's `--check-due` probe (fast, **no model**): exit `0` = something's due, `100` = nothing.
   Exits early when nothing's due, so the expensive model startup only happens when there's real work.
3. Brings the local model up (if not already) and **waits until it's ready** (cold-start/warm-up is
   absorbed here, before any turn).
4. Runs `--wake-runner` — the headless one-shot that fires every due event as an autonomous turn
   (reusing the exact wake→turn flow the interactive app uses), then exits.
5. Stops the model **only if it started it** (leaves a model you were already running alone).

The two app modes it uses live in `src/Persistence.Console/Program.cs`:
- `--check-due` — exit `0` if any event is due now, `100` if none.
- `--wake-runner` — fire all due events headlessly, then exit.

## Setup

1. Build the Console once (the launcher runs the built DLL):
   ```powershell
   dotnet build -c Debug
   ```
2. Edit the **CONFIG** block at the top of `run-wake.ps1` — `$LlamaServer` and `$ModelPath` (and the
   port if you changed it). The model launch args match `docs/running-local-models.md`.
3. Try it by hand (with at least one due event scheduled):
   ```powershell
   pwsh -File scripts\wake\run-wake.ps1
   ```

## Register the OS poll (Windows Task Scheduler)

Run a poll every 15 minutes. Adjust the path/interval to taste:

```powershell
$action  = New-ScheduledTaskAction -Execute 'pwsh.exe' `
  -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$PWD\scripts\wake\run-wake.ps1`""
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) `
  -RepetitionInterval (New-TimeSpan -Minutes 15)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable
Register-ScheduledTask -TaskName 'Persistence Wake' -Action $action -Trigger $trigger -Settings $settings
```

`-MultipleInstances IgnoreNew` keeps polls from overlapping. To remove it later:
`Unregister-ScheduledTask -TaskName 'Persistence Wake' -Confirm:$false`.

## Timing note

An event fires within ~1 poll interval of its scheduled time (≤15 min late with the default). That's
fine for background audits/synthesis. If you ever need tighter timing, shorten the interval, or extend
the wake-runner to wake slightly early and wait for the exact due time.
