# Pair-model teacher: dev check

`pair-teacher-v1` (mmBERT-base fine-tuned on synthetic pairs, `tools/model/PROVENANCE.md`) is asked the two questions the engine's
rules answer, and its answers are run through the unchanged pipeline on **synth-v1 dev** (set hash `94026ec0…`, 210 pairs,
42 documents, seven languages), beside the rules. Dev only: nothing here touches synth-v1 eval. Every row is the paired difference
(variant − rules) over the same documents, 10,000 bootstrap resamples over documents, seed 20261104. A variant "beats" when the 95%
interval excludes 0 on the good side, "loses" when it excludes 0 on the bad side, otherwise "ties".

Commands: `tools/model/dev-check.sh pair-teacher-v1`. All variants pass `Verify` at fallback level 0 on every dev pair
(`verify.pass` 1.0000, `verify.fallback` 0.0000). The plumbing check (`rules-table`: the rules' own decisions read back through the
answer tables) reproduces the engine with every difference exactly 0.

## What the teacher learned (offline, dev dump)

| Decision | Rules | Teacher |
|---|---|---|
| Block pair is one block edited (916 distinct dev pairs, gold link yes/no) | the rules' score is a similarity, not a yes/no; at a 0.5 cut it is 68.0% right (291 false positives) | **98.4% right** (302 TP, 15 FP, 0 FN, 599 TN); AUC on its validation slice 0.998 |
| Pairs inside the rules' ambiguity band 0.30–0.95 (830 pairs, 218 true) | not comparable at 0.5 | 815 / 830 right |
| Clause is one unit (16 dev clauses) | 16 / 16 | 16 / 16 |
| Neighbouring units belong together (29 gaps) | 18 / 29 | **29 / 29** |

The rules' 0.5 cut is only an indication: the engine does not cut there. It admits by `MatchThreshold` and structure and lets a global
assignment resolve competing candidates, which is why the real test is the pipeline rows below.

## Pipeline rows on dev (all scope, paired, 10k resamples)

| Row | Rules | Teacher (both) | Δ [95% CI] | Verdict | Judge only: Δ [CI] | Clauses only: Δ [CI] | Perfect judge + clauses (gold answers): Δ [CI] | Verdict at best |
|---|---|---|---|---|---|---|---|---|
| align.F1 same | .9999 | .9999 | 0 | same | 0 | 0 | 0 | — |
| align.F1 edited | .9829 | .9829 | 0 [−.0056, +.0049] | tie | 0 [−.0056, +.0049] | 0 | +.0053 [+.0010, +.0120] | **room: .005** |
| align.F1 moved | .9861 | .9861 | 0 | same | 0 | 0 | 0 | none (gold judge adds nothing) |
| align.F1 split / merged | 1.0000 | 1.0000 | 0 | same | 0 | 0 | 0 | at ceiling |
| align.F1 unrelated | .9380 | .9414 | +.0034 [−.0090, +.0167] | tie | +.0034 [−.0090, +.0167] | 0 | +.0195 [+.0037, +.0410] | **room: .02** |
| boundary F1 | .9150 | .9134 | −.0016 [−.0050, 0] | tie | −.0016 [−.0049, 0] | −.0001 [−.0002, 0] | −.0001 [−.0002, 0] | none |
| boundary P | .9156 | .9140 | −.0016 [−.0065, +.0016] | tie | −.0022 [−.0068, 0] | +.0005 [0, +.0018] | +.0005 [0, +.0018] | none |
| boundary R | .9144 | .9128 | −.0016 [−.0041, 0] | tie | −.0010 [−.0031, 0] | −.0007 [−.0021, 0] | −.0007 [−.0021, 0] | none |
| frag.unit | 1.1305 | 1.1305 | 0 [−.0053, +.0065] | tie | 0 [−.0053, +.0065] | 0 | −.0024 [−.0063, 0] | none |
| frag.sentence | 1.0012 | 1.0012 | 0 [−.0037, +.0035] | tie | +.0012 [0, +.0037] | −.0012 [−.0039, 0] | −.0012 [−.0039, 0] | none |
| missed.sentence | .0000 | .0000 | 0 | same | 0 | 0 | 0 | at floor |
| regions.unit | 1.0232 | 1.0232 | 0 [−.0053, +.0065] | tie | 0 | 0 | −.0098 [−.0226, −.0012] | display only |

**Verdict: the teacher beats the deterministic engine on no dev row and loses on none; every row with room is a tie, within
the interval around 0.** Per row: ties on edited, unrelated, boundary F1/P/R, frag.unit, frag.sentence and regions.unit; identical
(same) on same, moved, split, merged, missed.sentence and verification.

## Where headroom exists at all

The gold-answer columns are an upper bound for anything that only changes these two decisions (a perfect judge and perfect clause
grouping, through the same pipeline):

- **Alignment of edited and unrelated blocks:** +.005 and +.020 F1. This is the only headroom with an interval that excludes 0,
  and a perfect judge is needed to reach it; the teacher recovers +.0034 of the .0195 on unrelated (statistically a tie) and none on
  edited.
- **Boundaries:** none. A perfect clause decision moves boundary F1 by −.0001. The rules already join the right units on this data;
  the teacher's 29 / 29 gap accuracy against 18 / 29 for the rules' local join test does not show in the rows because the join
  answers only matter in a clause that is not one whole unit, and 14 of the 16 dev clauses are (the rules and the teacher both get
  all 16 whole-or-not decisions right). Dev has too few such clauses (16, of 4,100 changes) to say more.
- **Fragmentation, missed sentences, moved/split/merged/same:** at or within .01 of the best possible value.
- **Display:** a perfect judge lowers `regions.unit` by .0098 (one display region per unit); no change is added or hidden.

## Real pairs (60 pairs from real-pairs-v1, unlabelled)

Both decisions answered by the teacher (these pairs are plain text, outside the synthetic distribution) and compared with the rules
through the same pipeline; every teacher comparison verified at fallback level 0 (0 not verified).

| | Rules | Teacher | Only under rules | Only under teacher |
|---|---|---|---|---|
| Alignment links | 6,374 (2,283 from the scorer stages) | — | 470 (7.4% of all, 20.6% of scorer-stage links) | 428 |
| Changes (kind and ranges) | 11,376 | — | 2,144 (18.8%) | 1,578 |
| Pairs that differ at all | — | — | 56 of 60 | |

Scorer lookups: 11,769 answered, 0 missed; clause lookups: 424 answered, 67 not covered (the model's input is cut at 320 tokens;
those clauses keep the rules' decision). The numbers say how much the model moves the output on documents unlike its training data;
there are no labels, so they say nothing about which side is right.

## Data caveat

Evaluation and dev text share paragraphs with training (74% verbatim, `PROVENANCE.md`). A positive dev result would have been inflated;
the dev result here is a tie, so the caveat can only make the teacher look better than it is.

## Files (outside git)

`dev-check/dev-table.md` (all seven variants), `*.paired.json`, `curve.jsonl`, `chunks.log`, `real-effect.jsonl` under the model run
directory; hashes in `provenance.json`.
