# Gold-set scores: rule grouper on synth-v1 (eval split)

Pipeline: `Comparison.Of` with the rule grouper, settings from `tools/OneDiff.GoldSets` (`Pipeline.Settings`), scored by the fragmentation definition in `measurement.md` (changes and display regions counted apart; pairs with no difference left out of boundary and fragmentation).
Set: `synth-v1`, manifest sha256 `e11b5614d5cd967d4a3feac46e636156fb0beb0e741f96a28bfe7236c3d402a8`, eval documents only: 210 documents, 1,050 pairs, 7 languages. Scored once, after the grouping thresholds were fixed on the dev split (`94026ec084850e98eb9ca06d9d299773f6ffa45b9583ef43e8315922a4bbe7e2`, 42 documents, 210 pairs, disjoint from eval by `check-split`).
Method: `OneDiff.GoldSets score`, bootstrap over documents, 10,000 resamples, seed 20261104, 95% percentile intervals. Verification passed on every eval and dev pair with no fallback. These are the floors.

## All pairs

| Row | Eval [95% CI] | Dev [95% CI] |
|---|---|---|
| align F1 same | 0.9996 [0.9994, 0.9998] | 0.9996 [0.9991, 0.9999] |
| align F1 edited | 0.9733 [0.9659, 0.9798] | 0.9776 [0.9622, 0.9895] |
| align F1 moved | 0.9900 [0.9797, 0.9980] | 0.9861 [0.9518, 1.0000] |
| align P moved | 0.9802 [0.9602, 0.9960] | 0.9861 [0.9518, 1.0000] |
| align R moved | 1.0000 [1.0000, 1.0000] | 0.9861 [0.9518, 1.0000] |
| align F1 split | 0.9880 [0.9720, 1.0000] | 1.0000 [1.0000, 1.0000] |
| align F1 merged | 1.0000 [1.0000, 1.0000] | 0.9524 [0.8235, 1.0000] |
| align F1 unrelated | 0.9279 [0.9121, 0.9426] | 0.9352 [0.9003, 0.9640] |
| boundary F1 | 0.9115 [0.9007, 0.9218] | 0.9123 [0.8916, 0.9305] |
| boundary precision | 0.9118 [0.9008, 0.9222] | 0.9138 [0.8936, 0.9317] |
| boundary recall | 0.9112 [0.9005, 0.9213] | 0.9109 [0.8896, 0.9296] |
| frag.sentence (changes per changed sentence) | 1.0047 [1.0028, 1.0070] | 1.0012 [1.0000, 1.0039] |
| regions.sentence (display regions per changed sentence) | 1.0037 [1.0019, 1.0057] | 1.0000 [1.0000, 1.0000] |
| frag.unit (changes per gold unit) | 1.1665 [1.1387, 1.1972] | 1.1245 [1.0800, 1.1780] |
| regions.unit (display regions per gold unit) | 1.0259 [1.0194, 1.0331] | 1.0220 [1.0091, 1.0384] |
| missed.sentence | 0.0068 [0.0038, 0.0102] | 0.0060 [0.0011, 0.0127] |
| pairs with no difference (left out) | 8.0000 [3.0000, 14.0000] | 1.0000 [0.0000, 3.0000] |
| verify pass | 1.0000 [1.0000, 1.0000] | 1.0000 [1.0000, 1.0000] |
| verify fallback | 0.0000 [0.0000, 0.0000] | 0.0000 [0.0000, 0.0000] |

## Per language (eval)

