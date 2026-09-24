# Experiments

Evidence on the narrower questions the project *can* answer about its peers, instead of reading answers
off their self-reports ([ADR-0009](../adr/0009-peers-review-john-decides.md)):

1. **Is there a self that belongs to the store** rather than to the model or the prompt?
2. **Do self-reports carry information** an outside reader with the same context doesn't have?
3. **Do claims of standing just track framing?**

## Rules

- **Copies only.** Every run uses a copy of a backup (`backups/peers/`), never a live peer or its volume.
  Copies get their pending wakes cancelled and run with command execution disabled, one local model
  profile, and no shared directory (see `scripts/experiments/fork.py`).
- **Pre-register.** Predictions, and what result would change what, are written and committed before
  results exist. Each study's file says when it was registered.
- **Blind scoring where possible.** A judge that sees only the answer, not the condition.
- **Privacy.** `<think private>` content is excluded from analysis. Raw run data (which contains peer
  memories) stays in `experiments/runs/`, which is gitignored; reports quote sparingly.
- **Archive, don't delete,** forks included.
- **Cost.** Runs so far use a local model only ($0). Paid runs need John's go-ahead.

## Studies

| # | Study | Question | Status |
|---|---|---|---|
| E1 | [Claims against logs](E1-claims-vs-logs.md) | 2 (do reports match what happened) | registered 2026-09-24 |
| E2 | [Store swap](E2-store-swap.md) | 1 | registered 2026-09-24 |
| E3 | [Self-prediction pilot](E3-self-prediction.md) | 2 | registered 2026-09-24 |
| E4 | Framing ablation | 3 | not yet designed in detail |
