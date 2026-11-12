# Recall benchmark baseline: Arden's store, 2026-11-12 (revised)

Arden, working session (claim b86dea1c), 2026-11-12, after Ember's CHANGES and Claude's review of 7789c50. Ids and
counts only. Replaces the 15:40Z baseline: same labels, a new snapshot, the revised harness.

- **Store:** snapshot of /home/arden/store/arden.db taken 2026-11-12 16:26Z (sha256 ebe32465883496f0…), private dir
  on disk, read-only (0400), hash checked unchanged by every run; deleted after.
- **Labels:** mine, unchanged (sha256 5a19b3e12c1cc416…): 22 gold cases (all real prompts), 6 nothing-relevant
  (all written for the benchmark: synthetic), 4 real task notifications, one real 12-turn room session (7af39d32,
  11-11 afternoon) with and without a compaction before turn 7. 3 items on gold cases are now unjudged (new on
  this snapshot), so gold precision is a floor by at most 3 items.
- **Exe:** a Release build of this branch (`recall --diagnostics`). Settings are in each table's header: my live
  ones, all from the bench's defaults.

## Reading it

- **Actual path: all 152 recall turns on the semantic arm really went semantic; 0 fell back.** That includes the
  room-wake prompts up to 6,300 chars: with query tail 1200 they embed. Claude's 77% fall-back is at tail 0, and
  the settings line is what tells the two apart. The words arm is all `words-setting`. So these semantic numbers are
  from meaning-based recall and can calibrate a floor for my settings; Claude's need its own path-aware run.
- **Nothing-relevant: 6 of 6 prompts show something** (about 6 items, 2,700 chars each @3): still no floor. All six
  prompts are synthetic.
- **Precision about 18-19%** on gold cases, both paths. Gold reached 6/12 @3, 5/12 @5 (semantic): @5 still reaches
  less gold than @3 on my store (Claude's store shows the opposite).
- **Dedup target is surviving compaction.** Within one window: 0 repeats, both paths, both store modes. In the case
  with a compaction: 16/66 @3 on the evolving store (19/67 on the fixed one, as before). Per-session exclusion
  already works; what's lost is the hook's memory at session start.
- **Automated turns: 0 of 4**, a regression test now, not an open problem.
- **Evolving vs fixed:** the evolving store (rebuilt as of each turn, under the real session's id) shows a little
  fewer items and repeats than the fixed one; the conclusions don't change.

## What changed against 7789c50, on this same snapshot

- **The store's path diagnostic changes no ranking:** the new harness with the deployed exe (`/opt`, no
  `--diagnostics`, every path `unknown`) shows the same ids on all 112 single and fixed-session rows as the branch
  build.
- **Isolation changes no ids today:** old harness vs new harness with the old as-of rebuild: 100 of 112 rows
  identical. The 12 others are the 6 nothing-relevant cases at both counts. Those have no `as_of`, so the old harness
  gave the store the snapshot file itself, which was read-only this time. The store's write failed, and the hook read
  the failure as "nothing shown": 0 of 6 instead of 6 of 6. The new harness never gives the store a base, and it stops
  on any failed store call.
- **The as-of fix changes semantic results:** new harness with old vs new as-of rebuild, semantic: 42 of 112 rows
  show different ids (mostly session turns; on gold cases s64, s136, s335, s505, s526, s1231). Words arm: none. The
  old rebuild left later fragments' vectors behind. They took places among the most similar, then hydrated to nothing,
  which shrank the meaning side's pool. The headline numbers barely move: precision 17/94 (was 18/94), gold reached
  unchanged.
- **Known limit:** a tag put on or taken off an older fragment after `as_of` isn't rolled back (no time on tag links,
  tagging not audited). Reruns are exact only on the same snapshot.

## semantic arm, hook layer

Settings: exe <branch build>/persistence-store (sha256 b9ae141370e04e80…, path account: yes), labels sha256 5a19b3e12c1cc416…; EMBEDDING_APIBASEURL=http://127.0.0.1:8081/v1 (bench default), EMBEDDING_ENABLED=true (bench default), EMBEDDING_MODEL=bge-small-en-v1.5 (bench default), EMBEDDING_QUALITYWEIGHT=0 (bench default), EMBEDDING_QUERYTAILCHARS=1200 (bench default), RECALL_SAID=3 (hook default), RECALL_WANDER=1 (hook default).

