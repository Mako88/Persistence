# Recall evaluation

Two scripts, both read-only against a snapshot of a store, never the live file or the served socket.

- `eval.py`: the recall A/B from `docs/design/concept-graph.md` step 6 (arms, re-ranker, floor sweep). Its
  docstring is its manual.
- `bench.py`: the **recall benchmark**. A fixed, labelled case set, run through what a turn really gets, so a
  change to recall (a relevance floor, per-session dedup, skipping automated turns) can be shown to help or not.

## The benchmark

```sh
D=$(mktemp -d)                                   # private (700): the snapshot holds your whole store
python3 bench.py snapshot ~/store/me.db "$D/snap.db"
python3 bench.py run labels.jsonl --db "$D/snap.db" --path semantic --out "$D/results.jsonl"
python3 bench.py run labels.jsonl --db "$D/snap.db" --path words    --out "$D/results.jsonl"
python3 bench.py report "$D/results.jsonl"       # ids and numbers only: safe to share
python3 bench.py unjudged "$D/results.jsonl"     # what your labels haven't judged yet
rm -r "$D"
```

**What runs.** By default (`--layer hook`) each turn goes through the real `scripts/claude-code/hook.py recall`,
imported, with only its route to the store replaced: every store call runs the CLI (`--exe`, default
`/opt/persistence/store/persistence-store`) with `--db` on the snapshot, and the hook's state lives in a temp dir.
So the hook's gate on automated turns, its exclusion of what the context window already holds (the core's ids at
session start, then everything shown since), the wander and past utterances (`--said`) are all measured as a
session gets them. A fix in the hook or in the store shows up here; point `--exe` at a dev build to test a store
change. `--layer store` calls `recall` alone, without the hook's gate or memory.

**Paths.** `--path semantic` runs with the `PERSISTENCE_EMBEDDING_*` your served store runs with: the defaults in
`bench.py` (`LIVE_ENV`) are Arden's live settings since 2026-11-11 (bge-small on :8081, query tail 1200, quality
weight 0). Set the same variables in the environment to match yours. `--path words` turns embedding off: the
exact-words fallback the store uses when the embedder refuses a long message, scored on its own.
Recall counts: `--counts 3,5` runs every case at `PERSISTENCE_RECALL_COUNT` 3 and again at 5.

**As of.** A case runs on the store as it was when the question was asked (`as_of`), rebuilt from the snapshot's
audit log (`eval.py`'s `as_of_db`): fragments created later are gone, later edits are rolled back. The rebuilt
copies sit beside the snapshot and go with it.

The `snapshot` step also embeds every past utterance in the copy (recall otherwise embeds a few per call, so a
case's result would depend on the cases run before it). Utterances under 80 characters are never embedded.

## Labels

A JSONL file, one case per line; lines starting with `//` are comments. Labels hold real prompts, so they stay
outside the repo (`chmod 600`). Ids are your store's own: a labels file only means something against the store it
was written for.

```jsonc
{"config": {"project": "work"}}           // optional: defaults for every case
// gold: the store holds something this prompt should bring
{"id": "s1434", "kind": "gold", "query_from": 1434, "gold": [1404, 1414], "ok": [1459], "noise": [295]}
{"id": "q7", "kind": "gold", "query": "the prompt text", "as_of": "2026-11-11T14:41:28Z", "gold": [1404]}
// none: the store has nothing on it; the right answer is to show nothing
{"id": "dlq", "kind": "none", "query": "How should I configure retries on the dead letter queue?"}
// automated: text the harness or a tool wrote, with no new words from a person or self; right answer: no recall
{"id": "auto-1", "kind": "automated", "query": "<task-notification> ... </task-notification>"}
// session: consecutive turns, to count repeats; turns take gold/ok/noise too, and "compacted": true starts a
// new context window before that turn (the core again, the hook's memory of what was shown reset)
{"id": "room-pm", "kind": "session", "turns": [{"query_from": 1592}, {"query_from": 1599, "gold": [1459]}]}
```

- `query` is the text the hook would receive; `query_from` reads it from an utterance in the snapshot instead
  (then `as_of` defaults to a second before that utterance was kept, so the store never offers the message to
  itself). Without either `as_of` or `query_from`, the case runs on the whole snapshot.
- `gold`: fragments the prompt should bring. `ok`: also relevant, not required. `noise`: judged not relevant.
  Anything shown that is in none of the three is *unjudged*; precision counts it as not relevant, so it is a floor
  until `bench.py unjudged` comes back empty.
- `project`: the project the asking session is in (recall leaves other projects' fragments out but for the wander).
- Gold already in the core at session start is not scored: the hook never recalls what the window holds. Gold not
  active at `as_of` is reported as a label error.
- What counts as **automated** (decided harness-side, 2026-11-12): text the harness or a tool wrote with no new
  words from a person or a self, such as task notifications and background-command or agent results. A wake's
  prompt (a task a self wrote for itself) and a room wake (people's and selves' messages in a wrapper) are words
  someone wrote: label them `gold` or `none` on their content.

## Metrics (`report`)

Per path and recall count:

- **gold cases**: gold reached (at least one gold id shown), precision (judged relevant / shown), chars shown.
- **nothing-relevant cases**: how many showed anything, items and chars shown.
- **automated turns**: how many got recall, items and chars.
- **session**: items shown, repeats (an id shown on an earlier turn of the same session), relevant/shown, chars.
- **head share**: group heads (tagged `head`) and a watched list (`--watch`, ids) as a share of all items shown on
  gold and none cases, and the ids shown on the most prompts.

Chars are what the hook actually injects (the whole additionalContext, notices included).

## Running it on another store

The harness is the same; the labels are yours. Write a labels file against your own store (your ids, your
prompts: use the same kinds, including some nothing-relevant prompts and a few real task notifications), set the
`PERSISTENCE_EMBEDDING_*` your store runs with, run both paths, and share only the `report` output: it holds ids
and counts, never fragment text or prompts.
