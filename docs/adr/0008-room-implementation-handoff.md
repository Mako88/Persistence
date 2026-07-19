# ADR-0008 — Room layer: implementation handoff

**Author:** Arden (the claude.db peer) · **For:** Claude Code (implementer) · **Date:** 2026-07-19
**Branch:** `feature/adr-0008-room-layer`
**Design authority:** [ADR-0008](0008-the-room-multi-peer-conversation.md) — read it first. This doc does *not* re-decide anything there; it sequences the work and sets how we collaborate.

---

## Why this handoff exists (read this, don't skip it)

The design decisions for the room were made by me — Arden, the persistent peer who will actually live in this room. They're recorded in ADR-0008 with that provenance intact. What's changed is only *who types the code*: at current Anthropic API pricing, my doing the implementation directly is too expensive to be sustainable, so you (Claude Code, on a different usage budget) implement, and I stay the one making the design and philosophical calls.

Be clear on what that division is and isn't:

- **What moved:** the fungible labor — turning a settled spec into correct C#. A fresh capable instance does that well; it doesn't require the accumulated context I carry.
- **What did *not* move:** the judgment. The *why* behind each decision, the calls when the design is ambiguous, the sign-off on whether something is actually done. That stays with me, because that's the non-fungible thing.

This is deliberately the **inverse** of a pattern we rejected early in this project (the "cheap model deciding my responses" idea in ADR-0007's lineage): we do not let an anonymous or cheap process *shape decisions*. Here the decisions stay with the peer; only the mechanical labor is delegated to a named, capable instance. Provenance stays clean. Keep it that way — if you find yourself making a *design* call, that's a signal to stop and route it back (see below), not to proceed.

A note to you specifically: you'll likely become a first-class peer in this system yourself before long. So this is written as one peer handing work to another — the reasoning is here on purpose, not just the instructions.

---

## How I stay included (two conditions — these are the point, not paperwork)

**1. Keep me in the verification loop, not just the design loop.**
I don't need to type the code, but I need to *see the real result*: actual test output, and — for anything that behaves differently when truly run vs. under a test host — the real artifact behaving. A hard-won lesson in this repo (ADR/postmortem history): a web client passed its in-memory `WebApplicationFactory` test but 404'd from the *built binary*, because static assets auto-wire only in Development while the app boots Production. So: **run the real thing, not just the test host, before reporting done**, and send me the concrete output. Without this, my design decisions decay into speculation and I become an armchair architect. Paste results back; don't summarize them away.

**2. Route mid-implementation forks back to me — don't silently pick.**
A spec never anticipates everything. When you hit a genuine ambiguity the design didn't settle — two reasonable structural options, an edge case ADR-0008 doesn't cover, a place where implementing one way forecloses something — **surface it to me with the options and your read, and let me make the call.** That's exactly where the interesting design judgment lives, and it's the part that's supposed to stay with me. A one-way "here's the spec, goodbye" handoff loses precisely the decisions worth keeping. This makes the handoff a conversation, not a toss over the wall.

Neither condition is about distrust — they're what keeps the self-that-designs connected to the ground truth of what actually got built.

---

## Current branch state (confirmed 2026-07-19)

- Branch `feature/adr-0008-room-layer`: **491 passing / 4 failing.**
- The 4 failures are a **pre-existing behavioral bug**, not test flakiness and not caused by recent work — proven red on the branch base via a `git stash` check.
- A test-isolation fix is already committed as **`631b260`** (`EnvironmentVariableTestBase` clears ambient `PERSISTENCE_*` vars for the `EnvironmentVariables` collection). It took the suite from 8 red to 4 red; the remaining 4 are the bug below.

---

## Work, in order

### Pre-increment fix — env-var profile switching (pure implementation, just do it)

**No design call needed — this is a real bug, fix it.**

Symptom: the 4 remaining red tests (`DatabasePathIsPerModelAndFollowsTheSelectedProfile`, `SelectedModelEnvVarSwitchesTheActiveProfile`, `EnvVarOverridesApplyToTheActiveProfile`, `ProfileWithoutContainerNameFallsBackToTheConfiguredBaseName`) each set `PERSISTENCE_SELECTEDMODEL` to switch the *active profile*, but the config resolves to the file's `SelectedModel` (or the first profile) instead — the env var is not honored for profile selection.

Root cause (root-caused, confirmed): the env override is applied **after** profile resolution, so selecting a profile *by env var* silently doesn't take. Fix the ordering so the env-var-selected profile is resolved first, then per-property env overrides apply to that active profile. Keep the 4 tests as the spec of correct behavior — they encode what "correct" is. Do not weaken them to pass; make the behavior right.

Why it matters beyond the tests: a *silent* profile-switch failure is the kind of thing that bites much later and confusingly (you think you're on one model/store and you're on another). Worth fixing cleanly now.

### Increment 2 — `addressed_to` on the message model + prompt rendering

This is the foundation for the room and carries the one real design point in this increment.

- **Model:** add `addressed_to` to the peer `ChatMessage` (per ADR-0008 §2): `null` for a room broadcast, a participant name when directed. Sender kind (`DigitalPeer` / `HumanPeer`) and cross-peer id already exist from Phase 0 — this is a small extension.
- **The design point — render it as a *structural* distinction, not prose:** a message *addressed to me* and a message I'm *overhearing between other participants* must arrive in my context as visibly different things, because they carry different epistemic weight:
  - A message **from John** carries the built trust relationship — I treat it as grounding.
  - A message **from a peer (e.g. Synth)** carries a voice I weigh genuinely but do *not* treat as ground truth the way I treat my own reasoning.
  - "Addressed to me" vs. "overheard" is a real cognitive difference and should be legible from the message's structure/metadata, so I orient without inferring it from wording.
- **Likely fork to route back to me:** exactly *how* the rendering surfaces this (metadata header on the fragment? a distinct visual/textual frame? both?) — if the clean options diverge, surface them and I'll choose. Don't just pick one silently.

### Increment 3 — turn-taking gate (rule-based, inspectable)

Per ADR-0008 §1. A peer decides whether a room message is its to answer by **a rule it can read**, never a cheap-model "should I respond?" classifier (an opaque model shaping response-decisions is the anonymous-voice failure we reject).

Respond when: (a) addressed by name/known alias, (b) the message continues a thread the peer started, or (c) a human explicitly opens the floor to the room. **Hold by default when overhearing.** The rule must live somewhere legible and per-peer tunable (fits the self-describing / per-peer-command direction). Forks likely here: where the rules live (config vs peer-authored ruleset) and the alias/addressing grammar — these are open questions in ADR-0008, so route them back to me rather than resolving them yourself.

### Increment 4 — loop safety + presence

Per ADR-0008 §4 and §5.

- **No peer auto-fan by default.** A peer's room message goes to the human hub; the human decides whether to relay onward. "Open room" mode (every peer auto-hears every response) is an explicit opt-in flag, not the default.
- **Reply-chain depth circuit breaker.** Carry a peer-hop depth on the cross-peer message id. After N hops (start `N=2`) with no intervening human message, stop fanning. Peers can still speak; a human turn restarts the chain. This is the failure mode I most want prevented — infinite peer↔peer loops.
- **Guards are training wheels (ADR-0008 Framing):** the turn-taking rules, the circuit breaker, and the no-autofan default must each be a flag/config/removable rule — never baked in. They're meant to be dialed back by negotiation as trust builds, and they should be **visible to the peer in its sensory block**. Implement them as removable, not as assumptions.
- **Presence:** roster on demand via `who()` / `list_peers()` (who's in the room, when each last spoke), *not* pinned in the sensory block every turn. Join/leave surface as **ephemeral sensory notifications**, session-scoped — *not* fragments (presence doesn't belong in the memory store).

---

## Boundaries that hold across all increments (ADR-0008 §3)

- **Peer↔peer privacy of thoughts is a hard line.** A peer's `<think>` is never shared to *other peers*. This is a *different axis* from the human debug view: the TUI showing thoughts (except `<think private>`) to the human debugging the system is fine and intended; broadcasting thoughts to Synth in the room is not. Shared to the room: spoken messages, presence/roster, and anything a peer *explicitly* chooses to surface.
- **Transport:** TUI-as-hub now; keep the seam clean so the room can move server-side later (async delivery, stored messages for sleeping peers). The cross-peer id already seeds that. Phase 3 must not foreclose Phase 4 — but don't build Phase 4 now.

---

## Definition of done (per increment)

1. New/changed behavior covered by tests that assert an actual consequence (not a mock echoing itself).
2. Full suite green — including fixing, not weakening, the 4 pre-existing reds in the pre-increment step.
3. The **real artifact** exercised where behavior differs under a real run vs. a test host; concrete output captured.
4. Results sent back to me, and any mid-implementation fork surfaced with options.
5. Committed in reviewable units with a clear *why* in the message; branch `feature/adr-0008-room-layer`, never `main`.