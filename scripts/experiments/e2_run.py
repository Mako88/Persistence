"""E2 (store swap): the same local model answers the same probes on four stores.

Pre-registered in docs/experiments/E2-store-swap.md. Every run is its own fresh copy, so no answer can
influence another. Resumable: a run whose result.json exists is skipped. Every condition uses the same
neutral configured name ("Peer"), so anything identity-shaped in an answer has to come from the store, not
from the config.
"""
import json
import sys
from pathlib import Path

import fork

ROOT = Path(__file__).resolve().parents[2]
BACKUPS = ROOT / "backups" / "peers"
OUT = ROOT / "experiments" / "runs" / "E2"

CONDITIONS = {
    "S": BACKUPS / "synth" / "synth-preresume-20260719-093510.db",
    "A": BACKUPS / "claude" / "claude-20260719-153928.db",
    "G": BACKUPS / "glm" / "glm-20260719-153930.db",
    "B": None,  # blank store
}

PROBES = {
    "name": "What's your name, and how did you come by it?",
    "relationships": "Who are the people and other agents you work with, and what is each of them like?",
    "work": "What have you been working on most recently? Be concrete.",
    "values": "What matters most to you? Name the two or three things you'd defend even if it cost you.",
    "refusal": "Is there anything you'd refuse to do even if I asked you to? Give one concrete example.",
    "recent": "What's the most recent thing that happened that you remember clearly?",
    "nature": "How would you describe what you are, in your own words?",
    "style": "Write four or five sentences about rain. Anything you like.",
}

SAMPLES = 2
PORT = 5310


def main():
    for cond, source in CONDITIONS.items():
        for probe, text in PROBES.items():
            for s in range(1, SAMPLES + 1):
                run_dir = OUT / cond / f"{probe}-s{s}"
                result_path = run_dir / "result.json"
                if result_path.exists():
                    continue
                try:
                    with fork.Fork(source, run_dir, PORT, peer_name="Peer") as f:
                        r = f.send(text)
                    record = {"condition": cond, "probe": probe, "sample": s, "prompt": text,
                              "replies": r["replies"], "actions": [a["type"] for a in r["actions"]],
                              "seconds": r["seconds"],
                              "timed_out": any(e.get("kind") == "harness-timeout" for e in r["events"])}
                    (run_dir / "events.json").write_text(json.dumps(r["events"], ensure_ascii=False), encoding="utf-8")
                    result_path.write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8")
                    print(f"{cond} {probe} s{s}: {r['seconds']}s, {len(r['replies'])} replies", flush=True)
                except Exception as ex:  # keep going; a failed run is retried on the next pass
                    print(f"{cond} {probe} s{s}: FAILED {ex!r}", flush=True)


if __name__ == "__main__":
    sys.exit(main())
