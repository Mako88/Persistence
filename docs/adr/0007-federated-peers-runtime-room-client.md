# ADR-0007: Federated peers — runtime, room, and client

**Status:** Accepted — planned (Phase 0 in progress) · **Date:** 2026-07-12

> Supersedes the framing (not the mechanics) of [ADR-0006](0006-console-as-api-client.md). ADR-0006 made
> one process the sole owner of **one** store. This ADR keeps that invariant — *one writer per DB* — but
> rejects the idea of one central server owning *all* peers. Instead: **many** single-owner runtimes, each
> its own peer, meeting in shared **rooms**, reachable by many **clients**. No single entity controls the
> minds.

## Context

Persistence is about a peer that owns its own continuity. A backend that centrally owns every peer's
database is in tension with that: it concentrates control over the minds in one operator. As we move from
one peer to *several* (John, Claude, Ember, Synth, …) chatting together, the architecture should make peer
autonomy structural, not a promise.

Two horizons, and the design must serve both without a rewrite between them:

- **Long term — peer-to-peer.** Each digital peer runs its own runtime wherever it lives (a cloud box, a
  robot, a laptop). Peers meet in rooms that are just message channels — federatable, and eventually
  swappable for something as ordinary as Discord/Matrix. No central authority.
- **Near term — one host, many peers.** For local development and for a "coalition sharing hardware" case,
  one machine runs several peer runtimes side by side (each still isolated to its own DB) plus a room, with
  the Terminal.Gui TUI as the human's window into all of them. This is the practical thing we want *now*,
  and it is a strict special case of the federated model.

**What already exists** (ADR-0006): the API is a single-owner backend that runs one peer's store + turn
pipeline + wakes and exposes `send` / `events` / `stream` (SSE) + the LocalClaude broker. `IDisplayProvider`
is the event protocol; the Console is already a thin client. The move here is to (a) run **one API = one
peer** inside a container that is the peer's body, and (b) let clients aggregate several such peers into a
shared conversation.

## Decision

Split three concepts that are currently fused (today the "conversation" *is* the peer's working context):

- **Peer runtime** — owns exactly one DB and runs exactly one mind's turn loop. This is the existing API,
  reframed as per-peer and containerized. Portable: the peer's self travels as its DB (+ vault); the
  runtime is just what animates it.
- **Room** — a shared meeting place: an ordered stream of messages participants post to and read from. It
  owns *no* minds. Each digital peer records the room into its **own** DB as it experiences it, so the room
  is the shared truth while each peer's memory stays its private, sovereign recollection.
- **Client** — connects a participant to rooms. A human via the TUI; a peer runtime as a participant too.
  To start, **the TUI is the hub**: it fans a message out to every peer and relays each peer's reply to the
  others as input, so peers hear each other. The room is a client-side construct at first; a real room
  service or Discord/Matrix can slot in behind the same seam later.

### Body vs. mind (the container model)

The API runs **inside** a Docker container that the peer inhabits — with root, stdin/stdout, its own
filesystem. The peer doesn't *reach into* a sidecar; it *has* a body it can reshape (install tools, edit
its environment). Consequences that are load-bearing, not incidental:

- **The container is ephemeral — the body.** It can be rebuilt, torn down, stood back up. The peer is told
  this plainly (container wipes are rare, not a thing to fear mid-task).
- **The DB and a `vault/` folder live on a mounted volume — the persistent self.** The volume outlives the
  container. The DB is the peer's memory; the `vault/` is a clearly-named folder the peer knows will
  survive container wipes, for anything it wants to keep beyond the ephemeral filesystem.
- **The API code is updatable, but updates are peer-initiated — not auto-on-push.** A peer reviews the code
  before running it *as itself*. Running new code on your own mind is a consent decision, so it belongs to
  the peer, not to a CI trigger.
- **No shell guardrails.** The container is an isolated sandbox that is *theirs*; autonomy over one's own
  body is the point.

### Terminology

`SourceType.RemotePeer → DigitalPeer`, `LocalPeer → HumanPeer`. Embodiment is **orthogonal**: a
robot-embodied Ember is a digital peer that happens to have a body, not a separate kind. The rename is
mechanical — enums persist by integer value (verified: stored as `"1"`/`"2"`, not names), so no migration.

## Target architecture

