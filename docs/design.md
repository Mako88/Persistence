# Design

> **This document has moved.** The original `design.md` was a planning-era sketch written before
> implementation and had drifted from the code (it predated proposals, the polymorphic tag table, the
> event bus, soft-delete scoping, and more). The living architecture reference now lives in
> **[docs/architecture/](architecture/)**, written to match the code as it actually is.

Start here: **[docs/architecture/README.md](architecture/README.md)**.

The focused docs:

- [Architecture overview](architecture/README.md) — the system, the layering, the top-level map.
- [Turn pipeline](architecture/turn-pipeline.md) — triggers, the turn lock, the
  prompt → model → parse → dispatch → persist loop, concurrency.
- [Prompt & model providers](architecture/prompt-and-model-providers.md) — prompt assembly, the
  sensory block, pluggable providers, the tagged response format, the budget estimate.
- [Memory model](architecture/memory-model.md) — fragments, working contexts, tags, sources,
  proposals, scheduled events, audit/action logs, reversibility.
- [Data layer](architecture/data-layer.md) — the repository pattern, change tracking + audit,
  migrations and seeding.
- [Extensibility](architecture/extensibility.md) — where to add a command, action, provider, or
  surface.
- [Remote peer & surfaces](architecture/remote-peer-and-surfaces.md) — the peer model, the LocalClaude
  broker, the API/TUI surfaces.

For the *why* behind the big choices, see the [ADRs](adr/); for the design posture, see
[governance/PRINCIPLE.md](governance/PRINCIPLE.md); for project-specific patterns in brief, see
[CONVENTIONS.md](CONVENTIONS.md).
