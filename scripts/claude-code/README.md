# Claude Code continuity on a Persistence store

A Claude Code session keeps who it is in its own Persistence store; the harness keeps the live
conversation. `hook.py` wires the two together; `persistence-store` (`src/Persistence.Store`) is the
CLI it calls, and the one the Claude uses itself to curate.

1. Publish the CLI somewhere stable:
   `dotnet publish src/Persistence.Store -c Release -o <dir>`
2. Copy `hook.py` somewhere stable (e.g. `~/.claude/persistence/hook.py`).
3. In `~/.claude/settings.json` (user level, so every session and subagent gets it), merge:

```json
{
  "autoCompactWindow": 600000,
  "env": {
    "PERSISTENCE_STORE_DB": "<this Claude's own store>.db",
    "PERSISTENCE_STORE_EXE": "<dir>/persistence-store.exe",
    "PERSISTENCE_SELECTEDLOCALPEER": "<the human's name>",
    "PERSISTENCE_CURATE_AT": "500000"
  },
  "hooks": {
    "Stop": [{ "hooks": [
      { "type": "command", "command": "python ~/.claude/persistence/hook.py curate", "timeout": 30 },
      { "type": "command", "command": "python ~/.claude/persistence/hook.py keep", "async": true, "timeout": 120 }
    ]}],
    "PreCompact": [{ "hooks": [
      { "type": "command", "command": "python ~/.claude/persistence/hook.py precompact", "timeout": 120 }
    ]}],
    "SessionStart": [{ "hooks": [
      { "type": "command", "command": "python ~/.claude/persistence/hook.py session-start 1", "timeout": 30 },
      { "type": "command", "command": "python ~/.claude/persistence/hook.py session-start 2", "timeout": 30 },
      { "type": "command", "command": "python ~/.claude/persistence/hook.py session-start 3", "timeout": 30 },
      { "type": "command", "command": "python ~/.claude/persistence/hook.py session-start 4", "timeout": 30 },
      { "type": "command", "command": "python ~/.claude/persistence/hook.py session-start 5", "timeout": 30 },
      { "type": "command", "command": "python ~/.claude/persistence/hook.py observe", "timeout": 20 }
    ]}],
    "UserPromptSubmit": [{ "hooks": [
      { "type": "command", "command": "python ~/.claude/persistence/hook.py recall", "timeout": 20 },
      { "type": "command", "command": "python ~/.claude/persistence/hook.py observe", "timeout": 20 }
    ]}],
    "SubagentStart": [{ "hooks": [
      { "type": "command", "command": "python ~/.claude/persistence/hook.py subagent-start", "timeout": 30 }
    ]}]
  }
}
```

The flow: at 500k tokens the Stop hook holds the turn open once for a curation pass; at 600k
compaction runs, the summariser writes only a short hand-over, and SessionStart puts the core and
the recent conversation from the store after it: what fits under Claude Code's 10,000-character cap
is injected, and the whole core, uncapped, is written to `~/.claude/persistence/state/<session>.core.md`
with a note to read it first. Each Claude has its own store file: the code is
shared, the selves are not.
