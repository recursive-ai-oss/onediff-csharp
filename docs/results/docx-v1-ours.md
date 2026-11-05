# OneDiff on the DOCX gold set

Rows for OneDiff on `docx-v1` (the 1,050 evaluation pairs, rendered to Word files in six looks, `Legal` settings, redline built on the new document), beside the free comparison tools in docs/results/docx-v1-comparators.md. Two rows per metric:

- **(a) Engine on the Word source:** the `Comparison` made from the Word files, scored against the gold blocks (105,585 of 105,585 content nodes map to a gold block; `score --body-only`).
- **(b) Tracked output:** the DOCX redline we write, read back by the same tracked-changes reader the comparators go through.

Bootstrap: 10,000 resamples over the 210 base documents, seed 20261104. Each verdict is a paired bootstrap on the same resamples (`score`'s `pair`): *strictly better* when the interval of the difference excludes zero in our favour, *ceiling* when both tools sit at the best value within noise, *WORSE* otherwise. Indicative: one run, one machine (x64 Ryzen 9 7950X3D, shared with other jobs), SDK 10.0.403, runtime 10.0.14, OneDocument (then named OneOffice) 0.1.0-preview.2. Comparator versions are those in docs/results/docx-v1-comparators.md.

## How fragmentation rows are read

A paragraph-granular tool trivially scores one change per sentence, so `frag.sentence` and `frag.unit` are read only beside boundary F1. A comparator wins a fragmentation row only if its boundary F1 is within the confidence interval of ours or better. Otherwise the row reads "not worse (fragmentation read with boundary F1)".

### Fragmentation against OfficeIMO

| Row | Ours | OfficeIMO | Boundary F1 ours | Boundary F1 OfficeIMO | Verdict |
|---|---|---|---|---|---|
| frag.sentence, tracked output | 1.007 | 1.000 | .893 | .337 | not worse (fragmentation read with boundary F1) |
| frag.sentence, engine on source | 1.004 | 1.000 | .919 | .337 | not worse (fragmentation read with boundary F1) |

OfficeIMO marks whole paragraphs, so every changed sentence is touched by exactly one change; that is why its count is 1.000 while it finds far fewer of the actual change boundaries (.337 against .893).

## (b) Tracked output

Boundary F1 ours .893.

### Against Docxodus (its boundary F1 .677)

| Row | onediff-tracked | docxodus | onediff-tracked − docxodus | Verdict |
|---|---|---|---|---|
| align.F1.same | 1.000 [1.000, 1.000] | 1.000 [1.000, 1.000] | +0.000 [+0.000, +0.000] | ceiling (both at the best value within noise) |
| align.F1.edited | 0.985 [0.980, 0.989] | 0.952 [0.942, 0.960] | +0.033 [+0.026, +0.041] | strictly better |
| align.F1.moved | 0.978 [0.962, 0.991] | 0.919 [0.885, 0.948] | +0.059 [+0.029, +0.093] | strictly better |
| align.F1.split | 0.996 [0.986, 1.000] | 0.992 [0.972, 1.000] | +0.004 [-0.012, +0.025] | ceiling (both at the best value within noise) |
| align.F1.merged | 1.000 [1.000, 1.000] | 0.989 [0.970, 1.000] | +0.011 [+0.000, +0.030] | ceiling (both at the best value within noise) |
| align.F1.unrelated | 0.943 [0.924, 0.960] | 0.851 [0.820, 0.878] | +0.092 [+0.073, +0.114] | strictly better |
| boundary.F1 | 0.893 [0.881, 0.905] | 0.677 [0.655, 0.697] | +0.217 [+0.199, +0.235] | strictly better |
| boundary.P | 0.895 [0.882, 0.906] | 0.645 [0.618, 0.670] | +0.250 [+0.226, +0.274] | strictly better |
| boundary.R | 0.892 [0.879, 0.905] | 0.712 [0.693, 0.730] | +0.180 [+0.167, +0.194] | strictly better |
| frag.sentence | 1.007 [1.004, 1.010] | 1.137 [1.109, 1.169] | -0.131 [-0.162, -0.103] | strictly better |
| frag.unit | 1.170 [1.143, 1.199] | 1.386 [1.345, 1.431] | -0.217 [-0.254, -0.184] | strictly better |
| missed.sentence | 0.001 [0.000, 0.003] | 0.029 [0.023, 0.036] | -0.028 [-0.035, -0.022] | strictly better |

### Against OfficeIMO (its boundary F1 .337)

| Row | onediff-tracked | officeimo | onediff-tracked − officeimo | Verdict |
|---|---|---|---|---|
| align.F1.same | 1.000 [1.000, 1.000] | 1.000 [1.000, 1.000] | +0.000 [-+0.000, +0.000] | ceiling (both at the best value within noise) |
| align.F1.edited | 0.985 [0.980, 0.989] | 0.934 [0.923, 0.944] | +0.051 [+0.042, +0.062] | strictly better |
| align.F1.moved | 0.978 [0.962, 0.991] | 0.000 [0.000, 0.000] | +0.978 [+0.962, +0.991] | strictly better |
| align.F1.split | 0.996 [0.986, 1.000] | 0.000 [0.000, 0.000] | +0.996 [+0.986, +1.000] | strictly better |
| align.F1.merged | 1.000 [1.000, 1.000] | 0.000 [0.000, 0.000] | +1.000 [+1.000, +1.000] | strictly better |
| align.F1.unrelated | 0.943 [0.924, 0.960] | 0.731 [0.694, 0.766] | +0.212 [+0.182, +0.244] | strictly better |
| boundary.F1 | 0.893 [0.881, 0.905] | 0.337 [0.312, 0.364] | +0.556 [+0.526, +0.584] | strictly better |
| boundary.P | 0.895 [0.882, 0.906] | 0.341 [0.316, 0.368] | +0.553 [+0.524, +0.581] | strictly better |
| boundary.R | 0.892 [0.879, 0.905] | 0.333 [0.307, 0.360] | +0.559 [+0.528, +0.588] | strictly better |
| frag.sentence | 1.007 [1.004, 1.010] | 1.000 [1.000, 1.000] | +0.007 [+0.004, +0.010] | not worse (fragmentation read with boundary F1) |
| frag.unit | 1.170 [1.143, 1.199] | 1.215 [1.184, 1.249] | -0.046 [-0.059, -0.033] | strictly better |
| missed.sentence | 0.001 [0.000, 0.003] | 0.049 [0.041, 0.059] | -0.048 [-0.058, -0.039] | strictly better |

### Against WmlComparer (its boundary F1 .561)

| Row | onediff-tracked | wmlcomparer | onediff-tracked − wmlcomparer | Verdict |
|---|---|---|---|---|
| align.F1.same | 1.000 [1.000, 1.000] | 1.000 [0.999, 1.000] | +0.000 [+0.000, +0.000] | strictly better |
| align.F1.edited | 0.985 [0.980, 0.989] | 0.928 [0.916, 0.939] | +0.057 [+0.046, +0.069] | strictly better |
| align.F1.moved | 0.978 [0.962, 0.991] | 0.000 [0.000, 0.000] | +0.978 [+0.962, +0.991] | strictly better |
| align.F1.split | 0.996 [0.986, 1.000] | 0.000 [0.000, 0.000] | +0.996 [+0.986, +1.000] | strictly better |
| align.F1.merged | 1.000 [1.000, 1.000] | 0.000 [0.000, 0.000] | +1.000 [+1.000, +1.000] | strictly better |
| align.F1.unrelated | 0.943 [0.924, 0.960] | 0.740 [0.703, 0.776] | +0.203 [+0.173, +0.235] | strictly better |
| boundary.F1 | 0.893 [0.881, 0.905] | 0.561 [0.526, 0.594] | +0.332 [+0.301, +0.366] | strictly better |
| boundary.P | 0.895 [0.882, 0.906] | 0.546 [0.513, 0.578] | +0.349 [+0.318, +0.381] | strictly better |
| boundary.R | 0.892 [0.879, 0.905] | 0.577 [0.540, 0.612] | +0.315 [+0.281, +0.351] | strictly better |
| frag.sentence | 1.007 [1.004, 1.010] | 1.060 [1.048, 1.073] | -0.053 [-0.066, -0.042] | strictly better |
| frag.unit | 1.170 [1.143, 1.199] | 1.314 [1.278, 1.352] | -0.144 [-0.167, -0.123] | strictly better |
| missed.sentence | 0.001 [0.000, 0.003] | 0.043 [0.035, 0.051] | -0.042 [-0.050, -0.034] | strictly better |

### Against LibreOffice Compare (its boundary F1 .435)

| Row | onediff-tracked | libreoffice | onediff-tracked − libreoffice | Verdict |
|---|---|---|---|---|
| align.F1.same | 1.000 [1.000, 1.000] | 0.883 [0.869, 0.897] | +0.116 [+0.103, +0.131] | strictly better |
| align.F1.edited | 0.985 [0.980, 0.989] | 0.802 [0.771, 0.829] | +0.183 [+0.155, +0.213] | strictly better |
| align.F1.moved | 0.978 [0.962, 0.991] | 0.000 [0.000, 0.000] | +0.978 [+0.962, +0.991] | strictly better |
| align.F1.split | 0.996 [0.986, 1.000] | 0.000 [0.000, 0.000] | +0.996 [+0.986, +1.000] | strictly better |
| align.F1.merged | 1.000 [1.000, 1.000] | 0.000 [0.000, 0.000] | +1.000 [+1.000, +1.000] | strictly better |
| align.F1.unrelated | 0.943 [0.924, 0.960] | 0.094 [0.082, 0.106] | +0.850 [+0.830, +0.868] | strictly better |
| boundary.F1 | 0.893 [0.881, 0.905] | 0.435 [0.408, 0.462] | +0.459 [+0.430, +0.486] | strictly better |
| boundary.P | 0.895 [0.882, 0.906] | 0.353 [0.327, 0.382] | +0.541 [+0.511, +0.570] | strictly better |
| boundary.R | 0.892 [0.879, 0.905] | 0.565 [0.524, 0.604] | +0.327 [+0.291, +0.365] | strictly better |
| frag.sentence | 1.007 [1.004, 1.010] | 1.050 [1.038, 1.064] | -0.043 [-0.057, -0.032] | strictly better |
| frag.unit | 1.170 [1.143, 1.199] | 1.350 [1.308, 1.396] | -0.180 [-0.208, -0.154] | strictly better |
| missed.sentence | 0.001 [0.000, 0.003] | 0.220 [0.183, 0.261] | -0.219 [-0.259, -0.181] | strictly better |

## (a) Engine on the Word source

Boundary F1 ours .919.

### Against Docxodus (its boundary F1 .677)

| Row | onediff-engine | docxodus | onediff-engine − docxodus | Verdict |
|---|---|---|---|---|
| align.F1.same | 1.000 [1.000, 1.000] | 1.000 [1.000, 1.000] | +0.000 [-+0.000, +0.000] | ceiling (both at the best value within noise) |
| align.F1.edited | 0.980 [0.974, 0.985] | 0.952 [0.942, 0.960] | +0.028 [+0.019, +0.038] | strictly better |
| align.F1.moved | 0.988 [0.975, 0.998] | 0.919 [0.885, 0.948] | +0.069 [+0.038, +0.104] | strictly better |
| align.F1.split | 0.996 [0.986, 1.000] | 0.992 [0.972, 1.000] | +0.004 [-0.012, +0.025] | ceiling (both at the best value within noise) |
| align.F1.merged | 1.000 [1.000, 1.000] | 0.989 [0.970, 1.000] | +0.011 [+0.000, +0.030] | ceiling (both at the best value within noise) |
| align.F1.unrelated | 0.929 [0.912, 0.944] | 0.851 [0.820, 0.878] | +0.078 [+0.050, +0.109] | strictly better |
| boundary.F1 | 0.919 [0.909, 0.929] | 0.677 [0.655, 0.697] | +0.242 [+0.225, +0.260] | strictly better |
| boundary.P | 0.938 [0.928, 0.947] | 0.645 [0.618, 0.670] | +0.293 [+0.270, +0.317] | strictly better |
| boundary.R | 0.901 [0.889, 0.912] | 0.712 [0.693, 0.730] | +0.189 [+0.175, +0.204] | strictly better |
| frag.sentence | 1.004 [1.002, 1.006] | 1.137 [1.109, 1.169] | -0.134 [-0.165, -0.106] | strictly better |
| frag.unit | 1.179 [1.149, 1.211] | 1.386 [1.345, 1.431] | -0.208 [-0.245, -0.174] | strictly better |
| missed.sentence | 0.002 [0.000, 0.004] | 0.029 [0.023, 0.036] | -0.027 [-0.034, -0.021] | strictly better |

### Against OfficeIMO (its boundary F1 .337)

| Row | onediff-engine | officeimo | onediff-engine − officeimo | Verdict |
|---|---|---|---|---|
| align.F1.same | 1.000 [1.000, 1.000] | 1.000 [1.000, 1.000] | +0.000 [-+0.000, +0.000] | ceiling (both at the best value within noise) |
| align.F1.edited | 0.980 [0.974, 0.985] | 0.934 [0.923, 0.944] | +0.046 [+0.036, +0.056] | strictly better |
| align.F1.moved | 0.988 [0.975, 0.998] | 0.000 [0.000, 0.000] | +0.988 [+0.975, +0.998] | strictly better |
| align.F1.split | 0.996 [0.986, 1.000] | 0.000 [0.000, 0.000] | +0.996 [+0.986, +1.000] | strictly better |
| align.F1.merged | 1.000 [1.000, 1.000] | 0.000 [0.000, 0.000] | +1.000 [+1.000, +1.000] | strictly better |
| align.F1.unrelated | 0.929 [0.912, 0.944] | 0.731 [0.694, 0.766] | +0.197 [+0.162, +0.235] | strictly better |
| boundary.F1 | 0.919 [0.909, 0.929] | 0.337 [0.312, 0.364] | +0.582 [+0.553, +0.609] | strictly better |
| boundary.P | 0.938 [0.928, 0.947] | 0.341 [0.316, 0.368] | +0.596 [+0.568, +0.624] | strictly better |
| boundary.R | 0.901 [0.889, 0.912] | 0.333 [0.307, 0.360] | +0.568 [+0.538, +0.596] | strictly better |
| frag.sentence | 1.004 [1.002, 1.006] | 1.000 [1.000, 1.000] | +0.004 [+0.002, +0.006] | not worse (fragmentation read with boundary F1) |
| frag.unit | 1.179 [1.149, 1.211] | 1.215 [1.184, 1.249] | -0.037 [-0.050, -0.024] | strictly better |
| missed.sentence | 0.002 [0.000, 0.004] | 0.049 [0.041, 0.059] | -0.048 [-0.057, -0.039] | strictly better |

### Against WmlComparer (its boundary F1 .561)

| Row | onediff-engine | wmlcomparer | onediff-engine − wmlcomparer | Verdict |
|---|---|---|---|---|
| align.F1.same | 1.000 [1.000, 1.000] | 1.000 [0.999, 1.000] | +0.000 [+0.000, +0.000] | strictly better |
| align.F1.edited | 0.980 [0.974, 0.985] | 0.928 [0.916, 0.939] | +0.052 [+0.041, +0.063] | strictly better |
| align.F1.moved | 0.988 [0.975, 0.998] | 0.000 [0.000, 0.000] | +0.988 [+0.975, +0.998] | strictly better |
| align.F1.split | 0.996 [0.986, 1.000] | 0.000 [0.000, 0.000] | +0.996 [+0.986, +1.000] | strictly better |
| align.F1.merged | 1.000 [1.000, 1.000] | 0.000 [0.000, 0.000] | +1.000 [+1.000, +1.000] | strictly better |
| align.F1.unrelated | 0.929 [0.912, 0.944] | 0.740 [0.703, 0.776] | +0.188 [+0.152, +0.227] | strictly better |
| boundary.F1 | 0.919 [0.909, 0.929] | 0.561 [0.526, 0.594] | +0.358 [+0.327, +0.391] | strictly better |
| boundary.P | 0.938 [0.928, 0.947] | 0.546 [0.513, 0.578] | +0.392 [+0.361, +0.423] | strictly better |
| boundary.R | 0.901 [0.889, 0.912] | 0.577 [0.540, 0.612] | +0.324 [+0.289, +0.360] | strictly better |
| frag.sentence | 1.004 [1.002, 1.006] | 1.060 [1.048, 1.073] | -0.056 [-0.069, -0.044] | strictly better |
| frag.unit | 1.179 [1.149, 1.211] | 1.314 [1.278, 1.352] | -0.135 [-0.158, -0.114] | strictly better |
| missed.sentence | 0.002 [0.000, 0.004] | 0.043 [0.035, 0.051] | -0.041 [-0.050, -0.033] | strictly better |

### Against LibreOffice Compare (its boundary F1 .435)

| Row | onediff-engine | libreoffice | onediff-engine − libreoffice | Verdict |
|---|---|---|---|---|
| align.F1.same | 1.000 [1.000, 1.000] | 0.883 [0.869, 0.897] | +0.116 [+0.103, +0.131] | strictly better |
| align.F1.edited | 0.980 [0.974, 0.985] | 0.802 [0.771, 0.829] | +0.177 [+0.151, +0.207] | strictly better |
| align.F1.moved | 0.988 [0.975, 0.998] | 0.000 [0.000, 0.000] | +0.988 [+0.975, +0.998] | strictly better |
| align.F1.split | 0.996 [0.986, 1.000] | 0.000 [0.000, 0.000] | +0.996 [+0.986, +1.000] | strictly better |
| align.F1.merged | 1.000 [1.000, 1.000] | 0.000 [0.000, 0.000] | +1.000 [+1.000, +1.000] | strictly better |
| align.F1.unrelated | 0.929 [0.912, 0.944] | 0.094 [0.082, 0.106] | +0.835 [+0.817, +0.852] | strictly better |
| boundary.F1 | 0.919 [0.909, 0.929] | 0.435 [0.408, 0.462] | +0.484 [+0.456, +0.511] | strictly better |
| boundary.P | 0.938 [0.928, 0.947] | 0.353 [0.327, 0.382] | +0.584 [+0.555, +0.612] | strictly better |
| boundary.R | 0.901 [0.889, 0.912] | 0.565 [0.524, 0.604] | +0.336 [+0.300, +0.374] | strictly better |
| frag.sentence | 1.004 [1.002, 1.006] | 1.050 [1.038, 1.064] | -0.046 [-0.060, -0.034] | strictly better |
| frag.unit | 1.179 [1.149, 1.211] | 1.350 [1.308, 1.396] | -0.171 [-0.199, -0.146] | strictly better |
| missed.sentence | 0.002 [0.000, 0.004] | 0.220 [0.183, 0.261] | -0.219 [-0.259, -0.181] | strictly better |

## Rows that use the ceiling clause

Against Docxodus, `align.F1.same`, `align.F1.split` and `align.F1.merged`, in both row sets: both tools' intervals reach 1.0, so neither can be told from the maximum. Against OfficeIMO, `align.F1.same` is the same tie. Against WmlComparer and LibreOffice every row is strictly better. Nothing else uses the clause.

## The reader, applied to every tool

All tools' outputs, ours and the comparators', are read by one tracked-changes reader and re-scored together; the comparator rows in docs/results/docx-v1-comparators.md are the re-read values (boundary F1 .677 Docxodus, .337 OfficeIMO, .561 WmlComparer, .435 LibreOffice). The rules:

1. A run with `w:rPrChange` is a revision of its text: the text is the same in both views and the stretch is a format change.
2. A revision nested in an insertion or move-to (a deletion) or in a move-from (an insertion) is in neither view, as accept-all and reject-all resolve it.
3. A move's own change is the moved text only; insertions, deletions and format changes beside or inside it are separate changes.

## Exact gates for the same code

| Gate | Cases | Failures |
|---|---|---|
| accept-all = new, reject-all = old, docx-v1 eval, built on new and on old, Legal and Strict | 2 × 2,100 | 0 |
| schema (Open XML SDK, Office 2019), same outputs | 2 × 2,100 | 0 new errors |
| generated pairs 0–4,999, built on new and on old, Legal and Strict (accept/reject and schema) | 2 × 10,000 | 0 |

The writer lists what a revision cannot carry (`NotCarried`); the gate lets through exactly those differences (docs/word.md).

## Speed (indicative)

One tool per row: two files in, one tracked-changes file out, in process, one thread, BenchmarkDotNet with one warm-up and five single-invocation iterations; AMD Ryzen 9 7950X3D, 32 logical cores, load average about 15 during the run (other jobs); runtime 10.0.14; Docxodus 15.0.0, OfficeIMO.Word 3.5.3. The first iterations include JIT, and the intervals are wide, so read the order of magnitude only.

| Pair | OneDiff | Docxodus | OfficeIMO | OneDiff allocated | Docxodus | OfficeIMO |
|---|---|---|---|---|---|---|
| n1 (about 5 pages) | 88 ms | 26 ms | 94 ms | 13 MB | 20 MB | 50 MB |
| n8 (about 40 pages) | 194 ms | 146 ms | 339 ms | 75 MB | 94 MB | 338 MB |
| n24 (about 120 pages) | 440 ms | 502 ms | 662 ms | 224 MB | 281 MB | 747 MB |

A generated 100-page contract with about 300 changes: read both files 191 ms, compare 184 ms, write the tracked document 183 ms (about 0.56 s in all, after warm-up; the budget is 1 s).
