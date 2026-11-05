# Results

Measured results for the text core (`RecursiveAI.OneDiff`) and the first document-engine scores, including the rows that do not meet a bar and the checks that are still open. Nothing is claimed beyond this page. Method: `docs/measurement.md`.

Status words: **pass** meets the bar; **pending** not yet run; **open** run, not yet decided or not yet final.

## Exact gates (text core)

Corpus: 9,483 code and prose version pairs and 2,599 real merge triples, manifest sha256 `087bd93a…` (pinned fetch in `tools/corpora-pins.json`). Oracles run as black-box processes: git 2.51.2, GNU diffutils 3.12, GNU patch 2.8, and the `diff-match-patch` Python package 20241021.

| Gate | Result | Reference | Verdict |
|---|---|---|---|
| Myers edit length is the minimum (`diff --minimal` length) | 9,483 / 9,483 | GNU diffutils 3.12 | pass |
| Every algorithm's script transforms old into new (Myers, patience, histogram) | 9,483 / 9,483 | — | pass |
| Unified patch round trip (make, parse, apply) | 9,483 pairs, 0 failures | — | pass |
| dmp patch round trip (make, text, parse, apply) | 9,483 pairs, 0 failures | — | pass |
| Our unified output accepted by GNU patch | 9,481 accepted, 2 skipped, 0 failures | GNU patch 2.8 | pass |
| Our unified output accepted by `git apply` | 9,481 accepted, 2 skipped, 0 failures | git 2.51.2 | pass |
| dmp patch text interop with the Python package | 9,454 agree, 29 skipped (inputs too large for that package), 0 failures | diff-match-patch 20241021 | pass |
| Three-way merge equals `git merge-file -p`, byte for byte, conflict style `merge` | 2,599 / 2,599 | git 2.51.2 | pass |
| Same, conflict style `diff3` | 2,599 / 2,599 | git 2.51.2 | pass |
| Same, conflict style `zdiff3` | 2,599 / 2,599 | git 2.51.2 | pass |
| `onediff diff -u` output accepted by GNU patch and `git apply` | 500 corpus pairs, 0 failures | GNU patch 2.8, git 2.51.2 | pass |
| `onediff merge` output equals `Diff.Merge` | 200 triples × 3 styles, byte-identical | — | pass |

The merge-file re-run also repeated the minimality and validity gates (9,483 / 9,483 each). The skipped pairs are listed in the gate output; none is a failure.

## Fuzzing (text core parsers)

A seeded mutation runner (`fuzz/OneDiff.Fuzz`, no coverage feedback): inputs are mutations of patches the library writes plus hostile cases. A crash is an exception, a broken invariant, or an input slower than 3 s. 600 s per target, seed 1, seven targets side by side, one core each, on the text-core build used for the speed rows below (37d53ec); the parsers, appliers and change-list reader have not changed since.

| Target | Duration | Executions | Crashes |
|---|---|---|---|
| unified-parse | 600 s | 273,494,302 | 0 |
| unified-apply | 600 s | 228,075,994 | 0 |
| dmp-parse | 600 s | 293,566,965 | 0 |
| dmp-apply | 600 s | 256,863,028 | 0 |
| unified-roundtrip | 600 s | 67,533,673 | 0 |
| dmp-roundtrip | 600 s | 8,743,643 | 0 |
| comparison-json (change-list reader) | 600 s | 29,836,303 | 0 |

## NativeAOT

`samples/OneDiff.AotSmoke` (references `RecursiveAI.OneDiff` and `.Documents`) published with NativeAOT on linux-x64 (gcc linker) and osx-arm64: no trim or AOT warnings; the binary runs and prints `ok`.

## Speed: text core against the managed comparators

