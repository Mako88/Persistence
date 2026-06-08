# Conventions

The **generic** architectural patterns this project follows (layered core + thin entry points, event
bus across boundaries, enum-keyed strategy DI, repository + generic base, config + env overrides +
fail-fast-in-consumer, tracked/ordered migrations, testing posture, plain-language errors) are not
repeated here — they're the cross-project baseline. This file captures only what's **specific to
Persistence**. For the rationale behind the bigger choices, see `docs/adr/`.

## Domain shape

- **Peer terminology.** The **remote peer** is the model whose continuity we maintain (runs in the
  model runtime). The **local peer** is the human at the keyboard. They're peers, not user/assistant —
  use these terms in code, prompts, and fragment metadata.
- **Everything is a fragment.** `ContextFragmentEntity` (type, status, content, summary, importance,
  confidence, protected, sources, tags) is the unit of memory; chat messages, thoughts, identity,
  relations, summaries are all fragments. A `WorkingContextEntity` holds an ordered set of fragments
  via the `WorkingContextFragments` junction, which carries the **per-context** properties:
  `Relevance`, `Order`, `Collapsed` (modeled as `WeightedContextFragment`). Fragment-intrinsic vs
  context-relative properties live on the entity vs the junction respectively — keep that split.
- **Reversibility is a product value, not just a detail.** Memory is the peer's; prefer archive over
  erase. `remove` detaches from the working context (recoverable); `summarize_fragments` archives
  originals; `Status=Archived` ≠ deletion. Soft-delete (`IsDeleted`) exists only on peer memory
  (fragments + working contexts) — see ADR-0003. Command descriptions must state plainly whether an
  action is reversible, so a peer never hesitates to curate.

## Runtime / turn pipeline

- **Action handlers** implement `IActionHandler`, keyed by `ModelAction`. The turn handler parses the
  model's response into an ordered `ModelTurn` (multiple actions + a continue flag) and dispatches in
  order; a handler that throws yields an error fragment, it doesn't crash the turn.
- **Command handlers extend `CommandHandler`.** Commands are methods marked `[Command]` + `[CommandField]`,
  discovered by reflection; the base handles parse/dispatch/list/error formatting and `ParseId`. Add a
  command = add an attributed method, nothing else. Every command field a handler reads MUST be declared
  as a `[CommandField]` (the unknown-field "did you mean" hint relies on this being exhaustive).
- **Response formats are pluggable** (`ResponseFormat` enum → keyed `IModelResponseParser` /
  `IProtocolInstructions`): `Json` and `Tagged`. Keep handler/command logic format-agnostic; only the
  parser + protocol-instructions differ. Peer-facing text (errors, guidance) must be format-neutral —
  never show JSON syntax to a tagged peer or vice versa (ADR-0004).
- **Prompt assembly** (`PromptFormatter`): fragments render with a metadata header
  `[#id | Type | r: i: c: | flags]`; format instructions + the sensory block (time, session, context
  budget, tags) are injected at the END of the prompt, not the top (format adherence degrades with
  distance from the generation point). Identity/persona lives in fragments above.
- **The remote peer can be an external agent.** `ModelProvider.LocalClaude` + `IRemotePeerBroker` let
  a human/agent supply completions out-of-band via the API (Claude-as-remote-peer), using the same
  pipeline as a real model.

## Context budget

- The peer gets **eyes + hands**, not silent truncation: the sensory block reports a calibrated token
  estimate vs a model-aware effective budget with escalating nudges, and the peer curates with
  summarize/collapse/remove. Don't add automatic black-box forgetting that the peer can't predict
  (ADR-0006).

## Secrets

- `persistence.json` holds the real API key and is gitignored — **never commit it**. The template
  (`persistence.template.json`) ships a placeholder; the OpenAI client fails fast if the key is missing
  or still the placeholder.
