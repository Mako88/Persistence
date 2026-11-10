#!/usr/bin/env python3
"""The recall A/B (docs/design/concept-graph.md, build step 6): each arm's recall on a gold set, scored per store.

    eval.py snapshot <live db> <out db>         a consistent copy (sqlite backup API); arms never touch a live store
    eval.py run <gold.jsonl> <db> [--arm today|rerank] [--limit 3] [--pool 30] [--floor F] [--out results.jsonl]
    eval.py compare <results.jsonl> today@3 rerank@3     paired: N made right, M made wrong
    eval.py floor <results.jsonl> [--arm rerank]          a relevance floor swept over the saved pool scores

A gold line: {"id": "emma", "query": "...", "as_of": "2026-11-10T00:30:00Z", "gold": [7153], "kind": "miss|sample|none",
"project": "pawsitivity", "why": "..."}. `project` is the folder the asking session sat in (recall drops fragments
tagged for other projects unless the message names one). `as_of` is when the question was really asked: the arm runs on the store as it was then (as_of_db), so a miss is
measured against that store, not against the fragments later written about the miss itself (the "Emma" miss today
finds #14609, which records that very miss). `kind: none` has no gold: anything shown counts as intrusion
chars. Gold files hold real prompts and stay outside the repo.

Arms: `today` = the store's own `recall` as the hook calls it (BM25 + bge-small embeddings, fused, importance-weighted);
`rerank` = step 4, that pool widened to 30 and re-scored by a cross-encoder (needs llama-server --reranking on :8082
with bge-reranker-v2-m3; scores of the whole pool are kept, so a relevance floor can be swept without rerunning).
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


def store_recall(item, db, limit, exclude, wander=None):
    """The store's own recall, called as the prompt hook calls it (hook.py recall): fused BM25 + bge-small embeddings,
    weighted by importance, the asking session's project only, plus `wander` of another project's best matches after
    the `limit` here's. Utterances (--said) are left out: no gold is a ChatMessage. Fragments as dicts, in order."""
    args = [EXE, "--db", db, "recall", "--text", "-", "--limit", str(limit), "--json", "--snippet", "300",
            "--wander", str(wander if wander is not None else os.environ.get("PERSISTENCE_RECALL_WANDER", "1"))]
    if exclude:
        args += ["--exclude", ",".join(map(str, exclude))]
    if item.get("project"):
        args += ["--project", item["project"]]      # the asking session's folder: recall leaves other projects out
    r = subprocess.run(args, input=item["query"], capture_output=True, text=True, env={**os.environ, **EMBED_ENV},
                       timeout=300)
    if r.returncode != 0:
        raise RuntimeError(f"recall failed on {item['id']}: {r.stderr[-500:]}")
    return json.loads(r.stdout or "[]")


def arm_today(item, db, limit, exclude, opts):
    return [(f["id"], len(f.get("content") or ""), None) for f in store_recall(item, db, limit, exclude)], None


RERANK_URL = os.environ.get("RECALL_RERANK_URL", "http://127.0.0.1:8082/v1/rerank")
QUERY_CHARS, DOC_CHARS = 600, 1000      # a pair must fit the re-ranker's 1024-token batch (bge-reranker-v2-m3)


