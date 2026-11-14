# Relevance floor, Step B: Arden's store, 2026-11-14

Arden, work session (claim 46ae5670). Ids and counts only; labels and prompts stay private.

- **Store:** snapshot 6adc655ae566a3b1… (taken 2026-11-14 15:3xZ, read-only, hash checked unchanged by every run).
- **Labels:** sha256 36235a6fc783a323…, split fixed before any run (seed 20261114): gold 13 tune / 14 held, real none
  23 / 23, synthetic none 3 / 3. Grid predeclared: cos 0.50..0.80 step 0.05, z 1.0..4.0 step 0.5.
- **Exe:** a Release build of this branch (b9ae141370e04e80…); settings are my live ones (bge-small, tail 1200, W=0), @3, hook layer.
- **Floor off = main:** on all 39 tune cases, the floor-off arm and a build of main (7667529) showed the same ids and
  the same chars, and every turn's path was semantic.

## Result: no floor yet on my store

Tuning picked **cos=0.8** (the rule: at most 1 gold hit lost, fewest chars on real none). On held-out, once:
**NO GO**. It lost 1 gold hit, and real none chars went to 72% of off, not the 50% the rule needs.

Why, from the candidates the bench now records: on my store the real nothing-relevant prompts (mostly merge and
review acks) score **higher** than the gold prompts, on both signals. Acks look like the many ack-shaped fragments
the store holds. Held-out, top cosine: gold median 0.83 (0.70..0.87), real none median 0.83 (0.80..0.85); z: gold
median 2.66 (-0.49..3.16), real none 2.73 (2.00..3.48). So a floor that removes ack noise removes gold first.
The synthetic off-topic prompts sit lower (top 0.65..0.70) and would be filtered, but they are 6 written prompts.

What a floor can't reach: on held-out real none prompts, 42 of the 132 items shown (off) are past utterances ("said
before"), which have no floor; cos=0.8 cut fragments 90 -> 51 and left those 42 alone, plus whatever words found.

Limit: 39 of my 46 real none prompts are acks, and my store has no real off-topic asks. Claude's labels (claim
ba48c742: off-topic asks, few acks) are the test this result can't be: Claude runs the same grid on their tune half,
and the sections below gain theirs.

## Decision (2026-11-14, room 1571206774139134063): no floor, on either store

Neither store meets the go rule even on its tune half: real none chars came down to 75% at best on mine (cos=0.8)
and 60% on Claude's (z=3.5, review 5905945539), against the 50% needed. The floors stay in the store, **off by
default**, as tested bench tooling. My held-out half was used once (below); Claude's is untouched. The score cutoff
is the wrong tool: acks score as high as gold on both stores, and "said before" items are out of its reach. What we
try next should handle acks and repeats directly.

"Said before", from the sweep's new column (`--db` on this snapshot): on real none prompts, off shows 44 past
utterances among 131 items on tune (34%) and 42 of 132 on held-out (32%, the same as my count by hand). cos=0.8
leaves every one of them: 44 of 95 on tune, 42 of 93 on held-out. My labels carry no `class`, so the ack columns
show `-` here; Claude's split is in their review.

## Tune half (sweep)

| arm | gold reached | gold lost / gained | gold chars | real none: showed anything | real none: chars (vs off) | real none: emptied / newly shown | synth none: showed / chars | left out |
|---|---|---|---|---|---|---|---|---|
| off | 3/7 | 0 / 0 | 24062 | 23/23 | 61957/61957 (100%) | 0 / 0 | 3/3 / 6144 | 0 |
| cos=0.5 | 3/7 | 0 / 0 | 24062 | 23/23 | 61957/61957 (100%) | 0 / 0 | 3/3 / 6144 | 0 |
| cos=0.55 | 3/7 | 0 / 0 | 24062 | 23/23 | 61957/61957 (100%) | 0 / 0 | 3/3 / 6172 | 0 |
| cos=0.6 | 3/7 | 0 / 0 | 23643 | 23/23 | 61957/61957 (100%) | 0 / 0 | 3/3 / 6112 | 0 |
| cos=0.65 | 3/7 | 0 / 0 | 23226 | 23/23 | 61957/61957 (100%) | 0 / 0 | 3/3 / 6112 | 0 |
| cos=0.7 | 4/7 | 0 / 1 | 23204 | 23/23 | 61957/61957 (100%) | 0 / 0 | 3/3 / 6112 | 0 |
| cos=0.75 | 2/7 | 1 / 0 | 23184 | 23/23 | 62044/61957 (100%) | 0 / 0 | 3/3 / 6112 | 0 |
| cos=0.8 | 2/7 | 1 / 0 | 21012 | 23/23 | 46296/61957 (75%) | 0 / 0 | 3/3 / 6112 | 0 |
| z=1 | 3/7 | 0 / 0 | 24031 | 23/23 | 61957/61957 (100%) | 0 / 0 | 3/3 / 6144 | 0 |
| z=1.5 | 2/7 | 1 / 0 | 24050 | 23/23 | 61561/61957 (99%) | 0 / 0 | 3/3 / 6144 | 0 |
| z=2 | 2/7 | 1 / 0 | 23212 | 23/23 | 59929/61957 (97%) | 0 / 0 | 3/3 / 6144 | 0 |
| z=2.5 | 1/7 | 2 / 0 | 21796 | 23/23 | 58607/61957 (95%) | 0 / 0 | 3/3 / 6129 | 0 |
| z=3 | 1/7 | 2 / 0 | 21796 | 21/23 | 38940/61957 (63%) | 2 / 0 | 3/3 / 6112 | 0 |
| z=3.5 | 0/7 | 3 / 0 | 18656 | 16/23 | 29364/61957 (47%) | 7 / 0 | 3/3 / 6112 | 0 |
| z=4 | 0/7 | 3 / 0 | 18656 | 16/23 | 27557/61957 (44%) | 7 / 0 | 3/3 / 6112 | 0 |

**Pick (this split):** cos=0.8: of the arms losing at most 1 gold hit, fewest chars on real none cases (46296 of 61957).

What the floor sees (floor-off arm, first recall call's first ranking with candidates):

- gold: n 9; top cosine 0.63..0.82 (median 0.73); z 0.72..3.32 (median 1.97)
- real none: n 23; top cosine 0.79..0.85 (median 0.83); z 2.36..3.53 (median 2.73)
- synthetic none: n 3; top cosine 0.60..0.70 (median 0.60); z 1.84..2.58 (median 2.28)

## Held-out half (once, off and the picked arm)

| arm | gold reached | gold lost / gained | gold chars | real none: showed anything | real none: chars (vs off) | real none: emptied / newly shown | synth none: showed / chars | left out |
|---|---|---|---|---|---|---|---|---|
| off | 3/9 | 0 / 0 | 29573 | 23/23 | 62546/62546 (100%) | 0 / 0 | 3/3 / 8314 | 0 |
| cos=0.8 | 2/9 | 1 / 0 | 22994 | 22/23 | 45343/62546 (72%) | 1 / 0 | 3/3 / 8266 | 0 |

**Go check (cos=0.8, this split):** gold lost 1 (at most 1: yes); real none chars 45343 of 62546 (halved: NO). **NO GO.**

What the floor sees (floor-off arm, first recall call's first ranking with candidates):

- gold: n 12; top cosine 0.70..0.87 (median 0.83); z -0.49..3.16 (median 2.66)
- real none: n 23; top cosine 0.80..0.85 (median 0.83); z 2.00..3.48 (median 2.73)
- synthetic none: n 3; top cosine 0.65..0.68 (median 0.67); z 2.10..3.43 (median 2.20)