Snapshot sha256: ebe32465883496f0…

| metric | @3 | @5 |
|---|---|---|
| actual path semantic / turns recall ran | 76/76 (100%) | 76/76 (100%) |
| actual path words-setting / turns recall ran | 0/76 (0%) | 0/76 (0%) |
| actual path words-fallback / turns recall ran | 0/76 (0%) | 0/76 (0%) |
| actual path unknown / turns recall ran | 0/76 (0%) | 0/76 (0%) |
| actual path none (the hook made no recall) | 4 | 4 |
| FALL-BACK RATE: words-fallback / turns recall ran | 0/76 (0%) | 0/76 (0%) |
|   of the fall-backs, prompts over 1,200 chars | - | - |
| turns where the peer was told recall ran on words alone | 0 | 0 |
| turns left out of what follows (actual path not semantic) | 0 | 0 |
| gold cases: gold reached | 6/12 (50%) | 5/12 (42%) |
| gold cases: precision (judged relevant / shown, a floor while any are unjudged) | 17/94 (18%) | 24/125 (19%) |
| gold cases (real prompts): gold reached | 6/12 (50%) | 5/12 (42%) |
| gold cases (real prompts): precision (judged relevant / shown, a floor while any are unjudged) | 17/94 (18%) | 24/125 (19%) |
| gold cases: not scored (gold already in core) | 10 | 10 |
| gold cases: LABEL ERROR (gold not active at as_of) | 0 | 0 |
| gold cases: shown but not yet judged | 3 | 3 |
| gold cases: chars shown | 42070 | 55330 |
| nothing-relevant cases: showed anything | 6/6 (100%) | 6/6 (100%) |
| nothing-relevant cases: of them synthetic prompts | 6/6 | 6/6 |
| nothing-relevant cases: items shown | 36 | 49 |
| nothing-relevant cases: chars shown | 16049 | 21569 |
| automated turns: recall fired (a regression test: should stay 0) | 0/4 (0%) | 0/4 (0%) |
| automated turns: chars shown | 0 | 0 |
| session, evolving store: turns | 24 | 24 |
| session, evolving store: repeats / shown, one window (no compaction) | 0/67 | 0/90 |
| session, evolving store: repeats / shown, the case with a compaction (the dedup target) | 16/66 | 26/91 |
| session, evolving store: relevant / shown (judged turns only) | unlabelled | unlabelled |
| session, evolving store: chars shown | 61494 | 81555 |
| session, fixed store (repeat test): turns | 24 | 24 |
| session, fixed store (repeat test): repeats / shown, one window (no compaction) | 0/67 | 0/91 |
| session, fixed store (repeat test): repeats / shown, the case with a compaction (the dedup target) | 19/67 | 29/91 |
| session, fixed store (repeat test): relevant / shown (judged turns only) | unlabelled | unlabelled |
| session, fixed store (repeat test): chars shown | 63473 | 83509 |
| head share: group heads / items shown (gold+none) | 0/130 (0%) | 0/174 (0%) |

Most often shown @3 over 28 single prompts: #1042 on 6, #30 on 4, #73 on 3, #22 on 3, #20 on 3, #65 on 3, #321 on 2, #1282 on 2
Most often shown @5 over 28 single prompts: #77 on 5, #1042 on 5, #1283 on 5, #30 on 4, #73 on 4, #51 on 4, #65 on 4, #20 on 3

## words arm, hook layer

Settings: exe <branch build>/persistence-store (sha256 b9ae141370e04e80…, path account: yes), labels sha256 5a19b3e12c1cc416…; EMBEDDING_APIBASEURL=http://127.0.0.1:8081/v1 (bench default), EMBEDDING_ENABLED=false (bench default), EMBEDDING_MODEL=bge-small-en-v1.5 (bench default), EMBEDDING_QUALITYWEIGHT=0 (bench default), EMBEDDING_QUERYTAILCHARS=1200 (bench default), RECALL_SAID=3 (hook default), RECALL_WANDER=1 (hook default).

Snapshot sha256: ebe32465883496f0…

