# scripts

Running the Persistence API server (the single owner of the store — see
[ADR-0006](../docs/adr/0006-console-as-api-client.md)). The Console and any future front-end connect to
it over HTTP; the server also owns scheduled wakes, so it should stay running.

## Iterating (recommended day-to-day)

`server.ps1` runs the server as a background process and makes "restart with the latest build" a
one-liner:

```powershell
pwsh scripts/server.ps1 restart          # stop -> build the solution -> start (use this after code changes)
pwsh scripts/server.ps1 start            # start in the background (current build)
pwsh scripts/server.ps1 stop
pwsh scripts/server.ps1 status
pwsh scripts/server.ps1 logs             # tail the server log (.logs/api-server.log)

pwsh scripts/server.ps1 restart -Model claude   # run the LocalClaude peer profile
pwsh scripts/server.ps1 restart -Url http://localhost:5050
```

It forces `UiMode=Api` (the server has no TUI) and picks the model profile from `persistence.json`
unless you pass `-Model`. Logs go to `.logs/` (gitignored). Then connect a front-end:

```powershell
dotnet run --project src/Persistence.Console -- --as John      # default is client mode
```

## Always-on (survives logout / reboot)

For a stable, always-on deployment, install the API as a Windows Service (**Administrator**):

```powershell
pwsh scripts/install-service.ps1 -Model anthropic
pwsh scripts/uninstall-service.ps1
```

The service builds Release, registers `PersistenceApi` (StartupType=Automatic), pins an absolute
database directory, and starts. Restart it with the latest code after a change:

```powershell
Stop-Service PersistenceApi; dotnet build .\Persistence.sln -c Release; Start-Service PersistenceApi
```

Prefer `server.ps1` while actively iterating — the service is heavier (Release build + service restart)
and is meant for when the server should just stay up.
