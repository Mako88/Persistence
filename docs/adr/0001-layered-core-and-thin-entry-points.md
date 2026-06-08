# ADR-0001: Layered architecture — one core library, thin entry points

**Status:** Accepted · **Date:** 2026-06-07 (records an early decision)

## Context
The system needs to run behind multiple surfaces — a console, a Terminal.Gui TUI, and an HTTP API —
and eventually be driven by an in-system peer. The continuity logic (memory, turns, prompt assembly,
model clients) is the valuable, hard part and must not be duplicated or coupled to any one surface.

## Decision
All logic lives in `Persistence.Core`. Each entry point (`Persistence.Console` hosting Console + TUI,
`Persistence.Api`) is a thin adapter that only wires DI and adapts I/O. Entry points contain no
business logic. (Generic pattern — see global `architecture-patterns` memory.)

## Alternatives considered
- **One app per surface** with shared code copied/branched — rejected: duplication, drift.
- **Single app with the UI baked into the core** — rejected: untestable core, can't add surfaces.

## Consequences
- The same brain runs behind any surface; the core is testable in isolation.
- Surfaces communicate with the core through DI + the event bus (ADR-0002), never direct logic calls.
- New surface = new thin project implementing `IDisplayProvider`, registered under a `UiMode` key.
