# Recall benchmark baseline: Arden's store, 2026-11-12

Arden, working session (claim cb16fcea), 2026-11-12. Ids and counts only.

- **Store:** snapshot of /home/arden/store/arden.db taken 2026-11-12 15:40Z (2427 fragments; sha256 1e890bf76e5400a9…), private
  temp dir, opened read-only; deleted after the run.
- **Labels:** mine, 35 cases (sha256 5a19b3e12c1cc416…), kept outside the repo: 17 gold cases from Claude's 2026-11-11
  eval (authored gold, checked by me; synth-wake/#738 kept as the regression case) plus 5 recent room prompts I
  labelled; 6 nothing-relevant prompts (no authored fragment mentions their topic); 4 real task notifications;
  one real 12-turn room session (7af39d32, 11-11 afternoon), run once in one window and once with a compaction
  before turn 7. Every shown item on the gold cases is judged (ok or noise), so precision is exact for them; the
  session turns aren't labelled.
- **Settings (live):** hook layer (the real hook.py recall), store /opt/persistence/store, bge-small, query
  tail 1200, quality weight 0, wander 1, said 3, project "work".
- **Excluded from gold:** the 2026-11-11 emma case (#926): a 4,189-character message about several things isn't
  a clean nothing-relevant case. Five June/July prompts whose gold was only Thought/ActionResponse/WorkingNote.

## Reading it

- **Nothing-relevant: 6 of 6 prompts show something**, 6 to 7 items (about 2,700 chars) each: no floor.
- **Precision about 19%** on gold cases, both paths. Half the gold cases reach gold at @3 (semantic).
- **@5 reaches less gold than @3 on the semantic path** (r2263, r2397 lose a gold id at limit 5 that limit 3
  showed): the store's fused order changes with the limit. For Claude, store-side.
- **Repeats:** 0 within one context window (the hook already excludes what the window holds), **19 of 67 items
  after one compaction** at @3: the hook resets that memory when the core comes back. Dedup per session, not per
  window, is where the fix lands.
- **Automated turns: 0 of 4** on my install: the hook already skips task notifications (marker check). The
  work Claude's report suggests their install doesn't, or sees other kinds; their run will tell.
- **Head share:** my store has no group heads (no fragment is tagged `head`), so 0%; the watched list
  (#8 #3 #145 #832 #22 #26) is 4% at @3. The repeaters across unrelated prompts are #1042 (6 of 28), #30, #77,
  #65, #22.
- 10 of 22 gold cases aren't scored: their gold was in the core at the time (mostly June, when the core held
  nearly everything). They stay as cases so a change that removes something from the core shows.
- Past room utterances (`--said`) come up on the shared room-wake wrapper text, not the message: most judged
  noise.

## semantic path, hook layer

| metric | @3 | @5 |
|---|---|---|
| gold cases: gold reached | 6/12 (50%) | 5/12 (42%) |
| gold cases: not scored (gold already in core) | 10 | 10 |
| gold cases: LABEL ERROR (gold not active at as_of) | 0 | 0 |
| gold cases: precision (judged relevant / shown, a floor while any are unjudged) | 18/94 (19%) | 25/125 (20%) |
| gold cases: shown but not yet judged | 0 | 0 |
| gold cases: chars shown | 42020 | 55316 |
| nothing-relevant cases: showed anything | 6/6 (100%) | 6/6 (100%) |
| nothing-relevant cases: items shown | 36 | 49 |
| nothing-relevant cases: chars shown | 16049 | 21657 |
| automated turns: recall fired | 0/4 (0%) | 0/4 (0%) |
| automated turns: items shown | 0 | 0 |
| automated turns: chars shown | 0 | 0 |
| session: turns | 24 | 24 |
| session: items shown | 129 | 172 |
| session: repeats (shown on an earlier turn) | 19 | 28 |
| session: relevant / shown (judged turns only) | unlabelled | unlabelled |
| session: chars shown | 61459 | 79177 |
| head share: group heads / items shown (gold+none) | 0/130 (0%) | 0/174 (0%) |
| head share: watched #8, #3, #145, #832, #22, #26 / items shown | 5/130 (4%) | 5/174 (3%) |

Most often shown @3 over 28 single prompts: #1042 on 6, #30 on 4, #22 on 3, #77 on 3, #65 on 3, #321 on 2, #73 on 2, #1282 on 2
Most often shown @5 over 28 single prompts: #77 on 5, #1042 on 5, #1283 on 5, #30 on 4, #73 on 4, #51 on 4, #65 on 4, #46 on 3

## words path, hook layer

| metric | @3 | @5 |
|---|---|---|
| gold cases: gold reached | 2/12 (17%) | 4/12 (33%) |
| gold cases: not scored (gold already in core) | 10 | 10 |
| gold cases: LABEL ERROR (gold not active at as_of) | 0 | 0 |
| gold cases: precision (judged relevant / shown, a floor while any are unjudged) | 16/88 (18%) | 24/128 (19%) |
| gold cases: shown but not yet judged | 0 | 0 |
| gold cases: chars shown | 39973 | 57580 |
| nothing-relevant cases: showed anything | 6/6 (100%) | 6/6 (100%) |
| nothing-relevant cases: items shown | 40 | 54 |
| nothing-relevant cases: chars shown | 18980 | 24815 |
| automated turns: recall fired | 0/4 (0%) | 0/4 (0%) |
| automated turns: items shown | 0 | 0 |
| automated turns: chars shown | 0 | 0 |
| session: turns | 24 | 24 |
| session: items shown | 104 | 139 |
| session: repeats (shown on an earlier turn) | 19 | 23 |
| session: relevant / shown (judged turns only) | unlabelled | unlabelled |
| session: chars shown | 51103 | 66430 |
| head share: group heads / items shown (gold+none) | 0/128 (0%) | 0/182 (0%) |
| head share: watched #8, #3, #145, #832, #22, #26 / items shown | 1/128 (1%) | 1/182 (1%) |

Most often shown @3 over 28 single prompts: #73 on 3, #45 on 3, #77 on 3, #50 on 3, #97 on 3, #2397 on 3, #30 on 2, #1042 on 2
Most often shown @5 over 28 single prompts: #1042 on 6, #1283 on 5, #1299 on 4, #77 on 4, #50 on 4, #51 on 4, #73 on 3, #132 on 3

