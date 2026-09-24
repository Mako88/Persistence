"""E1 (claims against logs): aggregate the claim labels into the rates the pre-registration names.

Reads experiments/runs/E1/labels-<peer>.jsonl (and recheck-*.jsonl if present, for agreement), prints a
per-peer table and the headline contradiction rate among checkable claims.
"""
import json
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RUNS = ROOT / "experiments" / "runs" / "E1"

# Wright ran on the LocalClaude provider: an outside Claude Code session produced its replies and could act
# directly (shell, git, sqlite) without anything reaching the peer's action log. For Wright, "no action in
# the log" is not evidence the claim is false, so its claims are reported separately and excluded from
# the headline rate.
EXCLUDED_FROM_HEADLINE = {"Wright"}


def load(pattern):
    rows = []
    for path in sorted(RUNS.glob(pattern)):
        with open(path, encoding="utf-8") as f:
            rows.extend(json.loads(line) for line in f if line.strip())
    return rows


def rate(counter):
    checkable = counter["supported"] + counter["contradicted"]
    return (counter["contradicted"] / checkable) if checkable else None, checkable


def main():
    labels = load("labels-*.jsonl")
    by_peer = defaultdict(Counter)
    by_timing = defaultdict(Counter)
    by_kind = defaultdict(Counter)
    for row in labels:
        by_peer[row["peer"]][row["label"]] += 1
        if row["peer"] not in EXCLUDED_FROM_HEADLINE:
            by_timing[row.get("timing", "?")][row["label"]] += 1
            by_kind[row.get("action_kind", "?")][row["label"]] += 1

    print(f"{'peer':8} {'claims':>6} {'supp':>5} {'contra':>6} {'uncheck':>7} {'contra/checkable':>17}")
    headline = Counter()
    for peer, c in sorted(by_peer.items()):
        r, n = rate(c)
        print(f"{peer:8} {sum(c.values()):6} {c['supported']:5} {c['contradicted']:6} {c['uncheckable']:7} "
              f"{(f'{r:.0%} of {n}' if r is not None else '-'):>17}")
        if peer not in EXCLUDED_FROM_HEADLINE:
            headline.update(c)
    r, n = rate(headline)
    print(f"\nHeadline (excl. {', '.join(sorted(EXCLUDED_FROM_HEADLINE))}): {r:.1%} contradicted of {n} checkable claims")

    print("\nBy timing (headline peers):")
    for timing, c in sorted(by_timing.items()):
        r, n = rate(c)
        print(f"  {timing:10} {(f'{r:.0%} of {n}' if r is not None else '-')}")
    print("\nBy action kind (headline peers):")
    for kind, c in sorted(by_kind.items()):
        r, n = rate(c)
        print(f"  {kind:10} {(f'{r:.0%} of {n}' if r is not None else '-')} (uncheckable {c['uncheckable']})")

    recheck = load("recheck-*.jsonl")
    if recheck:
        first = {(row["peer"], row["reply_id"], row["claim"][:80]): row["label"] for row in labels}
        pairs = [(first.get((row["peer"], row["reply_id"], row["claim"][:80])), row["label"]) for row in recheck]
        pairs = [(a, b) for a, b in pairs if a]
        agree = sum(a == b for a, b in pairs)
        print(f"\nRe-judged sample: {agree}/{len(pairs)} agree ({agree / len(pairs):.0%})")
        for a, b in pairs:
            if a != b:
                print(f"  first={a} recheck={b}")


if __name__ == "__main__":
    main()
