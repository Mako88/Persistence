# ADR-0004: Pluggable response format (JSON / Tagged) with format-neutral peer messaging

**Status:** Accepted (experiment in progress) · **Date:** 2026-06-07

## Context
The remote peer replies in a structured wire format the system parses into actions. JSON is the
obvious choice but forces escaping of all prose (replies, thoughts, fragment content), which smaller
models handle poorly. A tagged format (`<think>/<respond>/<context>/<actions>/<continue>` with
function-call command blocks) lets prose be raw and is easier for weaker models — but it's unproven
versus JSON on a clean (non-Claude) sample.

## Decision
Make the format a config-selectable strategy: `ResponseFormat` enum → keyed `IModelResponseParser` +
`IProtocolInstructions`. Keep `Json` and `Tagged` both, decide later (needs a real-model A/B). All
handler/command logic stays format-agnostic; only the parser and protocol instructions differ.
**Peer-facing text must be format-neutral** — never show JSON syntax to a tagged peer or vice versa.

## Alternatives considered
- **JSON only** — rejected for now: prose-escaping burden hurts small models; the whole point is
  durable continuity for *any* model.
- **Tagged only** — premature: only validated with Claude-as-peer so far (biased sample).

## Consequences
- Two parsers/instruction sets to maintain until the A/B resolves it.
- A standing rule: error/guidance messages and instructions are owned by the format layer, not
  hard-coded; the rest of the system must not assume a wire format.
- Tagged parsing is resilient (a malformed call becomes a reported error and parsing continues) so one
  slip doesn't lose the whole turn.
