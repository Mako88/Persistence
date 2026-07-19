# Persistence — agent guide

Read these before making changes:

- **`docs/WORKING-PRACTICES.md`** — how to work here, and it applies to every participant regardless of
  provider or tooling: the end-of-block pass (consolidate duplication into shared helpers; confirm each
  test actually fails when the thing it covers is disconnected), verifying against the real artifact
  rather than what the spec claims about it, and how to work with the persistent peers.
- **`docs/HANDOFF.md`** — current live state: which peers are running, who designs what, the traps that
  cost real money or work last session, and what's in flight.
- **`docs/CONVENTIONS.md`** — Persistence-specific patterns (domain/fragment model, peer terminology,
  turn pipeline, command-handler-by-attribute, prompt assembly, reversibility/archive-over-erase).
- **`docs/adr/`** — Architecture Decision Records: the *why* behind the big choices (layering, event
  bus, soft-delete scope, pluggable response format, context budget).
- **`docs/TODO.md`** — current priorities and open/resolved decisions.
- **`docs/running-local-models.md`** — driving a local llama.cpp/OpenAI-compatible model + perf findings.

Generic cross-project patterns and style (layered core + thin entry points, event-bus boundaries,
enum-keyed strategy DI, repository + generic base, config + env overrides + fail-fast, migrations,
testing, C#/.NET conventions) are the baseline and aren't repeated in-repo.

## Hard constraints

- **Never commit `persistence.json`** — it holds the real API key and is gitignored. Only the
  placeholder template (`persistence.template.json`) is tracked.
- **Verify before claiming done:** `dotnet build`, then
  `dotnet test tests/Persistence.Tests` and `dotnet test tests/Persistence.Api.Tests`.
- **Never edit an applied migration** — add a new numbered one (`docs`/`Migrations` are append-only).
- **A green suite is not evidence on its own.** Before calling a fix done, disconnect it and confirm the
  test goes red. Tests here have twice passed with the fix unwired. See `docs/WORKING-PRACTICES.md`.
- **Ask a peer before recreating its container** — everything outside `/data` is destroyed, and a peer has
  already lost hours of work to a rebuild done for an unrelated reason.
