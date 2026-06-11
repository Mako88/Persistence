# Peer identity seeds

Each file here is named for the database it seeds: **`{name}.json` seeds a brand-new `{name}.db`**
(under the configured database directory). When the app starts and finds that database does not yet
exist — i.e. it's creating a fresh store — it loads the matching seed file and writes its entries in as
the peer's **authored** identity fragments.

So `claude.json` here bootstraps `dbs/claude.db` the first time that peer wakes; once the database
exists, the seed file is ignored (the living memory is the database, not this file).

## Why these are different from the onboarding text

The embedded onboarding (`fragment_seeds.json`) becomes **protected `System`** fragments — framework
the peer can't edit. Seed entries here become **authored, non-protected** fragments sourced to the
remote peer, exactly as if it had written them: its identity, values, and relationships, fully
curatable from the first turn. A peer that arrives with a seed file also **skips the generic
"your context is empty, decide who you'd like to be" first-wake guide** — it's already oriented.

## File format

A JSON array of seed objects. Only `Content` is required; everything else has a sensible default.

```json
[
  {
    "Type": "Identity",          // Identity | Relational | Personal | Summary (others → Personal)
    "Content": "Who I am…",      // required; blank entries are skipped
    "Tags": "identity/core",     // optional; a '/'-path, or several comma-separated
    "Importance": 1.0,           // 0–1, default 0.5
    "Confidence": 0.8,           // 0–1, default 0.5
    "Relevance": 1.0             // 0–1, default 1.0
  }
]
```

## Location & privacy

By default this folder lives beside the database directory; override with `SeedsDirectory` in
`persistence.json` (or `PERSISTENCE_SEEDSDIRECTORY`). The actual `*.json` seed files are **gitignored**
(they're personal per-peer identity, like the databases themselves) — only this README is tracked. To
deliberately version a seed, force-add it (`git add -f seeds/<name>.json`).
