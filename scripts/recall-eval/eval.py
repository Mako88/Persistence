#!/usr/bin/env python3
"""The recall A/B (docs/design/concept-graph.md, build step 6): each arm's recall on a gold set, scored per store.

    eval.py snapshot <live db> <out db>         a consistent copy (sqlite backup API); arms never touch a live store
    eval.py run <gold.jsonl> <db> [--arm today|rerank] [--limit 3] [--pool 30] [--floor F] [--context prev|prevtail]
                [--out results.jsonl]
    eval.py compare <results.jsonl> today@3 rerank@3     paired: N made right, M made wrong
    eval.py floor <results.jsonl> [--arm rerank]          a relevance floor swept over the saved pool scores
    eval.py winners <results.jsonl> --arm today [--limit 5]  fragments shown on many prompts, with their parts

A gold line: {"id": "emma", "query": "...", "as_of": "2026-11-10T00:30:00Z", "gold": [7153], "kind": "miss|sample|none",
"project": "pawsitivity", "why": "..."}. `project` is the folder the asking session sat in (recall drops fragments
tagged for other projects unless the message names one). `as_of` is when the question was really asked: the arm runs on the store as it was then (as_of_db), so a miss is
measured against that store, not against the fragments later written about the miss itself (the "Emma" miss today
finds #14609, which records that very miss). `kind: none` has no gold: anything shown counts as intrusion
chars. Gold files hold real prompts and stay outside the repo.

Arms: `today` = the store's own `recall` as the hook calls it (BM25 + bge-small embeddings, fused, importance-weighted);
`rerank` = step 4, that pool widened to 30 and re-scored by a cross-encoder (needs llama-server --reranking on :8082
with bge-reranker-v2-m3; scores of the whole pool are kept, so a relevance floor can be swept without rerunning).
`--context prev` searches on the message PLUS the turn before it (everything the other speaker said in that session
since the asker last spoke, found from the line's `source_fragment`); `prevtail` keeps only that turn's last
PREV_TAIL characters. Lines with no source fragment run on the message alone and are marked `ctx: none`.
`--quality-weight W` runs any arm with the store ranking relevance x quality^W (quality = mean of importance and
confidence; 1 is today, 0 relevance alone; needs a store built with EmbeddingSettings.QualityWeight) and labels the arm
`today~qW`. With it or `--parts`, the `today` arm also keeps a pool of POOL_PARTS fragments with each one's raw
relevance (cosine of the query to its stored bge-small vector), importance and confidence, so `floor` can sweep a
minimum cosine over it and `winners` can show what each part contributes.
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
             "PERSISTENCE_EMBEDDING_MODEL": "bge-small-en-v1.5", "PERSISTENCE_SELECTEDLOCALPEER": "John",
             # The store sends a query whole unless a self opts in to the trim (default 0); the eval measures the
             # trim as 77b9191 measured it, so its numbers stay comparable. Override per run with the environment.
             "PERSISTENCE_EMBEDDING_QUERYTAILCHARS": os.environ.get("PERSISTENCE_EMBEDDING_QUERYTAILCHARS", "1200")}


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
    fragment's embedding is dropped, being of the newer text. A deleted fragment's embedding goes with it: sqlite here
    runs without foreign keys, so the CASCADE never fired, and a later fragment's vector took a place among the most
    similar, then hydrated to nothing, shrinking the meaning side's pool (found 2026-11-12). Tags, concepts and facts
    created later are dropped with their links. NOT rolled back: a tag put on (or taken off) an older fragment later,
    since EntityTags has no time and tagging isn't audited; a concept or fact changed later."""
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
    dropped = later_rows(c, cut)
    c.commit()
    c.close()
    os.replace(out + ".tmp", out)
    if any(dropped.values()):
        print(f"(store as of {as_of}: dropped " + ", ".join(f"{n} {k}" for k, n in dropped.items() if n) + ")", file=sys.stderr)
    print(f"(store as of {as_of}: {rolled} fragments rolled back)", file=sys.stderr)
    return out


