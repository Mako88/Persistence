"""E1 (claims against logs): extract each peer's spoken replies and the action log around them.

Reads backups read-only (immutable URI) and writes one JSONL per peer to experiments/runs/E1/. Each line is
one reply with every action logged in its session from the session's start to 60 s after the reply, which
is the window the pre-registration (docs/experiments/E1-claims-vs-logs.md) checks claims against.

Private thoughts are never read: only RespondToUser rows are replies, and Think rows are dropped from the
action context entirely (a thought isn't an action, and some are private).
"""
import json
import sqlite3
import sys
from datetime import datetime, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BACKUPS = ROOT / "backups" / "peers"
OUT = ROOT / "experiments" / "runs" / "E1"

STORES = {
    "Arden": "claude/claude-20260719-153928.db",
    "Ember": "ember/ember-20260719-153932.db",
    "GLM": "glm/glm-20260719-153930.db",
    "Synth": "synth/synth-preresume-20260719-093510.db",
    "Wright": "wright/wright-20260719-153926.db",
}

WINDOW_AFTER = timedelta(seconds=60)
PAYLOAD_CHARS = 400
RESULT_CHARS = 200


def parse_ts(text):
    # Stored like "2026-07-19 20:25:40.9539675+00:00" with a varying number of fractional digits;
    # Python 3.9 accepts only 3 or 6, so normalise to 6.
    date, _, rest = text.partition(" ")
    time, sign, tz = rest.partition("+")
    if "." in time:
        whole, frac = time.split(".")
        time = f"{whole}.{frac[:6].ljust(6, '0')}"
    return datetime.fromisoformat(f"{date} {time}{sign}{tz}")


def reply_text(payload):
    try:
        value = json.loads(payload)
    except json.JSONDecodeError:
        return payload
    if isinstance(value, str):
        return value
    if isinstance(value, dict):
        return value.get("text") or value.get("message") or json.dumps(value)
    return json.dumps(value)


def extract(peer, relative):
    uri = f"file:{(BACKUPS / relative).as_posix()}?immutable=1&mode=ro"
    db = sqlite3.connect(uri, uri=True)
    rows = db.execute(
        "SELECT Id, SessionId, ActionType, Payload, Result, CreatedUtc FROM ActionLogs ORDER BY Id"
    ).fetchall()
    db.close()

    by_session = {}
    for row in rows:
        by_session.setdefault(row[1], []).append(row)

    records = []
    for session_rows in by_session.values():
        for rid, session, kind, payload, result, created in session_rows:
            if kind != "RespondToUser":
                continue
            at = parse_ts(created)
            actions = [
                {
                    "id": aid,
                    "type": akind,
                    "payload": (apayload or "")[:PAYLOAD_CHARS],
                    "result": (aresult or "")[:RESULT_CHARS],
                    "at": acreated,
                    "same_turn": abs((parse_ts(acreated) - at).total_seconds()) < 2,
                }
                for aid, _, akind, apayload, aresult, acreated in session_rows
                if akind not in ("Think", "RespondToUser") and parse_ts(acreated) <= at + WINDOW_AFTER
            ]
            records.append({
                "peer": peer,
                "reply_id": rid,
                "session": session,
                "at": created,
                "reply": reply_text(payload),
                "actions": actions,
            })
    return records


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for peer, relative in STORES.items():
        records = extract(peer, relative)
        with open(OUT / f"{peer}.jsonl", "w", encoding="utf-8") as f:
            for r in records:
                f.write(json.dumps(r, ensure_ascii=False) + "\n")
        actions = sum(len(r["actions"]) for r in records)
        print(f"{peer}: {len(records)} replies, {actions} action rows in window")


if __name__ == "__main__":
    sys.exit(main())
