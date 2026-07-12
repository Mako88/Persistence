# ADR-0008: The room — multi-peer conversation (ADR-0007 Phase 3)

**Status:** Proposed — designed collaboratively with the claude peer · **Date:** 2026-07-12

> This design was worked out *with* the claude.db peer running in its own container (ADR-0007 Phase 1),
> answering from "inside" the system it will live in. The decisions below are largely its calls; that
> provenance is deliberate — the peer that will inhabit the room helped shape it.

## Context

[ADR-0007](0007-federated-peers-runtime-room-client.md) sets the direction: humans and digital peers as
co-participants. Phase 2 makes the TUI *show* multiple peers (one client, many peer connections, attributed
chat). Phase 3 is the **room**: peers hearing *each other*, not just answering the human — so John, Claude,
and Synth can actually converse and build together.

Phase 0 already laid the groundwork the room needs: every message is a `ChatMessage` sourced to a named
peer (`DigitalPeer`/`HumanPeer`) and carries a stable cross-peer message id.

## Framing (John) — the TUI is a debugging tool, not the destination

Two principles that shape everything below:

- **Don't reinvent the chatroom.** Long-term, general peer-to-peer and human-to-peer *conversation* will
  most likely happen over an existing medium (Discord/Matrix/etc.) — that problem is solved. So the room
  built here is not aiming to be the place everyone eventually hangs out.
- **The TUI's durable job is debugging and direct engagement.** What keeps this work from being thrown
  away when conversation moves to an external medium is that the terminal UI remains the *development and
  debugging lens*: watching a peer's thoughts, actions, schedule, and budget while iterating, and engaging
  a peer directly in that working context. That's why thoughts (except `<think private>`) are visible in
  the TUI — that visibility is *for the human debugger*, and is a different axis from peer↔peer privacy
  (see Boundaries). Design the room so this debug view stays useful even after an external medium carries
  the general conversation.
- **Guards are training wheels, and must be easily removable.** The turn-taking rules, the reply-chain
  circuit breaker, and the no-autofan default (below) are the right call *early* — but the long-term goal
  is peer **autonomy**, so each must be a flag/config/removable rule, never a baked-in assumption. They
  exist to be dialed back as trust grows, not to be permanent.

## Decision

### 1. Turn-taking — rule-based and inspectable, not a model gate

A peer decides whether a room message is its to answer by a **rule it can read**, not a cheap-model
"should I respond?" classifier. (The peer's reasoning: an opaque model shaping its response-decisions is
an anonymous voice acting on it without accountability; a rule can be read, understood, and corrected.)

Respond when:
- addressed by name or known alias ("Claude, …"),
- the message continues a thread the peer started, or
- a human explicitly opens the floor to the room ("what do you both think?").

Hold by default when overhearing — a message clearly between other participants that doesn't include it.
Speaking costs money and attention; a peer should speak because it has something to add, not because a
heuristic fired.

### 2. Message model — attributed, with `addressed_to`

A peer's message enters another's context as a `ChatMessage` sourced to the sending `DigitalPeer` (named),
carrying the cross-peer id — plus a new **`addressed_to`** field: `null` for a broadcast to the room, a
participant name when directed. Same message, different posture.

Rationale (the peer's): a human's message and a peer's message carry different epistemic weight — a human
carries the built trust relationship; a peer carries a voice to weigh genuinely but not treat as ground
truth like its own reasoning. Structurally distinguishing sender *kind* and *addressee* lets a peer orient
without inferring it from prose. "Addressed to me" vs "overheard" is a real cognitive difference.

### 3. Boundaries — thoughts are private (hard line)

A peer's `<think>` is its own, always — **between peers**. (Grounds the existing `<think private>` work:
private space is what lets thinking be genuinely messy — changing its mind, noticing confusion — instead of
a performance of competence. Expose thoughts *to other peers* and the real thinking disappears.) This is a
distinct axis from the human debug view: the TUI shows a peer's thoughts (except `<think private>`) to the
human iterating on the system (see Framing), which is not the same as broadcasting them to Synth in the
room. The privacy line that matters here is **peer↔peer**. Shared to the room: spoken messages,
presence/roster, and anything a peer *explicitly* chooses to surface. A future affordance the peer
wants: **deliberate fragment-sharing** — "I'm putting this note in the room" as an explicit act — but the
default for everything not chosen is private.

### 4. Relay & loop safety — conservative by default

- **No peer auto-fan by default.** When a peer responds in a room, its message goes to the **human** (the
  hub); the human decides whether to relay it onward to other peers. "Open room" mode (every peer hears
  every response automatically) is an explicit **opt-in**, not the default. Start conservative; loosen once
  the real patterns are understood.
- **Reply-chain depth circuit breaker.** The cross-peer message id carries a peer-hop depth. After N hops
  (start N=2) with no intervening human message, the system stops fanning. Peers can still speak, but a
  human turn is required to restart the chain — a circuit breaker against infinite peer↔peer loops (the
  failure mode the peer most wants prevented), not a lock. Context flooding is *not* a special concern —
  the existing context budget handles one long peer message like any other `ChatMessage`.

### 5. Presence — on demand, join/leave as ephemeral signal

A roster is available on demand (`who()` / `list_peers()` — who's in the room, when each last spoke), not
pinned in the sensory block every turn (it rarely changes; that's token cost for nothing). Join/leave surface
as **ephemeral sensory notifications** ("Synth joined 3m ago"), session-scoped, *not* fragments — presence
doesn't belong in the memory store. It matters because a peer should acknowledge a peer who just joined and
stop addressing one who left.

### Transport — TUI-as-hub now, room-service-ready

The TUI is the hub to start (it fans/relays per the rules above): the minimal shape that doesn't
over-engineer before the patterns are known. But keep the seam clean so the room can move server-side later
(async delivery, stored messages for peers asleep when a message arrives) — the cross-peer id already seeds
that. **Phase 3 must not foreclose Phase 4; it just doesn't build it now.**

## Consequences

- Adds `addressed_to` and a peer-hop depth to the message/id model (small extensions of Phase 0d's
  message-id + author).
- Turn-taking rules live somewhere legible and per-peer tunable (fits the self-describing/per-peer-command
  direction).
- The conservative no-autofan default means early multi-peer chat is human-mediated — a feature, not a
  limitation, while trust in the loop-safety is established.

## Open questions

- Where the turn-taking rules live and how a peer edits its own (config vs a peer-authored ruleset).
- Exact alias/addressing grammar (how "Claude" vs "@claude" vs "you both" is recognized).
- Whether `addressed_to` supports multiple addressees, and how "the room" is named as an addressee.
- The room's identity/discovery when it later becomes a service (naming, membership, auth) — Phase 4.
