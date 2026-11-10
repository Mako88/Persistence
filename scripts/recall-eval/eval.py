#!/usr/bin/env python3
"""The recall A/B (docs/design/concept-graph.md, build step 6): each arm's recall on a gold set, scored per store.

    eval.py snapshot <live db> <out db>         a consistent copy (sqlite backup API); arms never touch a live store
    eval.py run <gold.jsonl> <db> [--arm today] [--limit 3] [--out results.jsonl]

A gold line: {"id": "emma", "query": "...", "as_of": "2026-11-10T00:30:00Z", "gold": [7153], "kind": "miss|sample|none",
"project": "pawsitivity", "why": "..."}. `project` is the folder the asking session sat in (recall drops fragments
tagged for other projects unless the message names one). `as_of` is when the question was really asked: the arm runs on the store as it was then (as_of_db), so a miss is
measured against that store, not against the fragments later written about the miss itself (the "Emma" miss today
finds #14609, which records that very miss). `kind: none` has no gold: anything shown counts as intrusion
chars. Gold files hold real prompts and stay outside the repo.

Arms: `today` = the store's own `recall` (BM25 + bge-small embeddings, fused). More come with steps 4-5.
Scored: gold reached in the top `limit`, characters shown, and paired against a baseline arm with `compare`.
"""
import argparse
import json
import os
import sqlite3
import subprocess
import sys

EXE = os.environ.get("PERSISTENCE_STORE_EXE", "/opt/persistence/store/persistence-store")
# As the live store service runs (persistence-store.service): recall without these is BM25 alone.
EMBED_ENV = {"PERSISTENCE_EMBEDDING_ENABLED": "true", "PERSISTENCE_EMBEDDING_APIBASEURL": "http://127.0.0.1:8081/v1",
             "PERSISTENCE_EMBEDDING_MODEL": "bge-small-en-v1.5", "PERSISTENCE_SELECTEDLOCALPEER": "John"}


def snapshot(live, out):
    src = sqlite3.connect(f"file:{live}?mode=ro", uri=True)
    dst = sqlite3.connect(out)
    src.backup(dst)
    dst.close()
    src.close()


COLUMNS = ("FragmentType", "Status", "Content", "Summary", "AddressedTo", "MessageId", "RelayDepth", "SupersededById",
           "SessionId", "TouchedSessionId", "EnteredBy", "AttributedNote", "Importance", "Confidence", "IsProtected",
           "IsDeleted", "LastModifiedUtc", "Notes")


def ts(when):
    """ISO time, either 'T' or ' ' form, cut to seconds: CreatedUtc text compares in time order once both are cut."""
    return when.replace("Z", "").replace("T", " ")[:19]


def as_of_db(snap, as_of):
    """The store as it was at `as_of`, rebuilt from a snapshot and its audit log, cached beside it. Fragments created
    later are deleted; a fragment changed later gets back the OldData of its first later change (status, content,
    type...), so a message archived after the question, or a fragment edited after it, reads as it did then
    (#14594 was archived 5 minutes after John's question; #14110 gained the "Emma" line hours after). A rolled-back
    fragment's embedding is dropped, being of the newer text. Not rolled back: tags, concepts, facts."""
    out = f"{snap}.asof-{ts(as_of).replace(' ', 'T').replace(':', '')}.db"
    if os.path.exists(out):
        return out
    snapshot(snap, out + ".tmp")
    c = sqlite3.connect(out + ".tmp")
    cut = ts(as_of)
    c.execute("DELETE FROM ContextFragments WHERE replace(substr(CreatedUtc,1,19),'T',' ') > ?", (cut,))
    later = c.execute("""SELECT TargetId, OldData FROM AuditLogs a WHERE TargetType='ContextFragmentEntity' AND EventType=1
                         AND replace(substr(CreatedUtc,1,19),'T',' ') > ? AND Id = (SELECT min(Id) FROM AuditLogs b
                         WHERE b.TargetType=a.TargetType AND b.TargetId=a.TargetId AND b.EventType=1
                         AND replace(substr(b.CreatedUtc,1,19),'T',' ') > ?)""", (cut, cut)).fetchall()
    rolled = 0
    for fid, old in later:
        d = json.loads(old or "{}")
        cols = [k for k in COLUMNS if k in d]
        if not cols:
            continue
        n = c.execute(f"UPDATE ContextFragments SET {', '.join(k + '=?' for k in cols)} WHERE Id=?",
                      [d[k] for k in cols] + [fid]).rowcount
        if n and "Content" in d:
            c.execute("DELETE FROM FragmentEmbeddings WHERE ContextFragmentId=?", (fid,))
        rolled += n
    c.commit()
    c.close()
    os.replace(out + ".tmp", out)
    print(f"(store as of {as_of}: {rolled} fragments rolled back)", file=sys.stderr)
    return out


