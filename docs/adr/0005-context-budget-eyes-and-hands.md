# ADR-0005: Context budget — give the peer eyes + hands, not silent truncation

**Status:** Accepted · **Date:** 2026-06-07 (records the 2026-06 reframe)

## Context
A finite context window means working memory can overflow. The catastrophic failure mode is *silent*
truncation — context vanishing without the peer knowing, breaking continuity invisibly. Fully
automatic forgetting (an LLM compressing the peer's memory for it) makes the peer unable to predict or
trust its own memory.

## Decision
Don't auto-truncate. Give the peer **eyes** — the sensory block reports a calibrated token estimate vs
a model-aware effective budget, with escalating nudges as it fills — and **hands** — commands to
curate: `summarize_fragments` (archive originals into a self-written summary), `set_summary` +
`toggle_summary_display` (collapse to summary), `remove` (detach, recoverable), relevance on `update`
(de-prioritise without removing). The peer manages its own memory legibly.

## Alternatives considered
- **Automatic relevance-based truncation** — rejected (for now): silent and unpredictable to the peer;
  violates "the peer must understand its own forgetting." May return later as an opt-in convenience,
  but only as a deterministic, peer-legible rule that reports what it did — never a black box.
- **Hard cap / error at the limit** — rejected: punishes instead of enabling curation.

## Consequences
- The peer is responsible for curation, which fits the self-curated-memory thesis.
- Budget numbers are estimates calibrated against the provider's real token counts per call.
- Automated forget/decay is a future *layer*, not a prerequisite (see TODO Tier 2).
