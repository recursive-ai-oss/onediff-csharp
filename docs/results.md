# Results

The published results of this release: the text core's exact gates, fuzzing and speed against the managed comparators; the PDF engine's gates; the document engine's gold-set scores, including change semantics and combine; and the free DOCX comparison tools' scores on the same gold set. Losing rows and gaps are listed, not left out. Nothing is claimed beyond this page. Method: [measurement.md](measurement.md).

Status words: **pass** meets the bar; **pending** not yet run or not yet filled in; **open** run, not yet decided or not yet final; **floor set** a first score that later releases may only raise; **loses** a row where a comparator scores better.

No commercial comparison product is measured or compared against. The DOCX package (`RecursiveAI.OneDiff.Word`) is not in this release, so its rows read "pending" below.

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

A seeded mutation runner (`fuzz/OneDiff.Fuzz`, no coverage feedback): inputs are mutations of patches the library writes plus hostile cases, and for the change-list reader mutations of valid comparisons. A crash is an exception, a broken invariant, or an input slower than 3 s. 600 s per target, seed 1, on the release build (9989a33), one process per target on the arm64 machine (Apple M2, 6 cores reserved, load 2.8 before and 6.6 after).

| Target | Duration | Executions | Crashes |
|---|---|---|---|
| unified-parse | 600 s | 409,599,563 | 0 |
| unified-apply | 600 s | 367,589,317 | 0 |
| dmp-parse | 600 s | 510,064,214 | 0 |
| dmp-apply | 600 s | 462,174,804 | 0 |
| unified-roundtrip | 600 s | 90,294,084 | 0 |
| dmp-roundtrip | 600 s | 9,180,084 | 0 |
| comparison-json (change-list reader) | 600 s | 37,034,877 | 0 |

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

## PDF engine

Set: `pdf-v1`, sha256 `eb10c6b3a7af57239c71482da8d1fdd9011aaf3a3bd0036db23e2e586074e88b`: the synthetic evaluation pairs rendered to PDF (2,220 PDFs, 1,110 pairs), including 60 real version pairs (SEC EDGAR filings, Common Paper, Bonterms, YC SAFE forms and licence texts; sources and terms in `tools/fetch-real-pairs.py`). Every pair is compared with the Legal and the Strict settings.

| Gate | Result | Verdict |
|---|---|---|
| Verification passes, fallback level 0, `Check()` clean | 2,220 comparisons, 0 failures | pass |
| Marks on the new document, re-extracted with OnePDF, equal the change list (annotation text under the quadrilaterals, pop-up text, skip reasons) | 2,220 comparisons, 0 failures | pass |
| Marks on the old document, re-extracted likewise | 2,220 comparisons, 0 failures | pass |
| Second run gives byte-identical comparison JSON and marked PDFs | 2,220 comparisons, 0 failures | pass |
| Same digest of generated inputs, comparison JSON and marked PDFs on x64 and arm64 | `72923d7c…` on both | pass |
| Rendered redline PDF (OneRender 1.3.3), read back with OnePDF, equals the change list, inline and side by side each: text corpus | 638 pairs, 20,235 pages, 0 failures | pass |
| The same, pdf-v1 | all 1,110 pairs, 15,184 pages, 0 failures | pass |
| The same, pairs over 300 kB | 24 pairs, 0 failures | pass |
| Same rendered text-corpus digest on x64 and arm64 | `1cf5ca96…` on both | pass |
| HTML redlines parsed back: every mark carries its change, text equals the change list | 96,000 generated pages and 1,324 corpus pages, 0 mismatches | pass |
| HTML pages against the W3C Nu Html Checker 26.11.4 | 0 errors | pass |
| Same HTML page hashes on x64 and arm64 | identical | pass |
| Seeded property sweeps of invertibility, grouping and semantics | 60,000 cases each, 0 failures | pass |

### Reading a PDF: gold-set scores

The engine's PDF source run on every fifth pair of pdf-v1 (210 synthetic pairs, 10,000 resamples), its nodes mapped onto the gold blocks; 19,575 of 21,614 content nodes matched a gold block (the rest are page furniture and extraction artefacts), and 0 comparisons failed Verify or fell back. The column on the right is the same pairs with the gold documents as input.

