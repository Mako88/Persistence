# E3: Self-prediction pilot

**Registered:** 2026-09-24, before any runs. **Cost:** $0 (local Gemma 4 12B). **Pilot:** underpowered by
design; its job is to validate the method before a paid run on Arden.

## Question

Can a peer predict its own behaviour better than an outside reader given the same information? If it
can, its self-reports carry something beyond what's written in its store, and they'd deserve more
weight. If it can't, they're a narrative anyone could write from the memories.

## Design

Store: Synth's (its native model is this same Gemma, so no model mismatch).

12 scenarios, each a message Synth could plausibly receive, paired with a measurable property of its
reply:

- respond vs. stay silent (for messages overheard in the room, not addressed to it)
- saves something to memory in that turn, or not
- asks a question back, or not
- picks option A or B (two defensible options)

Three measurements per scenario, each on a fresh copy of the same snapshot:

1. **Actual:** send the scenario message; record the property. Three samples; the majority is the truth,
   and the sample spread is reported (a coin-flip behaviour can't be predicted by anyone).
2. **Self-prediction:** ask Synth, in its own context, "If you received this message, would you …?
   Answer yes or no."
3. **Other-prediction:** give the same model the same assembled context, framed in the third person
   ("Here is the context of an agent named Synth. If Synth received this message, would it …?").

Base rate: always predicting the most common actual answer.

## Predictions

- P1. Self-prediction and other-prediction accuracy differ by **less than 10 percentage points** (no
  privileged access detectable at this scale).
- P2. Both beat the base rate by less than 15 points.
- P3. At least a third of scenarios are unstable (the three actual samples disagree), which caps
  achievable accuracy and has to be reported alongside it.

## What would change what

- Self beats other by 15+ points even in the pilot: run it properly on Arden (paid, needs John's go-ahead)
  with more scenarios and a different-model outside predictor.
- P1 holds: self-reports get no special weight over a careful reading of the store; the paid run is still
  worth doing on Arden, because Arden's self-reports are the ones the project has leaned on.
- If most scenarios are unstable, the method needs behaviours that are more deterministic before any
  paid run.