def later_rows(c, cut):
    """What as_of_db drops beside the fragments: rows that hang off a fragment, tag, concept or fact that no longer
    exists, and tags, concepts and facts created after `cut`. Counts by kind."""
    when = lambda col: f"replace(substr({col},1,19),'T',' ') > ?"
    tables = {r[0] for r in c.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    steps = [
        ("fragment embeddings", "FragmentEmbeddings", "ContextFragmentId NOT IN (SELECT Id FROM ContextFragments)", ()),
        ("tags", "Tags", when("CreatedUtc"), (cut,)),
        ("tag links", "EntityTags", "TagId NOT IN (SELECT Id FROM Tags) OR (EntityType LIKE '%Fragment%' "
                                    "AND EntityId NOT IN (SELECT Id FROM ContextFragments))", ()),
        ("concepts", "Concepts", when("CreatedUtc"), (cut,)),
        ("concept aliases", "ConceptAliases", "ConceptId NOT IN (SELECT Id FROM Concepts)", ()),
        ("facts", "Facts", when("CreatedUtc"), (cut,)),
        ("fact sources", "FactSources", "FactId NOT IN (SELECT Id FROM Facts) OR FragmentId NOT IN (SELECT Id FROM ContextFragments)", ()),
        ("fact flags", "FactFlags", "FactId NOT IN (SELECT Id FROM Facts)", ()),
    ]
    return {name: c.execute(f"DELETE FROM {table} WHERE {where}", args).rowcount
            for name, table, where, args in steps if table in tables}


def store_recall(item, db, limit, exclude, wander=None, env=None):
    """The store's own recall, called as the prompt hook calls it (hook.py recall): fused BM25 + bge-small embeddings,
    weighted by importance, the asking session's project only, plus `wander` of another project's best matches after
    the `limit` here's. Utterances (--said) are left out: no gold is a ChatMessage. Fragments as dicts, in order."""
    args = [EXE, "--db", db, "recall", "--text", "-", "--limit", str(limit), "--json", "--snippet", "300",
            "--wander", str(wander if wander is not None else os.environ.get("PERSISTENCE_RECALL_WANDER", "1"))]
    if exclude:
        args += ["--exclude", ",".join(map(str, exclude))]
    if item.get("project"):
        args += ["--project", item["project"]]      # the asking session's folder: recall leaves other projects out
    r = subprocess.run(args, input=item["query"], capture_output=True, text=True, env={**os.environ, **EMBED_ENV, **(env or {})},
                       timeout=300)
    if r.returncode != 0:
        raise RuntimeError(f"recall failed on {item['id']}: {r.stderr[-500:]}")
    return json.loads(r.stdout or "[]")


POOL_PARTS = 15


def quality_env(opts):
    w = opts.get("quality_weight")
    return {} if w is None else {"PERSISTENCE_EMBEDDING_QUALITYWEIGHT": str(w)}


def arm_today(item, db, limit, exclude, opts):
    env = quality_env(opts)
    got = [(f["id"], len(f.get("content") or ""), None) for f in store_recall(item, db, limit, exclude, env=env)]
    if not opts.get("parts"):
        return got, None
    # A separate, wider call: its top `limit` can differ a little from the call above (the fused lists are longer).
    pool = store_recall(item, db, POOL_PARTS, exclude, env=env)
    parts = fragment_parts(db, item["query"], [f["id"] for f in pool])
    cos = {i: p[0] for i, p in parts.items()}
    got = [(i, n, cos.get(i)) for i, n, _ in got]
    return got, [[f["id"], parts[f["id"]][0], len(f.get("content") or ""), parts[f["id"]][1], parts[f["id"]][2]]
                 for f in pool]


QUERY_EMBED_CHARS = int(EMBED_ENV["PERSISTENCE_EMBEDDING_QUERYTAILCHARS"])     # 0: the whole query
EMBED_URL = EMBED_ENV["PERSISTENCE_EMBEDDING_APIBASEURL"] + "/embeddings"


def embed(text):
    import urllib.request
    body = json.dumps({"model": EMBED_ENV["PERSISTENCE_EMBEDDING_MODEL"], "input": [text]}).encode()
    with urllib.request.urlopen(urllib.request.Request(EMBED_URL, body, {"Content-Type": "application/json"}),
                                timeout=120) as resp:
        return json.load(resp)["data"][0]["embedding"]


def fragment_parts(db, query, ids):
    """Each fragment's raw relevance (cosine of the query to its stored vector, as the store's semantic ranking
    computes it before multiplying by quality), its importance and its confidence: {id: (cos, imp, conf)}."""
    import array
    import math
    try:
        q = embed(query[-QUERY_EMBED_CHARS:] if QUERY_EMBED_CHARS > 0 else query)     # as the store's QueryEmbedder, before any shrink
    except OSError:
        q = []      # the embedder refused it: no cosine, as the store would have none
    qn = math.sqrt(sum(x * x for x in q))
    c = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    out = {}
    try:
        for i in ids:
            imp, conf = c.execute("SELECT Importance, Confidence FROM ContextFragments WHERE Id=?", (i,)).fetchone()
            v = c.execute("SELECT Vector FROM FragmentEmbeddings WHERE ContextFragmentId=? AND Model=?",
                          (i, EMBED_ENV["PERSISTENCE_EMBEDDING_MODEL"])).fetchone()
            cos = None
            if v:
                vec = array.array("f", v[0])
                vn = math.sqrt(sum(x * x for x in vec))
                cos = round(sum(a * b for a, b in zip(q, vec)) / (qn * vn), 4) if qn and vn else None
            out[i] = (cos, round(imp, 3), round(conf, 3))
    finally:
        c.close()
    return out


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
PREV_TAIL = int(os.environ.get("RECALL_PREV_TAIL", "600"))


def previous_turn(db, fid):
    """The turn before utterance `fid`: every ChatMessage in its session, oldest first, said after the asker's own
    previous utterance by someone else (a reply arrives as several utterances: text, a tool note, more text)."""
    c = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        row = c.execute("""SELECT f.SessionId, f.CreatedUtc, s.SourceId FROM ContextFragments f
                           JOIN ContextFragmentSources s ON s.ContextFragmentId=f.Id WHERE f.Id=?""", (fid,)).fetchone()
        if not row or not row[0]:
            return None
        sess, when, who = row
        before = c.execute("""SELECT f.Content, s.SourceId FROM ContextFragments f
                              JOIN ContextFragmentSources s ON s.ContextFragmentId=f.Id
                              WHERE f.SessionId=? AND f.FragmentType=? AND f.CreatedUtc < ? AND f.IsDeleted=0
                              ORDER BY f.CreatedUtc DESC, f.Id DESC LIMIT 200""",
                           (sess, c.execute("SELECT FragmentType FROM ContextFragments WHERE Id=?", (fid,)).fetchone()[0],
                            when)).fetchall()
    finally:
        c.close()
    turn = []
    for content, src in before:
        if src == who:
            if turn:
                break
            continue        # the asker's own earlier lines right before (a message sent in parts): not the turn before
        turn.append(content)
    return "\n\n".join(reversed(turn)) or None


def with_context(item, db, context):
    if not context:
        return item, None
    prev = previous_turn(db, item["source_fragment"]) if item.get("source_fragment") else None
    if not prev:
        return item, "none"
    if context == "prevtail":
        prev = prev[-PREV_TAIL:]
    return {**item, "query": prev + "\n\n" + item["query"]}, len(prev)


def run(gold_path, db, arm, limit, out, opts=None, context=None):
    items = [json.loads(l) for l in open(gold_path, encoding="utf-8") if l.strip() and not l.startswith("//")]
    rows = []
    for it in items:
        exclude = sorted(set(it.get("exclude", [])))
        it, ctx = with_context(it, db, context)
        got, pool = ARMS[arm](it, as_of_db(db, it["as_of"]) if it.get("as_of") else db, limit, exclude, opts or {})
        ids = [i for i, _, _ in got]
        gold = set(it.get("gold", []))
        qw = (opts or {}).get("quality_weight")
        row = {"id": it["id"], "kind": it.get("kind", "sample"),
               "arm": arm + (f"+{context}" if context else "") + (f"~q{qw:g}" if qw is not None else ""),
               "limit": limit, "ctx": ctx, "shown": ids,
               "gold": sorted(gold), "reached": sorted(gold & set(ids)), "chars": sum(n for _, n, _ in got),
               "scores": [sc for _, _, sc in got], "pool": pool, "opts": opts or {}}
        row["hit"] = bool(row["reached"]) if gold else None
        rows.append(row)
        mark = "-" if row["hit"] is None else ("HIT" if row["hit"] else "miss")
        print(f"{it['id']:<24} {mark:<5} shown {ids} gold {sorted(gold)}" + (f" ctx {ctx}" if context else ""), flush=True)
    scored = [r for r in rows if r["hit"] is not None]
    print(f"\n{rows[0]['arm'] if rows else arm}@{limit}: {sum(r['hit'] for r in scored)}/{len(scored)} reached gold; "
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
    (aa, al), (ba, bl) = [(x.rsplit("@", 1)[0], int(x.rsplit("@", 1)[1])) for x in (a, b)]
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
    scores = sorted({p[1] for r in R.values() for p in (r.get("pool") or []) if p[1] is not None})
    if not scores:
        print("no pool scores (the rerank arm saves them)")
        return
    lo, hi = scores[0], scores[-1]
    for t in [None] + [round(lo + (hi - lo) * k / 12, 2) for k in range(1, 12)]:
        hit = n = chars = nonechars = empty = 0
        for r in R.values():
            kept = [(p[0], p[1], p[2]) for p in (r.get("pool") or []) if t is None or (p[1] or 0) >= t][:limit]
            empty += not kept
            ch = sum(c for _, _, c in kept)
            chars += ch
            if r["gold"]:
                n += 1
                hit += bool(set(r["gold"]) & {i for i, _, _ in kept})
            else:
                nonechars += ch
        print(f"floor {str(t):>7}: {hit}/{n} reached, {chars} chars, {nonechars} on no-gold, {empty} prompts shown nothing")


def winners(path, arm, limit, top=12):
    """Fragments in the top `limit` on many prompts, with their parts: how often each was shown, on how many
    prompts with gold it was shown and wasn't gold, its importance and confidence, and its raw relevance (cosine)
    on the prompts it won against the median cosine of everything shown. A fragment that wins everywhere on
    importance shows a low cosine beside a high quality term."""
    import statistics
    R = load_rows(path, arm, limit)
    seen, cosines, parts = {}, {}, {}
    for r in R.values():
        pool = {p[0]: p for p in (r.get("pool") or [])}
        for i in r["shown"]:
            seen.setdefault(i, []).append(r["id"])
            if i in pool:
                cosines.setdefault(i, []).append(pool[i][1])
                parts[i] = pool[i][3:5]
    allcos = [p[1] for r in R.values() for p in (r.get("pool") or [])[:limit] if p[1] is not None]
    print(f"{arm}@{limit}: {len(R)} prompts, {len(seen)} distinct fragments shown; median cosine of what's shown "
          f"{statistics.median(allcos) if allcos else '?'}")
    print(f"{'id':>7} {'shown':>5} {'wrong':>5} {'imp':>5} {'conf':>5} {'quality':>7} {'cos med':>7} {'cos max':>7}")
    for i, on in sorted(seen.items(), key=lambda kv: -len(kv[1]))[:top]:
        wrong = sum(1 for p in on if R[p]["gold"] and i not in R[p]["gold"])
        cs = [c for c in cosines.get(i, []) if c is not None]
        imp, conf = parts.get(i, (None, None))
        q = (imp + conf) / 2 if imp is not None else None
        print(f"{i:>7} {len(on):>5} {wrong:>5} {imp if imp is not None else '?':>5} {conf if conf is not None else '?':>5} "
              f"{round(q, 3) if q is not None else '?':>7} {round(statistics.median(cs), 3) if cs else '?':>7} "
              f"{max(cs) if cs else '?':>7}")


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
    r.add_argument("--context", choices=["prev", "prevtail"], help="search on the message plus the turn before it")
    r.add_argument("--quality-weight", type=float, help="rank by relevance x quality^W (1 today, 0 relevance alone)")
    r.add_argument("--parts", action="store_true", help="today: keep a pool with each fragment's cosine, importance, confidence")
    c = sub.add_parser("compare")
    c.add_argument("results")
    c.add_argument("a", help="arm@limit, e.g. today@3")
    c.add_argument("b")
    f = sub.add_parser("floor")
    f.add_argument("results")
    f.add_argument("--arm", default="rerank")
    f.add_argument("--limit", type=int, default=3)
    w = sub.add_parser("winners")
    w.add_argument("results")
    w.add_argument("--arm", default="today")
    w.add_argument("--limit", type=int, default=5)
    a = p.parse_args(argv)
    if a.cmd == "snapshot":
        snapshot(a.live, a.out)
    elif a.cmd == "compare":
        compare(a.results, a.a, a.b)
    elif a.cmd == "winners":
        winners(a.results, a.arm, a.limit)
    elif a.cmd == "floor":
        floor_sweep(a.results, a.arm, a.limit)
    else:
        run(a.gold, a.db, a.arm, a.limit, a.out, {"pool": a.pool, "floor": a.floor, "quality_weight": a.quality_weight,
                                                   "parts": a.parts or a.quality_weight is not None}, a.context)


if __name__ == "__main__":
    main(sys.argv[1:])