| Row | PDF source (mapped) | Gold-document input | Note |
|---|---|---|---|
| align F1, edited | .909 [.884, .931] | .977 [.967, .986] | floor set |
| align F1, moved | .843 [.757, .913] | .984 [.957, 1.000] | floor set |
| align F1, split | .781 [.606, .902] | 1.000 | floor set |
| align F1, merged | .846 [.750, .925] | 1.000 | floor set |
| align F1, unrelated | .263 [.222, .304] | .914 [.878, .946] | floor set; low because page furniture nodes have no gold block and count as unrelated |
| boundary F1 | .792 [.757, .823] | .903 [.881, .924] | floor set |
| fragmentation: changes per changed sentence | 1.006 [1.001, 1.012] | 1.006 [1.001, 1.011] | floor set |
| fragmentation: changes per gold unit | 1.135 [1.097, 1.179] | 1.179 [1.138, 1.223] | floor set |
| missed changed sentences | .108 [.080, .137] | .004 [.000, .010] | floor set; the gap is the cost of reading a PDF (hyphenation, columns, tables) |

### Budgets

Time and memory budgets on a 100-page pair with about 300 changes, on 4 cores of the x64 machine and on the arm64 machine; method in [measurement.md](measurement.md). The budgets are proposals.

| Row | Budget | x64 | arm64 | Verdict |
|---|---|---|---|---|
| PDF pair: read both, compare, mark the new pages | 2 s | pending (measured at release) | pending (measured at release) | pending |
| The same pair to a rendered redline, inline | 2 s and peak memory 1 GB | pending (measured at release) | pending (measured at release) | pending |
| The same, side by side | information | pending (measured at release) | pending (measured at release) | — |
| Peak memory above the inputs, per page | information | pending (measured at release) | pending (measured at release) | — |

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