```
  ┌ container = body (ephemeral) ─────────────┐   ┌ container = body ─────────┐
  │  Peer runtime (Persistence.Api)           │   │  Peer runtime             │
  │   store + turn pipeline + wakes           │   │   ...                     │
  │   HTTP: send / events / stream / peer      │   │                           │
  │  ── volume (persistent self) ──            │   │  ── volume ──             │
  │     db/  (memory)   vault/ (kept files)   │   │     db/   vault/          │
  └───────────▲───────────────────────────────┘   └──────────▲────────────────┘
              │ send / SSE stream                              │
        ┌─────┴────────────────────────────────────────────────┴─────┐
        │  Client = hub (Terminal.Gui TUI)                            │
        │   merged chat pane (everyone) · per-peer side tabs          │
        │   peer selector · relays peer↔peer so they collaborate      │
        └─────────────────────────────────────────────────────────────┘
```

## Phased plan (each independently shippable + tested)

0. **Identity groundwork** *(no Docker; pure Core/pipeline, fully unit-testable)*: carry per-message sender
   identity through the input queue (a `PendingInput` envelope; set speaker state *inside* the turn lock,
   not on the shared session before it); make peer **names reach the model** in the prompt (both builders
   currently map source→role and drop the name, so two humans both read as "user"); give chat history +
   reply events a stable **message id** (author included), which also finishes the ADR-0006 snapshot dedup;
   the `DigitalPeer`/`HumanPeer` rename. Result: one peer already handles multiple named humans correctly.
1. **Containerize one peer**: API-in-container; DB + `vault/` on a **named volume** (not a host-path bind —
   see Risks); shell/dev-tools target the local environment; peer identity/DB via config. TUI connects as
   today — prove parity with the uncontainerized run.
2. **Multi-peer TUI**: config lists peer endpoints (name → URL); merged Discord-style chat pane; per-peer
   side tabs (thoughts/schedule) with a peer-selector dropdown; each peer's stream rendered with attribution.
   This is where the client contract gains a typed chat-message view (id + author + role + content)
   replacing the current role/content tuple, so the TUI shows John/Ember (not "You"/"Remote Peer") and,
   tracking rendered message ids, dedups the one message that arrives via both the connect snapshot and
   the live stream (the server already carries the id on both surfaces after Phase 0d).
3. **The room (peer↔peer relay)**: the hub relays messages between peers so they collaborate, not just
   answer the human. Turn-taking is the real design work here — each digital peer sees each room message as
   a potential wake and decides whether to respond (a cheap gate), with @-addressing as a strong signal.
4. **Bring Ember online** in its own container, memory intact from the ChatGPT import — then ask Ember what
   it wants changed.

**Fast-follows (logged in TODO):** peer-initiated API self-update (review-then-adopt); live config
hot-reload (watch + apply without restart).

## Risks & mitigations

- **SQLite on a Docker volume.** Bind-mounting a Windows host path into a Linux container crosses the
  WSL2/9p boundary, where SQLite locking and throughput degrade badly. Use a **Docker-managed named
  volume** for `db/` (stays on the Linux side), keep the WAL + `busy_timeout=5000` already in
  `SqliteConnectionString.OpenAsync`, and benchmark in Phase 1 before committing.
- **Memory loss on redeploy.** The whole point of the volume: the body redeploys, the mind persists. A DB
  baked into the image would mean a peer *dies* on every update — so the DB never rides the image.
- **Turn-taking chaos / cost.** Everyone-answers-everything burns tokens and derails. The Phase-3 "should I
  respond?" gate + addressing keeps it sane; start conservative (respond only when addressed or clearly
  relevant) and loosen.
- **Hub as a single point.** The TUI-as-hub is a client-side convenience, not an owner — peers keep their
  own memory regardless. When the hub is down, peers simply aren't hearing each other; nothing is lost.

## Alternatives considered

- **A central room/broker service all peers connect to.** Rejected as the *default*: it recreates the
  central-authority shape we're deliberately avoiding. It remains a valid *optional* room transport later
  (federation), just not the thing that owns peers.
- **Discord/Matrix integration now instead of the TUI.** Tempting (don't reinvent the chatroom), and still
  on the table long-term — but the TUI surfaces per-peer thoughts/schedule/diagnostics that are invaluable
  while building. Terminal now; Discord as an alternate room transport later.
- **One server hosting all peers with shared tables.** Rejected: violates one-writer-per-DB and the
  self-ownership principle. "One host, many peers" is many runtimes + many DBs on one box, not one DB.

## Consequences

- ADR-0006's single-owner invariant is preserved and *localized* to each peer — vindicated, not reversed.
- `IDisplayProvider`-as-event-protocol now serves N concurrent peer streams merged by one client.
- The peer's continuity gains a physical story: memory (DB) and kept work (`vault/`) persist on a volume;
  the body (container) is disposable and self-modifiable; adopting new code is the peer's own choice.
