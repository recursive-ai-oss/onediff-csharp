# synth-v1 eval: change semantics

Scored once on the synth-v1 **eval** split (set hash `e11b5614d5cd967d4a3feac46e636156fb0beb0e741f96a28bfe7236c3d402a8`, 1,050 pairs, seven languages). Thresholds and rules were tuned on the dev split only. Pipeline: Strict settings with cascades collapsed and typed changes on (`score --settings`). Intervals are 95% bootstrap intervals, 10,000 resamples over documents, seed 20261104. Row definitions are in [measurement.md](../measurement.md) (Semantic rows). x64 Ryzen 9 7950X3D, .NET 10.

## Exact

| Check | Result |
|---|---|
| Verify (Strict and Legal) | 1.0, fallback 0 |
| Seeded property sweeps (invertibility, grouping, semantics), 60,000 cases each | 0 failures |
| PDF gate set, every 5th pair (222 pairs, Legal and Strict) | 0 failures |
| JSON digest of 21 eval pairs, x64 and arm64, two runs each | identical |

## Semantic rows

| Row | Score | 95% CI |
|---|---|---|
| Type accuracy, all typed units | .9986 | [.9971, .9997] |
| Resolved-value accuracy, all typed units | .9983 | [.9966, .9997] |
| Amount: type / value | 1.0 / .9965 | value [.9887, 1] |
| Date, duration, percentage, defined term, number | 1.0 | — |
| Reference | .9869 | [.9727, .9970] |
| Type coverage | .9993 | — |
| Cascade group precision / recall | .9778 / .9809 | [.960, .993] / [.964, .994] |
| Rename group precision / recall | 1.0 / 1.0 | — |
| Substantive precision / recall | .9986 / .9989 | — |

Pairs with two successive renames of one term (22, a generator artefact) are left out of the rename rows (`pairs.renamechain`).

## Existing rows (no regression)

| Row | Score | 95% CI |
|---|---|---|
| Boundary F1 | .9144 | [.9036, .9246] |
| frag.sentence | 1.0047 | [1.0028, 1.0070] |
| frag.unit | 1.1712 | [1.1423, 1.2027] |
| missed.sentence | .0018 | [.0003, .0038] |
| Alignment F1 edited / moved / unrelated | .979 / .988 / .928 | — |

## Known limits

- The remaining cascade misses are Japanese reference edits merged with a neighbouring adverb change into one unit.
- The semantic passes add about 4% to compare time on a flat 565-change contract, and nothing measurable on a renumbered one (indicative, local).