def rerank(query, docs):
    import urllib.request
    body = json.dumps({"query": query[-QUERY_CHARS:], "documents": [d[:DOC_CHARS] for d in docs]}).encode()
    req = urllib.request.Request(RERANK_URL, body, {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as resp:
        out = json.load(resp)["results"]
    scores = [None] * len(docs)
    for r in out:
        scores[r["index"]] = r["relevance_score"]
    return scores


def arm_rerank(item, db, limit, exclude, opts):
    """Build step 4, standard retrieval: the store's fused pool widened to `pool` (30), re-scored by a cross-encoder
    (bge-reranker-v2-m3 behind llama-server --reranking), best `limit` kept, and none under `floor` when one is set.
    The query's last 600 characters are used: John's messages run long and end with what he is asking."""
    pool = store_recall(item, db, opts.get("pool", 30), exclude)
    if not pool:
        return [], []
    scores = rerank(item["query"], [f.get("content") or "" for f in pool])
    ranked = sorted(zip(pool, scores), key=lambda x: -x[1])
    floor = opts.get("floor")
    kept = [(f, s) for f, s in ranked if floor is None or s >= floor][:limit]
    return [(f["id"], len(f.get("content") or ""), round(s, 3)) for f, s in kept], \
        [[f["id"], round(s, 3), len(f.get("content") or "")] for f, s in ranked]


ARMS = {"today": arm_today, "rerank": arm_rerank}


def run(gold_path, db, arm, limit, out, opts=None):
    items = [json.loads(l) for l in open(gold_path, encoding="utf-8") if l.strip() and not l.startswith("//")]
    rows = []
    for it in items:
        exclude = sorted(set(it.get("exclude", [])))
        got, pool = ARMS[arm](it, as_of_db(db, it["as_of"]) if it.get("as_of") else db, limit, exclude, opts or {})
        ids = [i for i, _, _ in got]
        gold = set(it.get("gold", []))
        row = {"id": it["id"], "kind": it.get("kind", "sample"), "arm": arm, "limit": limit, "shown": ids,
               "gold": sorted(gold), "reached": sorted(gold & set(ids)), "chars": sum(n for _, n, _ in got),
               "scores": [sc for _, _, sc in got], "pool": pool, "opts": opts or {}}
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


def load_rows(path, arm, limit):
    rows = [json.loads(l) for l in open(path, encoding="utf-8") if l.strip()]
    return {r["id"]: r for r in rows if r["arm"] == arm and r["limit"] == limit}


def compare(path, a, b):
    """Arms paired on the same questions: "N made right, M made wrong" (design step 6). a and b are arm@limit."""
    (aa, al), (ba, bl) = [(x.split("@")[0], int(x.split("@")[1])) for x in (a, b)]
    A, B = load_rows(path, aa, al), load_rows(path, ba, bl)
    both = [i for i in A if i in B and A[i]["hit"] is not None]
    right = [i for i in both if B[i]["hit"] and not A[i]["hit"]]
    wrong = [i for i in both if A[i]["hit"] and not B[i]["hit"]]
    print(f"{a}: {sum(A[i]['hit'] for i in both)}/{len(both)}   {b}: {sum(B[i]['hit'] for i in both)}/{len(both)}")
    print(f"{b} over {a}: {len(right)} made right {right}, {len(wrong)} made wrong {wrong}")
    for name, R in ((a, A), (b, B)):
        print(f"{name}: {sum(r['chars'] for r in R.values())} chars, "
              f"{sum(r['chars'] for r in R.values() if r['kind'] == 'none')} on no-gold")


def floor_sweep(path, arm, limit):
    """For each candidate floor: gold reached in the top `limit` of the pool scored over it, and what is shown."""
    R = load_rows(path, arm, limit)
    scores = sorted({s for r in R.values() for _, s, _ in (r.get("pool") or [])})
    if not scores:
        print("no pool scores (the rerank arm saves them)")
        return
    lo, hi = scores[0], scores[-1]
    for t in [None] + [round(lo + (hi - lo) * k / 12, 2) for k in range(1, 12)]:
        hit = n = chars = nonechars = empty = 0
        for r in R.values():
            kept = [(i, s, c) for i, s, c in (r.get("pool") or []) if t is None or s >= t][:limit]
            empty += not kept
            ch = sum(c for _, _, c in kept)
            chars += ch
            if r["gold"]:
                n += 1
                hit += bool(set(r["gold"]) & {i for i, _, _ in kept})
            else:
                nonechars += ch
        print(f"floor {str(t):>7}: {hit}/{n} reached, {chars} chars, {nonechars} on no-gold, {empty} prompts shown nothing")


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
    r.add_argument("--pool", type=int, default=30, help="rerank: candidates taken from the store's recall")
    r.add_argument("--floor", type=float, help="rerank: show nothing scored under this")
    c = sub.add_parser("compare")
    c.add_argument("results")
    c.add_argument("a", help="arm@limit, e.g. today@3")
    c.add_argument("b")
    f = sub.add_parser("floor")
    f.add_argument("results")
    f.add_argument("--arm", default="rerank")
    f.add_argument("--limit", type=int, default=3)
    a = p.parse_args(argv)
    if a.cmd == "snapshot":
        snapshot(a.live, a.out)
    elif a.cmd == "compare":
        compare(a.results, a.a, a.b)
    elif a.cmd == "floor":
        floor_sweep(a.results, a.arm, a.limit)
    else:
        run(a.gold, a.db, a.arm, a.limit, a.out, {"pool": a.pool, "floor": a.floor})


if __name__ == "__main__":
    main(sys.argv[1:])
