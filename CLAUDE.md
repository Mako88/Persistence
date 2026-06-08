# Persistence — agent guide

Read these before making changes:

- **`docs/CONVENTIONS.md`** — Persistence-specific patterns (domain/fragment model, peer terminology,
  turn pipeline, command-handler-by-attribute, prompt assembly, reversibility/archive-over-erase).
- **`docs/adr/`** — Architecture Decision Records: the *why* behind the big choices (layering, event
  bus, soft-delete scope, pluggable response format, context budget).
- **`docs/TODO.md`** — current priorities and open/resolved decisions.

Generic cross-project patterns and style (layered core + thin entry points, event-bus boundaries,
enum-keyed strategy DI, repository + generic base, config + env overrides + fail-fast, migrations,
testing, C#/.NET conventions) are the baseline and aren't repeated in-repo.

## Hard constraints

- **Never commit `persistence.json`** — it holds the real API key and is gitignored. Only the
  placeholder template (`persistence.template.json`) is tracked.
- **Verify before claiming done:** `dotnet build`, then
  `dotnet test tests/Persistence.Tests` and `dotnet test tests/Persistence.Api.Tests`.
- **Never edit an applied migration** — add a new numbered one (`docs`/`Migrations` are append-only).
