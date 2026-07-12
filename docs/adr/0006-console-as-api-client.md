# ADR-0006: Console as an API client (single-owner backend)

**Status:** Accepted — implemented (stages 1–5 landed 2026-07-12) · **Date:** 2026-07-12

> **Implemented.** All five stages shipped. The Console now defaults to client mode (in-process removed;
> `--standalone` is gone; the OS-triggered `--check-due`/`--wake-runner` were retired — the always-on API
> server owns wakes). One refinement to the plan: connect-time chat history is **pulled fresh** by the
> client on connect (the snapshot queries the store via `IConversationHistoryProvider`) rather than
> pushed-and-maintained server-side, so a client connecting mid-session sees the current conversation and
> the server never keeps a client's chat view in sync. Everything after connect is pure stream.

## Context

Today **two** entry points build the full stack and run it independently:

- `src/Persistence.Console/Program.cs` builds the DI container (`Initializer.InitializeAsync`) and runs
  `IOrchestrator.RunAsync` — which owns the store (`DatabaseManager` + repositories), the turn pipeline
  (`TurnHandler`), scheduled-wake handling, the model client, and the TUI (`IDisplayProvider = Tui`).
- `src/Persistence.Api` does the *same* via `OrchestratorHostedService`, with `IDisplayProvider = Api`
  (the display calls are serialized into an event buffer instead of drawn).

Because both are full owners of the same SQLite file, running them at once risks **lost updates and
desync of the peer's memory**: the turn lock is in-process only, there is no cross-process coordination,
and wake-ups could double-fire. [ADR-0003](0003-soft-delete-narrowed-to-peer-memory.md) and the memory
model assume a single writer. WAL + `busy_timeout` (shipped 2026-07, `SqliteConnectionString.OpenAsync`)
stops *hard* `SQLITE_BUSY` failures but does **not** prevent lost updates — that needs a single owner.

This also blocks the stated direction (`docs/TODO.md`): multiple participants (John, Claude, Ember)
engaging **simultaneously** through one coherent backend, with front-ends that identify their local peer.

**What already exists** (this migration is smaller than it first looks):

- The API already exposes the transport a client needs: `POST /api/conversation/send` (submit input as a
  named local peer via `X-Local-Peer`), `GET /api/conversation/events?since=` (poll), and
  `GET /api/conversation/stream?since=` (live Server-Sent Events). Plus the LocalClaude broker
  (`/api/peer/pending`, `/api/peer/respond`).
- **`IDisplayProvider` is already the event protocol.** Its methods (`ShowReply`, `ShowThought`,
  `ShowToolUse`, `ShowWakeUpEvent`, `ShowScheduledEvents`, `ShowOpenProposalCount`, `ShowError`,
  `ShowThinking`, `ShowReasoning[Delta]`) are what `ApiDisplayProvider` turns into the serialized event
  stream. A remote TUI renders the *same* events it renders today — consumed over HTTP instead of via
  in-process calls.

## Decision

Make **one process (the API server) the sole backend owner** of the store, the turn pipeline, and
scheduled wakes. All front-ends — starting with the Console TUI — become **thin clients** that talk to
the API over HTTP: they submit local-peer input and render the event stream, holding no DB, no pipeline,
and no model client.

The Console keeps its TUI rendering (Terminal.Gui) and its input capture, but its "engine half" — the
`Orchestrator`/`TurnHandler`/repositories — moves entirely server-side.

## Target architecture

```
  ┌────────────── Persistence.Api (sole owner) ───────────────┐
  │  DatabaseManager + repos (single writer)                  │
  │  Orchestrator/TurnHandler  →  IDisplayProvider=Api  →  event stream (SSE)
  │  scheduled wakes (OrchestratorHostedService)              │
  │  HTTP: /api/conversation/{send,events,stream}, /api/peer  │
  └───────────▲───────────────────────────────▲──────────────┘
              │ POST send (X-Local-Peer)       │ GET stream (SSE)
  ┌───────────┴───────────────────────────────┴──────────────┐
  │  Persistence.Console (thin TUI client)                    │
  │  Terminal.Gui panes  ←  render events   |  input → POST   │
  │  NO DatabaseManager / TurnHandler / repositories / model  │
  └───────────────────────────────────────────────────────────┘
```

