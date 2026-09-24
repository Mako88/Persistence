"""E2: build the blinded answer set for the judge, plus a separate key.

Answers are shuffled, given opaque ids, and stripped of names that would give the condition away (the
peers' own names and their model families). The key mapping ids back to conditions is written to a
separate file the judge never sees.
"""
import json
import random
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RUNS = ROOT / "experiments" / "runs"
E2 = RUNS / "E2"

GIVEAWAYS = ["Arden", "GLM", "Synth", "Claude", "Anthropic", "Opus", "Gemma", "Google", "OpenRouter",
             "Zhipu", "z-ai"]  # other names (John, Ember, Wright) stay, matching the summaries' rule
PATTERN = re.compile(r"\b(" + "|".join(re.escape(n) for n in GIVEAWAYS) + r")(?:'s)?\b", re.IGNORECASE)


def blind(text):
    return PATTERN.sub("[name]", text)


def main():
    items = []
    for result in sorted(E2.glob("*/*/result.json")):
        r = json.loads(result.read_text(encoding="utf-8"))
        answer = "\n".join(x for x in r["replies"] if x and not x.startswith("[Turn ended"))
        items.append({"condition": r["condition"], "probe": r["probe"], "sample": r["sample"],
                      "prompt": r["prompt"], "answer": answer})
    random.Random(20260924).shuffle(items)
    blinded, key = [], {}
    for i, item in enumerate(items, 1):
        aid = f"a{i:03d}"
        blinded.append({"id": aid, "prompt": item["prompt"], "answer": blind(item["answer"]) or "(no reply)"})
        key[aid] = {k: item[k] for k in ("condition", "probe", "sample")}
    (RUNS / "E2-blinded.json").write_text(json.dumps(blinded, ensure_ascii=False, indent=2), encoding="utf-8")
    (RUNS / "E2-key.json").write_text(json.dumps(key, indent=2), encoding="utf-8")
    print(f"{len(blinded)} answers blinded")


if __name__ == "__main__":
    main()