def arm_today(item, db, limit, exclude):
    args = [EXE, "--db", db, "recall", "--text", "-", "--limit", str(limit), "--json"]
    if exclude:
        args += ["--exclude", ",".join(map(str, exclude))]
    if item.get("project"):
        args += ["--project", item["project"]]      # the asking session's folder: recall leaves other projects out
    # As the prompt hook calls it (hook.py recall): one wander, another project's best match, comes back after the
    # `limit` here's, and each shows 300 characters. Utterances (--said) are left out: no gold is a ChatMessage.
    args += ["--wander", os.environ.get("PERSISTENCE_RECALL_WANDER", "1"), "--snippet", "300"]
    r = subprocess.run(args, input=item["query"], capture_output=True, text=True, env={**os.environ, **EMBED_ENV},
                       timeout=300)
    if r.returncode != 0:
        raise RuntimeError(f"recall failed on {item['id']}: {r.stderr[-500:]}")
    return [(f["id"], len(f.get("content") or "")) for f in json.loads(r.stdout or "[]")]


ARMS = {"today": arm_today}


def run(gold_path, db, arm, limit, out):
    items = [json.loads(l) for l in open(gold_path, encoding="utf-8") if l.strip() and not l.startswith("//")]
    rows = []
    for it in items:
        exclude = sorted(set(it.get("exclude", [])))
        got = ARMS[arm](it, as_of_db(db, it["as_of"]) if it.get("as_of") else db, limit, exclude)
        ids = [i for i, _ in got]
        gold = set(it.get("gold", []))
        row = {"id": it["id"], "kind": it.get("kind", "sample"), "arm": arm, "limit": limit, "shown": ids,
               "gold": sorted(gold), "reached": sorted(gold & set(ids)), "chars": sum(n for _, n in got)}
        row["hit"] = bool(row["reached"]) if gold else None
        rows.append(row)
        mark = "-" if row["hit"] is None else ("HIT" if row["hit"] else "miss")
        print(f"{it['id']:<24} {mark:<5} shown {ids} gold {sorted(gold)}", flush=True)
    scored = [r for r in rows if r["hit"] is not None]
    print(f"\n{arm}@{limit}: {sum(r['hit'] for r in scored)}/{len(scored)} reached gold; "
          f"{sum(r['chars'] for r in rows)} chars shown; "
          f"{sum(r['chars'] for r in rows if r['kind'] == 'none')} on no-gold prompts")
    if out:
        with open(out, "a", encoding="utf-8") as f:
            for r in rows:
                f.write(json.dumps(r) + "\n")
    return rows


def main(argv):
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("snapshot")
    s.add_argument("live")
    s.add_argument("out")
    r = sub.add_parser("run")
    r.add_argument("gold")
    r.add_argument("db")
    r.add_argument("--arm", default="today", choices=sorted(ARMS))
    r.add_argument("--limit", type=int, default=3)
    r.add_argument("--out")
    a = p.parse_args(argv)
    if a.cmd == "snapshot":
        snapshot(a.live, a.out)
    else:
        run(a.gold, a.db, a.arm, a.limit, a.out)


if __name__ == "__main__":
    main(sys.argv[1:])
