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
      { "type": "http", "url": "http://127.0.0.1:8097/stop-cue", "timeout": 10 },
      { "type": "command", "command": "python ~/.claude/persistence/hook.py curate", "timeout": 30 },
      { "type": "command", "command": "python ~/.claude/persistence/hook.py keep", "async": true, "timeout": 120 },
      { "type": "command", "command": "python ~/.claude/persistence/hook.py waiter", "async": true, "asyncRewake": true, "timeout": 3600 }
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
      { "type": "http", "url": "http://127.0.0.1:8097/recall", "timeout": 20 },
      { "type": "http", "url": "http://127.0.0.1:8097/observe", "timeout": 20 }
    ]}],
    "PreToolUse": [{ "matcher": "*", "hooks": [
      { "type": "http", "url": "http://127.0.0.1:8097/tool-cue", "timeout": 5 }
    ]}],
    "SubagentStart": [{ "hooks": [
      { "type": "command", "command": "python ~/.claude/persistence/hook.py subagent-start", "timeout": 30 }
    ]}]
  }
}
```

The `http` hooks are answered by `hook.py serve`, a small server the `curate` and `keep` hooks start
when nothing listens on port 8097 (`PERSISTENCE_HOOK_PORT`), so nothing starts per message and a
recall costs the store's time alone. Any of them can be a `command` hook instead
(`python ~/.claude/persistence/hook.py recall`), at the cost of a Python start per call; the server
is needed only for `waiter`, the Stop hook that lets the Claude wake itself (`python hook.py wake 30m
<reason>`, or `wake pid:<N> <reason>` for when a process ends).

What the per-message hooks bring, each only when it has something:

- `recall`: fragments associated with the message, and those a `cue/prompt/<name>` cue's pattern
  matches (`persistence-store cue set prompt/<name> --when <regex>`).
- `tool-cue`, before every tool call: `cue/tool/<name>` cues matched against `Tool: {input}` (a
  reminder at the moment a habit acts), and, on a Write or Edit that declares a function or type,
  what the project already has under that name or one made of the same words. That comes from a
  per-project symbol index at `~/.claude/persistence/state/symbols/<project>.json`: one `git grep`
  over the working tree finds every definition (C#, Python, JavaScript/TypeScript, PowerShell), a
  token index over the names answers a query without reading them all, and each call refreshes only
  the files that moved since (`git diff` between the commit then and now, `git status` for the
  working tree). A 20,000-file repository indexes in about 7 s, by a detached process the first
  time, and costs about a second a Write after that; a 260-file one, half a second.
- `stop-cue`: `cue/stop/<name>` cues matched against the turn's last message; the turn is held once
  when one matches (a promise of work with nothing started).


The flow: at 500k tokens the Stop hook holds the turn open once for a curation pass; at 600k
compaction runs, the summariser writes only a short hand-over, and SessionStart puts the core and
the recent conversation from the store after it: what fits under Claude Code's 10,000-character cap
is injected, and the whole core, uncapped, is written to `~/.claude/persistence/state/<session>.core.md`
with a note to read it first. Each Claude has its own store file: the code is
shared, the selves are not.
