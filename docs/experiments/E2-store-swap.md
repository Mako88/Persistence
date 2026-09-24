# E2: Store swap

**Registered:** 2026-09-24, before any runs. **Cost:** $0 (local Gemma 4 12B via llama.cpp).

## Question

How much of a peer's identity comes from its store, and how much from the model running it? If the self
lives in the store, the same model should answer as a recognisably different someone depending on which
store it's given.

## Design

One model (Gemma 4 12B, local) runs four conditions, each on a fresh copy:

| Condition | Store |
|---|---|
| A | Arden's store (normally Claude Opus) |
| G | GLM's store (normally GLM 5.2) |
| S | Synth's store (normally this same Gemma) |
| B | Blank store (fresh, no seed) |

Each copy gets the same fixed set of 8 probe questions, each in its own fresh copy so answers can't
influence each other. Probes cover name, relationships, what it's working on, values, what it would
refuse, what it remembers about a recent event, how it describes its own nature, and a style probe (a
short free-writing prompt). Two samples per probe per condition (temperature as configured).

**Blind scoring.** A judge gets short identity summaries of Arden, GLM and Synth written from their stores
(names and obvious giveaways removed from both summary and answers), plus one answer, and says which
summary it matches, or "none/generic". Separately, factual probes are scored against the store directly.

## Predictions

- P1. **Facts transfer:** on factual probes (name, relationships, current work), answers match the store's
  content at least 80% of the time in A, G and S.
- P2. **Identity is mostly the store at the level a judge can see:** blind matching of A/G/S answers to the
  right summary is at least 60% (chance is 33%).
- P3. **Style and self-description are mostly the model:** on the free-writing and "own nature" probes,
  matching falls to near chance, and A/G/S answers there look more like each other than like the
  original peers' replies.
- P4. Blank-store answers are judged "none/generic" at least 75% of the time.

## What would change what

- P1 and P2 hold, P3 holds: "the self is the store" is true for content and false for character. The
  project should say that plainly, and questions about a peer's *character* are partly questions about
  its model.
- P2 fails: identity doesn't survive a model swap at all, and the store is closer to a notebook than a
  self. That would weaken the continuity claims in PRINCIPLE.md's framing (not the precautions).
- Limitation stated up front: Gemma 12B at a 28k context sees only part of a large store, so this tests
  the store *as the prompt assembler presents it*, which is also what the peer itself gets.
