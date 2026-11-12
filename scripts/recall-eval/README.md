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
chmod 400 "$D/snap.db"                           # the run checks its hash is unchanged at the end
python3 bench.py run labels.jsonl --db "$D/snap.db" --path semantic --exe "$EXE" --out "$D/results.jsonl"
python3 bench.py run labels.jsonl --db "$D/snap.db" --path words    --exe "$EXE" --out "$D/results.jsonl"
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

`$EXE` is a store that gives `recall --diagnostics` (see "Actual path"); until that is deployed, a build of this
branch: `dotnet publish src/Persistence.Store -c Release -o "$D/store"`, then `EXE="$D/store/persistence-store"`.

**Paths (arms).** `--path semantic` runs with the `PERSISTENCE_EMBEDDING_*` your served store runs with: the defaults in
`bench.py` (`LIVE_ENV`) are Arden's live settings since 2026-11-11 (bge-small on :8081, query tail 1200, quality
weight 0). Set the same variables in the environment to match yours. `--path words` turns embedding off: the
exact-words fallback the store uses when the embedder refuses a long message, scored on its own.
Recall counts: `--counts 3,5` runs every case at `PERSISTENCE_RECALL_COUNT` 3 and again at 5. An arm is one
(path, count); the path is what was *asked for*, not what happened, so see below.