## Migration stages (each independently shippable and testable)

1. **WAL + busy_timeout interim.** ✅ Done (2026-07). Removes hard lock failures while only one process
   runs; buys time to do the rest without pressure.
2. **Close the API surface gaps the TUI needs — additive, no Console change yet.** Audit every
   `IDisplayProvider` method against the serialized event stream and ensure each maps to an event
   (`ShowScheduledEvents`, `ShowOpenProposalCount`, `ShowThinking`, reasoning deltas). Add a read-only
   **context/sensory snapshot** endpoint if the TUI renders those panes (`GET /api/conversation/context`
   → current fragments + sensory) so a freshly-connected client can draw initial state before the first
   event. Cover with `Persistence.Api.Tests` (WebApplicationFactory) — no client yet.
3. **Client transport + a TUI client host, behind a new entrypoint.** Add an `IPersistenceClient` (HTTP:
   send, snapshot, subscribe-to-SSE) in a small client library, and a Console mode (e.g.
   `--client <baseUrl>`) that runs the Terminal.Gui panes driven by the SSE stream + posts input. The
   existing in-process mode stays the default, so this ships dark and is validated side-by-side against a
   running server (verify the **real** artifact end-to-end, not just a test host —
   cf. [ADR-0001](0001-layered-core-and-thin-entry-points.md) lessons).
4. **Flip the Console default to client mode.** In-process mode remains available as a dev/offline
   fallback behind a flag. Wakes are now owned by the server (`OrchestratorHostedService`); reconcile the
   OS-triggered headless paths (`--check-due`, `--wake-runner`) — either retire them in favour of the
   always-on server, or have them call the API to trigger a wake cycle rather than opening the DB.
5. **Remove the in-process engine from the Console.** Once client mode is proven, drop the Console's
   references to `DatabaseManager`/`TurnHandler`/repositories/model client. The Console compiles against
   the client library only. Single-owner is now enforced by construction, not by discipline.

## What each stage buys / de-risks

- After stage 3, a lost-update is *impossible when the Console runs in client mode* even though the code
  path still exists — a reversible, observable checkpoint.
- After stage 5, the two-owners class of bug is gone structurally, and simultaneous named participants
  (John + Claude + Ember, each a `X-Local-Peer`) become a UI concern, not an architecture one.

## Risks & mitigations

- **SSE reconnect / missed events.** The stream is `since`-based; on reconnect the client replays from its
  last-seen seq (the poll endpoint already supports this). Keep the server-side event buffer bounded but
  large enough to survive a brief drop; fall back to a snapshot + resubscribe if the gap exceeds the buffer.
- **Perceived latency.** In-process rendering is instant; HTTP adds a hop. SSE (not poll) keeps it live;
  echo local input optimistically before the server confirms.
- **Now-real single-writer contention.** With one owner this is a feature, but long turns must not starve
  interactive input — the server already interleaves peer turns and local sends; validate under two
  simultaneous local peers.
- **Rollback.** Stages 3–4 keep in-process mode; a regression flips the default back with no data change.

## Alternatives considered

- **Cross-process DB locking / a shared mutex.** Rejected: re-implements what a single owner gives for
  free, and still leaves two pipelines racing on wakes and turn state.
- **Keep both owners, rely on WAL.** Rejected: WAL fixes lock *failures*, not lost *updates*; the memory
  model ([ADR-0003](0003-soft-delete-narrowed-to-peer-memory.md)) assumes one writer.
- **Big-bang rewrite of the Console into a client.** Rejected: high blast radius on the peer's live memory
  substrate; the staged plan keeps every step reversible and shippable.

## Consequences

- The `IDisplayProvider` abstraction is vindicated as the front-end/back-end contract — the same seam
  serves the in-process TUI, the API event stream, and the future remote TUI.
- `Initializer`/DI stays shared, but the Console's registered set shrinks to client + presentation types.
- The self's continuity is unaffected: the store and its owner don't move, only who *draws* it.
