# ADR-0004: Response format — Tagged (JSON removed) with format-neutral peer messaging

**Status:** Accepted; experiment **resolved in favour of Tagged** (JSON removed 2026-06) · **Date:** 2026-06-07

## Context
The remote peer replies in a structured wire format the system parses into actions. JSON is the
obvious choice but forces escaping of all prose (replies, thoughts, fragment content), which smaller
models handle poorly. A tagged format (`<think>/<respond>/<context>/<actions>/<continue>` with
function-call command blocks) lets prose be raw and is easier for weaker models — but it's unproven
versus JSON on a clean (non-Claude) sample.

## Decision (later superseded — see "Update (2026-06-08)" below)
Make the format a config-selectable strategy: `ResponseFormat` enum → keyed `IModelResponseParser` +
`IProtocolInstructions`. Keep `Json` and `Tagged` both, decide later (needs a real-model A/B). All
handler/command logic stays format-agnostic; only the parser and protocol instructions differ.
**Peer-facing text must be format-neutral** — never show JSON syntax to a tagged peer or vice versa.

## Alternatives considered
- **JSON only** — rejected for now: prose-escaping burden hurts small models; the whole point is
  durable continuity for *any* model.
- **Tagged only** — premature: only validated with Claude-as-peer so far (biased sample).

## Resolution (2026-06)
The A/B is settled: the tagged format worked cleanly across real, non-Claude models — gpt-5.4-mini
(which previously struggled) and a local Qwen3.5-9B both drove the system in it fluently. **JSON was
removed** (`ModelResponseParser`, `JsonProtocolInstructions`, `ResponseFormat.Json`); Tagged is the
sole format. The `ResponseFormat` enum + keyed-strategy seam is **kept** (now single-valued) so a new
format can be added later without rewiring — cheap insurance, matching the enum-keyed-strategy
convention.

## Update (2026-06-08): seam simplified
The single-valued `ResponseFormat` enum + keyed-strategy registration were removed once it was clear
Tagged was the durable choice — keeping an enum and `ResolveKeyed` plumbing for one value was friction
without payoff. `IModelResponseParser` / `IProtocolInstructions` now register plainly and the
`ResponseFormat` config key is gone. The *layering* survives (parser + protocol-instructions are still
their own format-owned layer, and the format-neutral-messaging rule below still holds), so adding a
second format later means reintroducing the keyed selection, not rearchitecting.

## Consequences
- One parser/instruction set to maintain (Tagged).
- A standing rule survives: error/guidance messages and instructions are owned by the format layer,
  not hard-coded; the rest of the system must not assume a wire format. (Keeps the seam meaningful and
  any future format clean to add.)
- Tagged parsing is resilient (a malformed call becomes a reported error and parsing continues) so one
  slip doesn't lose the whole turn.