Comparators: DiffPlex 1.9.0 and DiffMatchPatch 5.0.0 (the diff-match-patch authors' C# package), restored in the benchmark project only. Ratio = ours ÷ comparator, so lower is better. Bar: geometric mean per comparator and area at most 1.0, no row above 1.25.

**Settings.** Single-threaded, workstation non-concurrent GC; DiffMatchPatch `Diff_Timeout = 0`; no cleanup in the diff rows; DiffPlex `ignoreWhitespace: false` with an explicit word separator set; DiffMatchPatch's line mode maps lines to characters inside the timed work (its helper is not public); cleanup rows time the copy of the input diff on both sides. BenchmarkDotNet 0.15.8, one launch per row with its default warm-up and iteration counts, .NET 10.0.14, `Release`. Inputs are corpus pairs of about 2 KB (Small), 40 KB (Medium) and 300–400 KB (Large; lines and words only), named in the raw tables. The runs are of the text-core code before the merge-alignment change; that change touches only `Merge`, which has no managed comparator.

| | x64 | arm64 |
|---|---|---|
| CPU | AMD Ryzen 9 7950X3D, 16 cores / 32 threads (avx2, avx512, bmi2, fma, popcnt) | Apple M2, 8 cores (AdvSimd, AES, CRC32, DP, SHA) |
| OS | Linux (x86_64) | macOS (arm64) |
| Load average at start | 3.5 (1 min); 1.8–5.6 while the groups ran (includes the benchmark's own build and child processes) | 2.6, steady 2.1–2.7 |
| Isolation | run exclusively on the machine's benchmark queue | the Mac carries a steady background load of about 2 and the benchmark could not raise process priority ("permission denied"): these rows are less quiet than the x64 rows |

Both runs were taken once, in five groups of at most 20 minutes each (lines, words, chars, cleanup, patch). Raw BenchmarkDotNet output, the machine record and the input/edit-distance listing are kept with the run; peak working set is measured once per row in a cold child process (x64 only; the macOS counter reports 0).

### Verdicts per area

| Area | Comparator | x64 geomean | x64 worst row | arm64 geomean | arm64 worst row | Verdict |
|---|---|---|---|---|---|---|
| lines (3 algorithms × 3 sizes) | DiffPlex lines | 0.19 | 0.36 | 0.19 | 0.33 | pass |
| lines | diff-match-patch line mode | 0.49 | 0.69 | 0.55 | 0.79 | pass |
| words (3 × 3) | DiffPlex words | 0.29 | 0.82 | 0.35 | 0.91 | pass |
| chars (3 × 2) | DiffPlex characters | 0.01 | 0.08 | 0.02 | 0.08 | pass |
| chars | diff-match-patch `diff_main` | 0.20 | 0.57 | 0.24 | 0.67 | pass |
| cleanup, semantic (2 sizes) | diff-match-patch | 0.31 | 0.45 | 0.32 | 0.46 | pass |
| cleanup, efficiency (2 sizes) | diff-match-patch | 0.51 | 0.85 | 0.51 | 0.87 | pass |
| patch apply (2 sizes) | diff-match-patch | 0.61 | 0.66 | 0.63 | 0.66 | pass |
| patch make (2 sizes) | diff-match-patch | 0.83 | **1.03** | 0.83 | **1.03** | pass (geomean under 1.0; worst row under 1.25) |
| patch toText (2 sizes) | diff-match-patch | 0.20 | 0.31 | 0.19 | 0.26 | pass |
| patch fromText (2 sizes) | diff-match-patch | 0.09 | 0.11 | 0.07 | 0.09 | pass |

Rows to watch: medium patch make is at parity with diff-match-patch on both machines (1.03×; the diff dominates, and both sides run the same Myers-class search). Medium efficiency cleanup is 0.85–0.87× and its absolute time (about 0.1 µs) is near the timer's resolution, so its ratio carries more noise than the others. No row exceeded 1.25× on either machine.

### Comparability notes

- **Words.** DiffPlex cuts words at its own separators, so its word rows do not see the same tokens as ours: it reports fewer edits on the same input (Small 48 against our 57–61, Medium 60 against 69, Large 840 against 1,114). The ratios compare the time to produce each library's own script, not equal work. Lines and characters agree on edit distance between all implementations except where a heuristic algorithm (patience, histogram) finds a longer, equally valid script than Myers.
- **Not covered.** There are no word rows against diff-match-patch (it has no word mode), no large-size character rows, no large-size cleanup or patch rows, and no multi-threaded rows (the text core is single-threaded). Three-way merge has no managed comparator and is covered by the exact gate above, not a speed row.
- **Native tools** (git, GNU diff) are not compared.

### Allocations (bytes per call, x64 and arm64 agree within 1% except where noted)

| Row | Ours | Comparator | Ratio |
|---|---|---|---|
| lines, Medium, Myers | 105,928 | DiffPlex 317,592 / diff-match-patch 342,648 | 0.33 / 0.31 |
| lines, Large, Myers | 960,793 | DiffPlex 2,132,880 / diff-match-patch 2,880,414 | 0.45 / 0.33 |
| words, Large, Myers | 3,031,091 | DiffPlex 10,878,894 | 0.28 |
| chars, Medium, Myers | 1,928 | DiffPlex 3,637,006 / diff-match-patch 229,568 | 0.00 / 0.01 |
| semantic cleanup, Small | 1,184 | diff-match-patch 21,512 | 0.06 |
| efficiency cleanup, Medium | 0 | diff-match-patch 328 | 0.00 |
| patch make, Medium | 253,040 | diff-match-patch 392,096 | 0.65 |
| patch apply, Medium | 325,328 | diff-match-patch 487,464 | 0.67 |

Every measured row allocates less than its comparator (allocation ratio at most 0.67).

### Peak working set above the inputs (x64, cold child process)

| Row | Ours | DiffPlex | diff-match-patch |
|---|---|---|---|
| lines, Large (Myers) | 5.8 MB | 5.3 MB | 4.0 MB |
| words, Large (Myers) | 14.6 MB | 12.7 MB | — |
| chars, Medium (Myers) | 2.3 MB | 7.3 MB | 2.7 MB |

Memory at the largest sizes is within about 15–45% of the comparators' in the two rows where we are higher (the process baseline dominates at these sizes); it is linear in input size. No bar is set on peak memory for the text core.

## Gold sets: document engine

Set: `synth-v1`, evaluation split, manifest sha256 `e11b5614d5cd967d4a3feac46e636156fb0beb0e741f96a28bfe7236c3d402a8`: 210 documents, 1,050 labelled synthetic pairs, 7 languages. Paired bootstrap over documents, 10,000 resamples, seed 20261104, 95% percentile intervals. Full tables with per-language and per-edit-type scores: [`results/synth-v1-eval-aligner-ties.md`](results/synth-v1-eval-aligner-ties.md) (current floors), [`results/synth-v1-eval-rule-grouper.md`](results/synth-v1-eval-rule-grouper.md) and [`results/synth-v1-eval-block-aligner.md`](results/synth-v1-eval-block-aligner.md) (the aligner against the earlier reference pipeline). Grouping thresholds are tuned on a separate development split; the evaluation split is scored once per change.

| Row | Score [95% CI] | Reference / floor | Verdict |
|---|---|---|---|
| align F1, same | 0.9999 [0.9998, 1.0000] | reference pipeline 0.9983 | better than reference; floor set |
| align F1, edited | 0.9793 [0.9737, 0.9844] | 0.9544 | better; floor set |
| align F1, moved | 0.9880 [0.9749, 0.9979] | 0.7391 | better; floor set |
| align F1, split | 0.9960 [0.9862, 1.0000] | 0.9880 | tie; floor set |
| align F1, merged | 1.0000 [1.0000, 1.0000] | 1.0000 | tie; floor set |
| align F1, unrelated | 0.9278 [0.9113, 0.9434] | 0.8351 | better; floor set |
| boundary F1 | 0.9144 [0.9036, 0.9246] | block-aligner baseline 0.6729 [0.6446, 0.7003] | improved by the grouping step; floor set |
| boundary precision / recall | 0.9118 / 0.9112 | — | floor set |
| fragmentation: changes per changed sentence | 1.0047 [1.0028, 1.0070] | ideal 1.0 | floor set |
| fragmentation: changes per gold unit | 1.1712 [1.1423, 1.2027] | ideal 1.0 | floor set |
| display regions per changed sentence / per unit | 1.0037 / 1.0259 | — | reported beside fragmentation |
| missed changed sentences | 0.0018 [0.0003, 0.0038] | ideal 0 | floor set |
| verification pass / fallback rate | all pairs verified, fallback 0 | target 0 | pass |

Fragmentation counts changes, not display regions, so grouping a whole paragraph into one region cannot improve it; the definition is in [measurement.md](measurement.md#scoring). These are gold-set rows with floors, not comparator rows: no comparison against another comparison tool has been run yet, so no statement is made about any.

## Not yet run

- Document comparators (DOCX comparison tools), blind-preference rows, grids and slides rows, scan rows and the model rows: the packages that produce them are not built yet.
- Speed rows for the document engine, and a WASM timing row.
