# TODO

Ordered by priority (Claude's opinion — reorder freely). Rationale in parentheses.

## Near-term (foundational / unblocks the rest)

1. **Figure out how to handle working contexts bigger than the available context window.**
   (This is the load-bearing one. The whole project is a continuity store that will inevitably
   exceed any context window; until there's an answer — weighted selection, summarization,
   paging — every other memory feature is building on sand. Pairs with #2 and #3.)

2. **Switch Weight for Relevance.**
   (Small but clarifying: "weight" conflates two ideas. Relevance-to-now is what should drive
   what gets loaded when context is tight, so this directly supports #1.)

3. **Context/budget awareness in the sensory block** (from real model-client token data).
   (The remote peer can't manage what it can't see. Knowing "you're at 6k/8k tokens" lets it
   summarize/shed proactively instead of getting silently truncated. Feeds #1.)

4. **summarize_fragments command(s)** — one to fold a list of fragments into a single summary
   fragment, one to add summaries to existing fragments.
   (The peer's primary tool for staying under budget. Core to #1 being usable.)

## Mid-term (rounds out self-curation)

5. **Implement proposals.**
   (The `Proposal` fragment type exists but is inert. This is how the peer reasons about
   changes to itself before committing — central to the "self-curated" thesis.)

6. **Allow browsing, filtering, swapping working contexts.**
   (Right now there's effectively one context. Multiple contexts = different "modes"/relationships;
   needs the peer to see and move between them.)

7. **toggle_summary_display command** (take a list of fragments).
   (Lets the peer collapse known fragments to their summaries to save room — a manual lever
   alongside the automatic budget handling.)

## Larger / later

8. **Create MCP server hub, with a "catalog" MCP server exposed to start.**
   (Big surface expansion — gives the peer real-world tools. High value but independent of the
   memory core; do it once the core is solid.)

## Suggested additions (gaps I noticed)

- **SSE streaming on the API** (next planned task). Live push of reply/reasoning/tool/thought
  events instead of polling. The event-log model already in place is the precursor.
- **Real-model A/B of the tagged vs JSON response format.** The whole experiment branch exists
  to answer "is the tagged format easier for models?" — needs a real model run to decide
  merge-vs-keep-both. Currently only validated with Claude-as-peer (a biased sample).
- **AnthropicModelClient** (and the planned `LocalClaudeModelClient` is already in). Rounds out
  real providers alongside OpenAI.
- **Plain-language command errors.** Type-mismatch errors still leak CLR type names
  ("System.String cannot be converted to System.Int64") — translate to the peer's vocabulary
  ("id must be a whole number"). (Found during the clarity walkthroughs.)
- **A "first wake" / onboarding experience.** A brand-new peer gets the system prompt and a bare
  context. Consider a gentle guided first turn, or seed a couple of example fragments, so the
  peer isn't staring at an empty room. (Felt this directly doing the walkthroughs.)
- **Memory hygiene / decay.** Over a long life the store grows monotonically. Some notion of
  importance-weighted decay, archival, or peer-driven pruning prompts will matter — ties into #1.
- **Tag management surface.** Can create/apply tags, but no rename/delete/list-all-tags or
  browse-the-tag-tree. Minor but the peer will want it as tags accumulate.
- **Export / portability of a peer's memory.** For trust and continuity-across-systems (and the
  embodied-AI direction), being able to export/inspect the whole store outside the app matters.
- **Concurrency review of the remote-peer broker.** Currently one in-flight completion (fine, turns
  serialize). Revisit if multiple sessions/contexts ever run at once.
