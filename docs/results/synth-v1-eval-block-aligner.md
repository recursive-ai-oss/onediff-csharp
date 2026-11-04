# Gold-set scores: block aligner on synth-v1 (eval split)

Pipeline: `Comparison.Of` at main 4e0338d (block aligner with the unique-partner move rule, reference grouping), settings from `tools/OneDiff.GoldSets` (`Pipeline.Settings`).
Set: `synth-v1`, manifest sha256 `e11b5614d5cd967d4a3feac46e636156fb0beb0e741f96a28bfe7236c3d402a8` (checked against `manifest.sha256` and the file hashes before the run). Eval documents only: 210 documents, 1,050 pairs, 7 languages.
Method: `OneDiff.GoldSets score`, paired bootstrap over documents, 10,000 resamples, seed 20261104, 95% percentile intervals. Verification passed on every pair with no fallback.

Reference: the reference pipeline at bd45d92 on the same set.

| Row | Block aligner [95% CI] | Reference [95% CI] | CIs |
|---|---|---|---|
| align F1 same | 0.9996 [0.9994, 0.9998] | 0.9983 [0.9979, 0.9986] | better |
| align F1 edited | 0.9733 [0.9659, 0.9798] | 0.9544 [0.9439, 0.9638] | better |
| align F1 moved | 0.9900 [0.9797, 0.9980] | 0.7391 [0.6824, 0.7888] | better |
| align F1 split | 0.9880 [0.9720, 1.0000] | 0.9880 [0.9720, 1.0000] | tie |
| align F1 merged | 1.0000 [1.0000, 1.0000] | 1.0000 [1.0000, 1.0000] | tie |
| align F1 unrelated | 0.9279 [0.9121, 0.9426] | 0.8351 [0.8076, 0.8612] | better |
| boundary F1 | 0.6729 [0.6446, 0.7003] | 0.6066 [0.5762, 0.6372] | better |
| fragmentation (sentence) | 1.3564 [1.2988, 1.4189] | 1.3140 [1.2598, 1.3720] | tie |
| fragmentation (unit) | 1.6361 [1.5670, 1.7092] | 1.6344 [1.5694, 1.7016] | tie |
| missed sentences | 0.0373 [0.0179, 0.0602] | 0.0368 [0.0171, 0.0596] | tie |
| verify fallback rate | 0 | 0 | tie |

Per language, alignment F1 (block aligner / reference):

| Lang | same | edited | moved | split | merged | unrelated |
|---|---|---|---|---|---|---|
| en | .999 / .998 | .967 / .968 | .987 / .740 | 1.000 / 1.000 | 1.000 / 1.000 | .891 / .897 |
| de | .999 / .998 | .962 / .955 | 1.000 / .809 | .974 / .974 | 1.000 / 1.000 | .905 / .831 |
| fr | 1.000 / .999 | .984 / .977 | .988 / .828 | 1.000 / 1.000 | 1.000 / 1.000 | .949 / .924 |
| es | .999 / .999 | .978 / .967 | .982 / .767 | 1.000 / 1.000 | 1.000 / 1.000 | .938 / .867 |
| zh | 1.000 / .999 | .976 / .887 | .984 / .567 | .970 / .970 | 1.000 / 1.000 | .933 / .665 |
| ja | 1.000 / .998 | .976 / .960 | 1.000 / .615 | 1.000 / 1.000 | 1.000 / 1.000 | .947 / .822 |
| ar | 1.000 / .998 | .969 / .967 | .986 / .783 | .968 / .968 | 1.000 / 1.000 | .930 / .894 |

Each language has 30 documents and 150 pairs; per-language intervals are wide for split and moved (see the full JSON rows).