Fragmentation counts changes, not display regions, so grouping a whole paragraph into one region cannot improve it; the definition is in [measurement.md](measurement.md#scoring). These are gold-set rows with floors, not comparator rows. The free comparison tools are scored on the DOCX version of the same set in [DOCX comparators](#docx-comparators-on-docx-v1) below.

## Change semantics (synth-v1 eval)

Same set, same bootstrap. Cascades (renumbering and the references it causes) and renamed defined terms are single display groups listing their occurrences; changes are typed with resolved values. Rules decide; there is no model in this release. Full tables: [`results/synth-v1-eval-semantics.md`](results/synth-v1-eval-semantics.md).

| Row | Score [95% CI] | Verdict |
|---|---|---|
| type accuracy, all typed units | .9986 [.9971, .9997] | floor set |
| resolved-value accuracy, all typed units | .9983 [.9966, .9997] | floor set |
| amount type / value | 1.0 / .9965 (value [.9887, 1]) | floor set |
| date, duration, percentage, defined term, number | 1.0 | floor set |
| reference | .9869 [.9727, .9970] | floor set |
| cascade group precision / recall | .9778 / .9809 ([.960, .993] / [.964, .994]) | floor set |
| rename group precision / recall | 1.0 / 1.0 | floor set |
| substantive filter precision / recall | .9986 / .9989 | floor set |
| earlier rows after the semantic passes (boundary F1 .9144, changes per changed sentence 1.0047, missed .0018) | unchanged | pass |

Known limit: the remaining cascade misses are Japanese reference edits merged with a neighbouring adverb change into one unit.

## Combine and chains (combine-v1)

Set: `combine-v1`, sha256 `5c55ebb558a267e2264c7f84401b79d7d9208ed29bcb29157192abbb92def406`: 630 cases from the 210 evaluation documents with 2, 3 and 4 reviewers (1,890 reviewer copies, 5,283 combined changes, 455 conflicts); the same hash on x64 and arm64. 10,000 bootstrap resamples over documents. Full tables and the analysis of the misses: [`results/combine-v1-eval.md`](results/combine-v1-eval.md).

| Row | Score [95% CI] | Verdict |
|---|---|---|
| invertible per reviewer, check passes, fallback 0 | 630 / 630 cases | pass |
| attribution accuracy | .9937 [.9910, .9962] | floor set |
| shared-edit merge precision / recall | .9787 [.9610, .9927] / .9956 [.9886, 1.0000] | floor set |
| conflict precision / recall | .9648 [.9453, .9819] / .9843 [.9723, .9953] | floor set |
| property sweep, 1 to 4 reviewers (strict and legal settings) | 60,000 cases, 0 failures | pass |
| chain sweep, 3 to 5 steps | 20,000 cases, 0 failures | pass |
| digest of the first 20 cases on x64 and arm64 | equal | pass |

No miss is a completeness failure; the three groups of misses (alignment of short similar paragraphs, gold granularity, insertions inside a word) are described in the linked file.

## DOCX comparators on docx-v1

Set: `docx-v1`, sha256 `62b43875b9746cf41e59fafefa643e3ddf02c3301c490a6cf1eae7ce6697f91e`: the 1,050 evaluation pairs of the synthetic set rendered to Word files in six looks. The free comparison tools were run as released binaries through their public interfaces, one run each, with default settings except where noted in [`results/docx-v1-comparators.md`](results/docx-v1-comparators.md) (Docxodus 15.0.0 package, OfficeIMO.Word 3.5.3, WmlComparer through python-redlines 2.0.0, LibreOffice 26.8.2.2 headless). **Indicative**: one run each, on a shared machine. Bootstrap 95% intervals over documents (1,000 resamples).

**OneDiff's own rows on this set: pending: the DOCX package is not in this release.** The engine's tracked-changes DOCX output is not scored here.

| Tool | Pairs run | Accept-all = new | Reject-all = old |
|---|---|---|---|
| Docxodus | 1,050 / 1,050 | 1,050 | 1,050 |
| OfficeIMO | 1,050 / 1,050 | 1,050 | 958 |
| WmlComparer | 1,050 / 1,050 | 1,050 | 1,050 |
| LibreOffice | 884 / 1,050 (166 files of one look did not load) | 883 | 763 |
| OneDiff | pending: the DOCX package is not in this release | pending | pending |

| Row | Docxodus | OfficeIMO | WmlComparer | LibreOffice | OneDiff |
|---|---|---|---|---|---|
| align F1, edited | .952 [.943, .960] | .934 [.923, .944] | .928 [.916, .939] | .802 [.771, .829] | pending |
| align F1, moved | .919 [.885, .948] | 0 | 0 | 0 | pending |
| align F1, split | .992 [.972, 1.000] | 0 | 0 | 0 | pending |
| align F1, merged | .989 [.970, 1.000] | 0 | 0 | 0 | pending |
| align F1, unrelated | .851 [.820, .878] | .731 [.694, .766] | .740 [.703, .776] | .094 [.082, .106] | pending |
| boundary F1 | .689 [.668, .710] | .337 [.312, .364] | .561 [.526, .594] | .435 [.408, .462] | pending |
| fragmentation: changes per changed sentence | 1.144 [1.115, 1.177] | 1.000 | 1.060 [1.048, 1.073] | 1.050 [1.038, 1.064] | pending |
| fragmentation: changes per gold unit | 1.405 [1.362, 1.452] | 1.215 [1.184, 1.249] | 1.314 [1.278, 1.351] | 1.350 [1.308, 1.396] | pending |
| missed changed sentences | .072 [.063, .082] | .050 [.041, .059] | .043 [.035, .051] | .221 [.182, .261] | pending |

The comparators' zeros for moved, split and merged are not misreads: only Docxodus writes move markers, and none of the others can express a link between paragraphs. OfficeIMO's fragmentation of 1.000 is a paragraph per change (its boundary F1 is .337). Speed of the comparators on joined pairs of about 5, 40 and 120 pages is in the linked file (indicative); the DOCX speed rows of OneDiff are pending with the package.

Rows where OneDiff's DOCX output and the free tools are compared (strictly better, the ceiling clause, the blind-preference rows) are not published in this release.

## Pending, not in this release, and losing rows

- **Budgets** above: pending (measured at release).
- **Known limit:** one 29.7 MB pair exceeds the renderer's display-list limit; the rendered redline gives a clear error for it. It is a limit, not a failure of the gates above.
- **DOCX engine rows, DOCX speed rows against Docxodus and OfficeIMO:** pending; the package is not in this release.
- **Blind-preference rows** against the free comparators, grids, slides and scan rows, and the model rows: not run; the packages that produce them are not built yet.
- **A WASM timing row** and speed rows for the document engine against a managed comparator: not run (there is none for PDF).
- **Text-core rows that lose:** none. Patch make on the medium size is at parity (1.03×) with diff-match-patch on both machines, inside the bar; peak memory on the largest line and word inputs is above DiffPlex's (5.8 against 5.3 MB and 14.6 against 12.7 MB, in the table above), and no bar is set on it.