| Lang | boundary F1 | boundary P | boundary R | frag.sentence | regions.sentence | frag.unit | regions.unit | missed.sentence |
|---|---|---|---|---|---|---|---|---|
| en | 0.9128 [0.8865, 0.9361] | 0.9142 [0.8877, 0.9379] | 0.9113 [0.8850, 0.9356] | 1.0019 [1.0000, 1.0059] | 1.0000 [1.0000, 1.0000] | 1.1542 [1.0918, 1.2291] | 1.0336 [1.0148, 1.0589] | 0.0132 [0.0000, 0.0282] |
| de | 0.9183 [0.8967, 0.9382] | 0.9117 [0.8863, 0.9345] | 0.9249 [0.9046, 0.9434] | 1.0150 [1.0059, 1.0266] | 1.0150 [1.0059, 1.0266] | 1.2116 [1.1307, 1.3144] | 1.0488 [1.0247, 1.0801] | 0.0106 [0.0020, 0.0221] |
| fr | 0.9239 [0.9083, 0.9373] | 0.9239 [0.9083, 0.9373] | 0.9239 [0.9083, 0.9373] | 1.0037 [1.0000, 1.0098] | 1.0037 [1.0000, 1.0098] | 1.1772 [1.0936, 1.2863] | 1.0197 [1.0054, 1.0392] | 0.0000 [0.0000, 0.0000] |
| es | 0.9440 [0.9202, 0.9632] | 0.9464 [0.9223, 0.9657] | 0.9416 [0.9176, 0.9613] | 1.0036 [1.0000, 1.0094] | 1.0000 [1.0000, 1.0000] | 1.1291 [1.0649, 1.2071] | 1.0116 [1.0037, 1.0216] | 0.0072 [0.0017, 0.0142] |
| zh | 0.8929 [0.8556, 0.9257] | 0.8952 [0.8566, 0.9296] | 0.8907 [0.8549, 0.9223] | 1.0054 [1.0000, 1.0124] | 1.0054 [1.0000, 1.0124] | 1.1708 [1.1023, 1.2480] | 1.0269 [1.0104, 1.0456] | 0.0107 [0.0018, 0.0225] |
| ja | 0.8798 [0.8359, 0.9184] | 0.8819 [0.8384, 0.9199] | 0.8778 [0.8335, 0.9170] | 1.0017 [1.0000, 1.0056] | 1.0017 [1.0000, 1.0056] | 1.1640 [1.1145, 1.2227] | 1.0250 [1.0127, 1.0394] | 0.0017 [0.0000, 0.0058] |
| ar | 0.9133 [0.8972, 0.9311] | 0.9124 [0.8965, 0.9296] | 0.9142 [0.8974, 0.9330] | 1.0035 [1.0000, 1.0090] | 1.0017 [1.0000, 1.0058] | 1.1667 [1.0962, 1.2591] | 1.0204 [1.0059, 1.0399] | 0.0052 [0.0000, 0.0116] |

## Per edit type (eval, single-type pairs)

| Edit | boundary F1 | boundary P | boundary R | frag.sentence | regions.sentence | frag.unit | regions.unit | missed.sentence |
|---|---|---|---|---|---|---|---|---|
| cascade | 0.5479 | 0.5479 | 0.5479 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.0000 |
| clause-rewrite | 0.6844 | 0.6615 | 0.7088 | 1.0769 | 1.0769 | 1.0989 | 1.0989 | 0.0000 |
| format | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.0000 |
| merge | - | - | - | - | - | - | - | - |
| move | 0.7748 | 0.7748 | 0.7748 | - | - | 1.0000 | 1.0000 | - |
| move-edit | 0.8257 | 0.8279 | 0.8235 | 1.0143 | 1.0000 | 1.0000 | 1.0000 | 0.0000 |
| para-delete | 0.8922 | 0.8922 | 0.8922 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.0000 |
| para-insert | 0.8020 | 0.8020 | 0.8020 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.0000 |
| sentence-delete | 0.7888 | 0.8038 | 0.7744 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.0366 |
| sentence-insert | 0.8734 | 0.8625 | 0.8846 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.0000 |
| split | - | - | - | - | - | - | - | - |
| table-cell | 0.9648 | 1.0000 | 0.9320 | 1.0000 | 1.0000 | 1.3107 | 1.3107 | 0.0680 |
| table-row-delete | 0.9829 | 0.9829 | 0.9829 | 1.0000 | 1.0000 | 2.9487 | 1.0000 | 0.0171 |
| table-row-insert | 0.9926 | 0.9926 | 0.9926 | 1.0000 | 1.0000 | 2.9778 | 1.0000 | 0.0074 |
| term-rename | 0.9908 | 0.9919 | 0.9898 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.0000 |
| term-swap | 0.9880 | 0.9880 | 0.9880 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.0000 |
| value-amount | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.0000 |
| value-date | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.0000 |
| value-duration | 0.9798 | 0.9741 | 0.9855 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.0000 |
| value-percentage | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.0000 |

Per-edit rows carry intervals in the JSON; they are wide for the small slices and omitted here. The mixed pairs (`mixed`): boundary F1 0.9067, frag.sentence 1.0066, missed.sentence 0.0066.

## Reading the rows

- `frag.unit` above 1 comes from whole-node changes: an inserted or deleted table row is one change per cell and row node (about 3 per gold row unit), shown as one display region (`regions.unit`).
- 26 changed sentences are missed. 21 are text that occurs more than once in the document (table cells with equal values, repeated template sentences): the aligner pairs the other copy, so the shown change is equal in text but sits elsewhere. The other five are alignment cases of the same family (a split or cell link chosen across near-duplicate neighbours). None is a grouping case; see the follow-ups in the unit report.
- 8 eval pairs have identical old and new documents (a defined-term rename undone by a second rename); their unit labels are left out of the boundary and fragmentation counts, and they are not left out of the alignment rows.