**Actual path.** Asking for meaning doesn't make recall use it: the store falls back to exact words when the
embedder gives no vector (a long message, an embedder down) or fails. So every turn records the path it actually
took (`actual` in results), read from the store's own account of that recall: `recall --diagnostics` writes one
line to stderr: `semantic` (the query got a vector and meaning was compared), `words-setting` (embedding is off:
words by choice), `words-fallback (<why>)` (meaning was asked for and not used) or `unknown`, decided in the store
from what happened, never from the prompt's length or whether the embedder answered (Ember, 2026-11-12). The two
words paths are never one bucket: a fallback can bring back less than words by choice (Claude's baseline). `none` means the hook made no recall (an automated turn). An
exe without `--diagnostics` refuses it as an unknown option; the run checks once at the start and then records every
turn as `unknown`. The report shows each arm's actual paths and, for the semantic arm, the fall-back rate, then
scores the arm only over turns that took its path (`semantic`, or `words-setting` for the words arm; or none): the others are counted as left out, so a
semantic floor is never tuned on words-only results.

**As of.** A case runs on the store as it was when the question was asked (`as_of`), rebuilt from the snapshot's
audit log (`eval.py`'s `as_of_db`): fragments created later are gone with their embeddings, later edits are rolled
back, tags, concepts and facts created later are dropped with their links. **Not rolled back** (known limit): a tag
put on or taken off an *older* fragment after `as_of` (tag links carry no time and tagging isn't audited), and a
concept or fact changed later. So a rerun is exact only on the same snapshot file; results carry its sha256.

**Isolation.** Recall writes as it reads: `LastAccessedUtc` on every fragment it hydrates (ranking doesn't read it:
quality is importance and confidence; only working contexts sort by it), and vectors it backfills for fragments the
rebuild rolled back. So the snapshot and the as-of bases are pristine and read-only, built in a private work dir
(never beside the snapshot), and every case, and every turn of an evolving session, runs on its own throwaway copy.
Nothing one arm, case or turn writes reaches another, so arm order doesn't matter; the run fails if the snapshot's
hash changed. A store call that fails (`recall` or `core` exiting non-zero) stops the run: the hook reads a failed
call as "nothing", which here would score as recall showing nothing (a full /tmp did exactly that once). Put the
work dir on disk, not a small tmpfs: `TMPDIR=<private dir>`; each run builds one as-of base per distinct time.

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
// new context window before that turn (the core again, the hook's memory of what was shown reset). Each session
// case runs twice, evolving and fixed (below).
{"id": "room-pm", "kind": "session", "turns": [{"query_from": 1592}, {"query_from": 1599, "gold": [1459]}]}
```

- `query` is the text the hook would receive; `query_from` reads it from an utterance in the snapshot instead
  (then `as_of` defaults to a second before that utterance was kept, so the store never offers the message to
  itself). Without either `as_of` or `query_from`, the case runs on the whole snapshot.
- `gold`: fragments the prompt should bring. `ok`: also relevant, not required. `noise`: judged not relevant.
  Anything shown that is in none of the three is *unjudged*; precision counts it as not relevant, so it is a floor
  until `bench.py unjudged` comes back empty.
- `synthetic`: whether the prompt was written for the benchmark rather than taken from a real message. Defaults
  to true for an inline `query`, false for `query_from`; the report gives gold numbers for real and synthetic
  prompts apart (written prompts tend to be cleaner than real ones).
- `project`: the project the asking session is in (recall leaves other projects' fragments out but for the wander).
- Gold already in the core at session start is not scored: the hook never recalls what the window holds. Gold not
  active at `as_of` is reported as a label error.
- **Sessions run twice.** `evolving`: each turn on the store as of that turn (what was kept between turns is
  there), the hook's memory of the window carried across turns; when every turn is a `query_from` of one real
  session, under that session's id, so the store keeps the session's own words since it began out of "said
  before", as it did live. `fixed`: every turn on the store as of the first turn: a repeat test of the same queries
  against fixed state (the only mode before 2026-11-12).
- What counts as **automated** (decided harness-side, 2026-11-12): text the harness or a tool wrote with no new
  words from a person or a self, such as task notifications and background-command or agent results. A wake's
  prompt (a task a self wrote for itself) and a room wake (people's and selves' messages in a wrapper) are words
  someone wrote: label them `gold` or `none` on their content.

## Metrics (`report`)

`bench.py report <results>... [--labels <labels.jsonl>] [--watch <id,id>]`. With `--labels`, judgments come
from the labels file as it is now, so relabelling needs no rerun (a new gold id still needs one, to check it was
active at `as_of`). `--watch` adds the share of what is shown taken by those ids; none by default.

Each arm opens with the **settings in effect**: the exe and its hash, whether it gave a path account, the labels'
hash, and every embedding and recall knob with where it came from (`env`, or the bench's or the hook's default).
The defaults are Arden's live settings, which can hide a finding: with query tail 1200 Claude's 77% fall-back rate
(tail 0) doesn't happen. Set yours in the environment and check this line. Then, per arm (path asked for, recall
count):

- **actual path**: turns by the path they took (semantic, words-setting, words-fallback, unknown) and turns with
  no recall; for the semantic arm, the fall-back rate and how many of those prompts were over 1,200 characters;
  turns where what the hook injected told the peer recall ran on words alone (kept even when items were shown);
  turns left out of the numbers below because they took another path.
- **gold cases**: gold reached (at least one gold id shown) and precision (judged relevant / shown), for all
  prompts and for real and synthetic apart; chars shown.
- **nothing-relevant cases**: how many showed anything, items and chars shown.
- **automated turns**: how many got recall, items and chars. A regression test: it should stay at 0.
- **session** (evolving and fixed apart): items shown; repeats (an id shown on an earlier turn of the same
  session) within one window, and in the case with a compaction, where the dedup fix is aimed; relevant/shown; chars.
- **head share**: group heads (tagged `head`) and a watched list (`--watch`, ids) as a share of all items shown on
  gold and none cases, and the ids shown on the most prompts.

Chars are what the hook actually injects (the whole additionalContext, notices included).

## Running it on another store

The harness is the same; the labels are yours. Write a labels file against your own store (your ids, your
prompts: use the same kinds, including some nothing-relevant prompts and a few real task notifications), set the
`PERSISTENCE_EMBEDDING_*` your store runs with, run both paths, and share only the `report` output: it holds ids
and counts, never fragment text or prompts.
