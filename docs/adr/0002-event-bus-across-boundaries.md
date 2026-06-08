# ADR-0002: Event bus for cross-boundary communication

**Status:** Accepted · **Date:** 2026-06-07 (records an early decision)

## Context
The core produces things a surface must render — replies, thoughts, tool invocations, reasoning
deltas, wake-ups — but the core must not know which surface (if any) is attached, and a turn shouldn't
block on rendering. Some emissions need to be awaited (ordering), others must be non-blocking.

## Decision
Communicate across the core↔surface boundary via an `IEventBus`: `PublishAsync` (awaitable) and
`FireAndForget` (non-blocking), with `Subscribe<T>` returning an unsubscribe handle. Display providers
subscribe to domain events and render; the core never calls a surface directly.

## Alternatives considered
- **Direct `IDisplayProvider` calls from the core** — rejected: couples the core to a display
  abstraction and to call-ordering, and complicates multiple/zero consumers (e.g. SSE + poll).
- **Return values threaded up the stack** — rejected: doesn't fit the streaming/async, multi-emission
  turn loop.

## Consequences
- Surfaces are pluggable consumers; the API layers both poll and SSE on the same event stream.
- Producers stay ignorant of consumers; easy to add observers (logging, debug events).
- Discipline required: handlers run outside locks and must not throw; events are the contract.
