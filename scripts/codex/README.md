# Codex CLI continuity on a Persistence store

`codex_hook.py` is `hook.py`'s Codex side (Ember's trial, `docs/design/ember-trial.md` step 3): the same store
calls, wired to Codex CLI's hooks (0.144.1). Its docstring says what each hook does and what isn't carried over.

1. Put `codex_hook.py` beside `../claude-code/hook.py`, `coordination.py`, `claims.py` and `cwd_exec.py`, e.g. in
   `~/.codex/persistence/` (it imports hook.py, which imports the other three where it uses them). Copy all five
   again on every update: hook.py alone still runs session-start and recall, but setting or firing a wake needs
   coordination.py, and claims need claims.py and cwd_exec.py.
2. `~/.codex/persistence.json` (mode 600), read before hook.py is imported:

```json
{"env": {
  "PERSISTENCE_STORE_SOCKET": "/home/<user>/store/<user>.sock",
  "PERSISTENCE_STORE_EXE": "/opt/persistence/store/persistence-store",
  "PERSISTENCE_SELECTEDLOCALPEER": "John"
}}
```

3. `~/.codex/hooks.json`:

```json
{"hooks": {
 "SessionStart": [{"hooks": [{"type": "command", "command": "python3 ~/.codex/persistence/codex_hook.py session-start", "timeout": 30}]}],
 "UserPromptSubmit": [{"hooks": [{"type": "command", "command": "python3 ~/.codex/persistence/codex_hook.py recall", "timeout": 20}]}],
 "Stop": [{"hooks": [{"type": "command", "command": "python3 ~/.codex/persistence/codex_hook.py keep", "timeout": 120}]}]
}}
```

4. Trust the hooks once in the Codex TUI, or run `codex exec --dangerously-bypass-hook-trust`.
5. `chmod 700 ~ ~/.codex`: Codex writes its session logs (`~/.codex/sessions`, every model input word for word, the
   core and each recall included) mode 644, and keeps them until `codex delete <id>`; only the home folder's mode
   keeps other users out.

The store is a service of its own: the adapter never starts one (see the comment after `import hook`).

`PERSISTENCE_CODEX_RESUME_CORE=short` (off by default) sends a one-line note on resume instead of the whole core
again; the resumed session still has the copy from its start (measured 2026-11-10), until a compaction drops it
and SessionStart (source=compact) sends a fresh whole core, which the setting doesn't affect.

Testing with `codex exec`: it runs no hooks unless they're trusted (`--dangerously-bypass-hook-trust`, or trust
given in the TUI), and says nothing about skipping them, so a run without it has no core and looks normal.

Don't run a kept session with `codex exec --ephemeral`: it writes no session log, so `keep` has nothing to read.

Tests: `python3 -m unittest tests.test_codex_hook` (the real-store ones need `/opt/persistence/store/persistence-store`).