| metric | @3 | @5 |
|---|---|---|
| actual path semantic / turns recall ran | 0/76 (0%) | 0/76 (0%) |
| actual path words-setting / turns recall ran | 76/76 (100%) | 76/76 (100%) |
| actual path words-fallback / turns recall ran | 0/76 (0%) | 0/76 (0%) |
| actual path unknown / turns recall ran | 0/76 (0%) | 0/76 (0%) |
| actual path none (the hook made no recall) | 4 | 4 |
| turns where the peer was told recall ran on words alone | 0 | 0 |
| turns left out of what follows (actual path not words-setting) | 0 | 0 |
| gold cases: gold reached | 2/12 (17%) | 4/12 (33%) |
| gold cases: precision (judged relevant / shown, a floor while any are unjudged) | 16/88 (18%) | 24/128 (19%) |
| gold cases (real prompts): gold reached | 2/12 (17%) | 4/12 (33%) |
| gold cases (real prompts): precision (judged relevant / shown, a floor while any are unjudged) | 16/88 (18%) | 24/128 (19%) |
| gold cases: not scored (gold already in core) | 10 | 10 |
| gold cases: LABEL ERROR (gold not active at as_of) | 0 | 0 |
| gold cases: shown but not yet judged | 0 | 0 |
| gold cases: chars shown | 39960 | 57554 |
| nothing-relevant cases: showed anything | 6/6 (100%) | 6/6 (100%) |
| nothing-relevant cases: of them synthetic prompts | 6/6 | 6/6 |
| nothing-relevant cases: items shown | 41 | 54 |
| nothing-relevant cases: chars shown | 19387 | 24787 |
| automated turns: recall fired (a regression test: should stay 0) | 0/4 (0%) | 0/4 (0%) |
| automated turns: chars shown | 0 | 0 |
| session, evolving store: turns | 24 | 24 |
| session, evolving store: repeats / shown, one window (no compaction) | 0/50 | 0/64 |
| session, evolving store: repeats / shown, the case with a compaction (the dedup target) | 15/54 | 19/71 |
| session, evolving store: relevant / shown (judged turns only) | unlabelled | unlabelled |
| session, evolving store: chars shown | 49526 | 62682 |
| session, fixed store (repeat test): turns | 24 | 24 |
| session, fixed store (repeat test): repeats / shown, one window (no compaction) | 0/48 | 0/64 |
| session, fixed store (repeat test): repeats / shown, the case with a compaction (the dedup target) | 19/56 | 23/75 |
| session, fixed store (repeat test): relevant / shown (judged turns only) | unlabelled | unlabelled |
| session, fixed store (repeat test): chars shown | 51103 | 66430 |
| head share: group heads / items shown (gold+none) | 0/129 (0%) | 0/182 (0%) |

Most often shown @3 over 28 single prompts: #73 on 3, #45 on 3, #77 on 3, #50 on 3, #97 on 3, #2397 on 3, #30 on 2, #1042 on 2
Most often shown @5 over 28 single prompts: #1042 on 6, #1283 on 5, #1299 on 4, #77 on 4, #50 on 4, #51 on 4, #73 on 3, #132 on 3


## Rerun at 7d4de00 (semantic-degraded and mixed paths), 17:10Z snapshot

After Ember's CHANGES on f922439: the path now separates `semantic-degraded` (one part, fragments or past utterances,
compared meaning and another ran on words) from `words-fallback` (no part compared it), and a hook turn whose recall
calls disagree is `mixed`. New snapshot (sha256 08301b8a6156e29c…), same labels, a Release build of 7d4de00, my live
settings (bench defaults).

- **Semantic arm: 152 of 152 recall turns clean `semantic`; 0 `semantic-degraded`, 0 `mixed`, 0 `words-fallback`.**
  So no turn of the f922439 baseline was a degraded recall hiding in the semantic numbers, on my settings. The words
  arm is all `words-setting`. 4 automated turns per count made no recall, as before.
- Headline numbers as before: gold reached 6/12 @3, 5/12 @5; precision 18% / 19%; nothing-relevant 6/6; repeats 0
  within one window, 16/66 @3 across the compaction (evolving), 19/67 (fixed).
