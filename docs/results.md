# Results

The measured results of the 1.0 packages (`RecursiveAI.OneDiff`, `.Documents`, `.Pdf`, `.Ocr` and the `onediff` tool): the text core's exact gates, fuzzing and speed against the managed comparators; the PDF and scan engine's gates and gold-set scores; change semantics and combine; budgets; platforms. Losing rows and gaps are listed, not left out. Nothing is claimed beyond this page. Method: [measurement.md](measurement.md).

Status words: **pass** meets the bar; **FAIL** a gate with failures (listed with its count); **floor** a score recorded as the floor later releases may only raise; **loses** a row where a comparator scores better; **information** measured, no bar.

No commercial comparison product is measured or compared against. The adapter packages for Word, spreadsheets, presentations, rendering, OneNLP recognizers and the model ship later; their rows are not part of this page, except the DOCX comparator scores kept at the end as information.

## What was run

| | x64 | arm64 |
|---|---|---|
| CPU | AMD Ryzen 9 7950X3D, 16 cores / 32 threads (avx, avx2, avx512f, avx512bw, bmi2, fma, popcnt, sse4.2) | Apple M2, 8 cores (arm64) |
| OS | Linux (x86_64) | macOS (arm64) |
| SDK / runtime | .NET SDK 10.0.403, runtime 10.0.14 | .NET SDK 10.0.403, runtime 10.0.14 |
| Timed rows | alone on the machine's benchmark queue; load average before the runs 1.6–6.5 (1 min), falling to 1.8–2.5 while they ran (other users' idle processes) | the Mac carries a steady background load of about 2–4 and the benchmark cannot raise its priority: these rows are less quiet than the x64 rows |

Code under test: the release candidate plus test-and-tooling changes only (no library code changed). Sister libraries: OneFont 1.4.0-preview.1, OnePDF 1.18.0-preview.3, OneRender 1.3.3, oneocr 1.0.0. Comparators and reference tools run as released binaries and packages: DiffPlex 1.9.0, DiffMatchPatch 5.0.0, git 2.51.2, GNU diffutils 3.12, GNU patch 2.8, the `diff-match-patch` Python package 20241021, W3C Nu Html Checker 26.11.4, BenchmarkDotNet 0.15.8.

Data sets (all evaluation splits, never used for tuning): text corpus manifest sha256 `087bd93a…` (9,483 code and prose pairs, 2,599 merge triples; pinned fetch in `tools/corpora-pins.json`); `synth-v1` `e11b5614…` (1,050 pairs, 7 languages); `pdf-v1` `eb10c6b3…` (1,110 pairs rendered to PDF, 60 of them real); `scan-v1` `6fe36af3…` (600 scanned pairs); `combine-v1` `5c55ebb5…` (630 cases; regenerating it from the seed gives the same hash).

## Exact gates (text core)

Oracles run as black-box processes. Zero failures in every gate.

| Gate | Result | Reference | Verdict |
|---|---|---|---|
| Myers edit length is the minimum (`diff --minimal` length) | 9,483 / 9,483 | GNU diffutils 3.12 | pass |
| Every algorithm's script transforms old into new (Myers, patience, histogram; line, word, character tokens) | 9,483 / 9,483 | — | pass |
| Unified patch round trip (make, parse, apply) | 9,483 pairs, 0 failures | — | pass |
| dmp patch round trip (make, text, parse, apply) | 9,483 pairs, 0 failures | — | pass |
| Our unified output accepted by GNU patch | 9,481 accepted, 2 skipped, 0 failures | GNU patch 2.8 | pass |
| Our unified output accepted by `git apply` | 9,481 accepted, 2 skipped, 0 failures | git 2.51.2 | pass |
| dmp patch text interop with the Python package, both directions | 9,454 agree, 29 skipped (inputs too large for that package), 0 failures | diff-match-patch 20241021 | pass |
| Three-way merge equals `git merge-file -p`, byte for byte, conflict style `merge` | 2,599 / 2,599 | git 2.51.2 | pass |
| Same, conflict style `diff3` | 2,599 / 2,599 | git 2.51.2 | pass |
| Same, conflict style `zdiff3` | 2,599 / 2,599 | git 2.51.2 | pass |

The skipped cases are listed in the gate output; none is a failure. Cases skipped by the oracle (timeouts, over-size) are counted as skips, never passes.

## Fuzzing

Seeded mutation runners (`fuzz/OneDiff.Fuzz`, no coverage feedback): inputs are mutations of patches the library writes plus hostile cases; for the change-list reader, mutations of valid comparisons; for the PDF source, mutations of valid PDFs (flips, cuts, splices, repeated chunks, extreme numbers). A crash is an exception other than the documented typed failure, a broken invariant, or an input slower than 3 s. Seed 1, release build, one process per target, all seven run side by side on the x64 machine (8 cores reserved; load average 11–16).

| Target | Duration | Executions | Crashes |
|---|---|---|---|
| unified-parse | 600 s | 368,289,410 | 0 |
| unified-apply | 600 s | 320,255,298 | 0 |
| dmp-parse | 600 s | 400,521,790 | 0 |
| dmp-apply | 600 s | 358,334,639 | 0 |
| unified-roundtrip | 600 s | 82,887,489 | 0 |
| dmp-roundtrip | 600 s | 10,275,281 | 0 |
| comparison-json (change-list reader) | 600 s | 43,825,170 | 0 (slowest input 23.4 ms) |
| pdf-source (new for this release: damaged and image-only PDFs are refused) | 60 s smoke | 23,310 (13,362 read, 9,948 refused with the typed failure) | 0 |

## Document exact gates

Every pair is compared with the Legal and the Strict settings.

| Gate | Result | Verdict |
|---|---|---|
| `synth-v1` eval: Verify passes, fallback level 0 | 1,050 / 1,050 pairs, fallback 0 | pass |
| `pdf-v1`: Verify, fallback 0, `Check()` clean | 2,220 comparisons, 0 failures | pass |
| `pdf-v1`: marks on the new document re-extracted with OnePDF equal the change list | 2,220 comparisons, 0 failures | pass |
| `pdf-v1`: marks on the old document, re-extracted likewise | 2,220 comparisons, 0 failures | pass |
| `pdf-v1`: a second run gives byte-identical comparison JSON and marked PDFs | 2,220 comparisons, 0 failures | pass |
| `scan-v1`: Verify, fallback 0 | 600 pairs × Legal/Strict, 0 failures | pass |
| `scan-v1`: flags and confidences round-trip through JSON | 32,646 changes (17,777 flagged), 0 failures | pass |
| `scan-v1`: marks on the new and old pages re-read | 54,334 marks, 0 failures | pass |
| `scan-v1`: determinism (two runs, same bytes) | 100 pairs, 0 failures | pass |
| Rendered redline PDF (OneRender 1.3.3) read back equals the change list, inline and side by side each: text corpus | 638 pairs, 0 failures | pass |
| The same, text-corpus pairs over 300 kB | 24 pairs, 0 failures | pass |
| The same, `pdf-v1` | 1,110 pairs, 0 failures (the first run of the release gate failed 85 pairs, inline layout only; fixed, see below) | pass |
| HTML redlines parsed back: every mark carries its change, text equals the change list | 96,000 generated pages (12,000 comparisons × 8 styles and layouts) and 1,324 corpus pages, 0 mismatches | pass |
| HTML pages against the W3C Nu Html Checker 26.11.4 | 370 pages, 0 errors, 0 warnings | pass |
| Seeded invertibility sweep (random edit sequences) | 60,000 cases, 0 failures | pass |
| Seeded alignment sweep (labelled and table pairs) | 60,000 cases, 0 failures | pass |
| Seeded grouping sweep (every preset) | 60,000 cases, 0 failures | pass |
| Seeded semantics sweep (random contracts: complete, groups partition the changes) | 60,000 cases, 0 failures | pass |
| Combine sweep, 1 to 4 reviewers | 60,000 cases, 0 failures | pass |
| Chain sweep, 3 to 5 steps | 20,000 cases, 0 failures | pass |

**The rendered `pdf-v1` failure, fixed.** A table cell whose text wraps over two lines in the source PDF (for example "6" and "months") kept the line break in its node text; the inline page drew the cell as a stack of short lines, and the re-extraction read the neighbouring cell's text between the cell's lines. The grid reading now joins the lines of a cell with a space (a word broken by a hyphen keeps its break so the hyphenation fold still joins it). The smallest failing pair is a test (`RenderedReadbackTests`). Re-run on the fixed code: `pdf-v1` Verify, fallback 0, `Check`, marks re-read and determinism on 1,110 pairs × Legal/Strict, the rendered read-back inline and side by side on 1,110 pairs, and the text corpus (638 pairs and 24 over 300 kB): 0 failures. The PDF digest slice (20 pairs) is identical on x64 and arm64.

### Same bytes on both machines

A fixed slice of `pdf-v1` (20 pairs, every 56th) and of `scan-v1` (20 pairs, every 30th), plus generated and corpus inputs. Each file below has the same SHA-256 on x64 and arm64.

| Digest | SHA-256 | Equal |
|---|---|---|
| `pdf-v1` slice: inputs, comparison JSON, marked PDFs (new and old) | `e8c27c0e…` | yes |
| `pdf-v1` slice: rendered redline PDFs, inline and side by side | `f877ec66…` | yes |
| Rendered text corpus (every 37th pair), PDFs | `21f32088…` | yes |
| Generated inputs, comparison JSON and marked PDFs (10 seeds) | `b2a02d49…` | yes |
| `scan-v1` slice: JSON and marked PDFs (OCR source) | `1a421327…` | yes |
| HTML pages (random documents in every style and layout, corpus pairs) | `2f32fe58…` | yes |
| `combine-v1` first 20 cases: combination JSON and report pages, two runs each | `2c58a823…` | yes |

## Gold sets

Paired bootstrap over documents, 10,000 resamples, seed 20261104, 95% percentile intervals. Every row is scored once on the evaluation split. All rows equal or beat their floors except one, marked.

### Alignment, boundaries, fragmentation (`synth-v1` eval)

Gold documents as input; 210 documents, 1,050 pairs.

| Row | Score [95% CI] | Floor | Verdict |
|---|---|---|---|
| align F1, same | 0.9999 [0.9998, 1.0000] | 0.9999 | floor held |
| align F1, edited | 0.9793 [0.9737, 0.9844] | 0.979 | floor held |
| align F1, moved | 0.9880 [0.9749, 0.9979] | 0.988 | floor held |
| align F1, split | 0.9960 [0.9862, 1.0000] | 0.996 | floor held |
| align F1, merged | 1.0000 [1.0000, 1.0000] | 1.000 | floor held |
| align F1, unrelated | 0.9278 [0.9113, 0.9434] | 0.928 | floor held |
| boundary F1 | 0.9144 [0.9036, 0.9246] | 0.914 | floor held |
| boundary precision / recall | 0.9135 / 0.9152 | .912 / .911 | floor held |
| fragmentation: changes per changed sentence | 1.0047 [1.0028, 1.0070] | 1.005 | floor held |
| fragmentation: changes per gold unit | 1.1712 [1.1423, 1.2027] | 1.171 | floor held |
| display regions per changed sentence / per unit | 1.0047 / 1.0273 | — | information |
| missed changed sentences | 0.0018 [0.0003, 0.0038] | 0.0018 | floor held |
| Verify passes / fallback rate | 1.0 / 0 | 1.0 / 0 | pass |

Fragmentation counts changes, not display regions, and is read beside boundary precision (definition in [measurement.md](measurement.md#scoring)). These are gold-set rows with floors, not comparator rows.

### Change semantics (`synth-v1` eval)

| Row | Score [95% CI] | Floor | Verdict |
|---|---|---|---|
| type accuracy, all typed units | .9986 [.9971, .9997] | .9986 | floor held |
| resolved-value accuracy, all typed units | .9983 [.9966, .9997] | .9983 | floor held |
| amount type / value | 1.0 / .9965 ([.9887, 1]) | same | floor held |
| date, duration, percentage, defined term, number (type and value) | 1.0 | 1.0 | floor held |
| reference | .9869 [.9727, .9970] | .9869 | floor held |
| cascade group precision / recall | .9778 [.9600, .9933] / .9809 [.9642, .9940] | .978 / .981 | floor held |
| rename group precision / recall | 1.0 / 1.0 | 1.0 / 1.0 | floor held |
| substantive filter precision / recall | .9986 [.9971, .9997] / .9989 [.9971, 1.0] | .9986 / .9989 | floor held |

Known limit: the remaining cascade misses are Japanese reference edits merged with a neighbouring adverb change into one unit.

### Combine and chains (`combine-v1`)

630 cases from the 210 evaluation documents with 2, 3 and 4 reviewers.

| Row | Score [95% CI] | Floor | Verdict |
|---|---|---|---|
| invertible per reviewer, check passes, fallback 0 | 630 / 630 | 630 / 630 | pass |
| attribution accuracy | .9937 [.9910, .9962] | .9937 | floor held |
| shared-edit merge precision / recall | .9787 [.9610, .9927] / .9956 [.9886, 1.0000] | .9787 / .9956 | floor held |
| conflict precision / recall | .9648 [.9453, .9819] / .9843 [.9723, .9953] | .9648 / .9843 | floor held |

### Reading a PDF (`pdf-v1`, every fifth pair)

The PDF source run on 210 synthetic pairs (222 compared, 0 failures), its nodes mapped onto the gold blocks (20,689 of 22,191 content nodes matched a gold block; the rest are page furniture and extraction artefacts, which count as unrelated).

| Row | Score [95% CI] | Floor | Verdict |
|---|---|---|---|
| align F1, edited | .9361 [.9171, .9542] | .909 | above floor |
| align F1, moved | .8429 [.7460, .9241] | .843 | equal at three decimals (the interval's centre is unchanged; moved precision .756, recall .952) |
| align F1, split | 1.0000 | .781 | above floor |
| align F1, merged | .9706 [.9206, 1.0000] | .846 | above floor |
| align F1, unrelated | .5386 [.4756, .5966] | .263 | above floor (low because page furniture has no gold block) |
| boundary F1 | .8297 [.7994, .8580] | .792 | above floor |
| fragmentation: changes per changed sentence | 1.0057 [1.0012, 1.0109] | 1.006 | floor held |
| fragmentation: changes per gold unit | 1.1735 [1.1325, 1.2185] | 1.135 | **above its floor value (a higher count is worse): the interval overlaps the recorded [1.097, 1.179]. Down from 1.188; the remainder is table rows, one change per cell, see below** |
| missed changed sentences | .0423 [.0269, .0598] | .108 | above floor |
| Verify passes / fallback | 1.0 / 0 | 1.0 / 0 | pass |

### Scanned pairs (`scan-v1`)

120 evaluation documents (30 per language: en, de, fr, es), 600 pairs. "Digital" is the same pairs read from the digital PDFs by the PDF source, for comparison; the digital column is information.

| Row | Scan [95% CI] | Floor | Digital |
|---|---|---|---|
| align F1 same | .7041 [.6949, .7131] | .704 | .9569 |
| align F1 edited | .4997 [.4700, .5281] | .500 | .9289 |
| align F1 moved | .3205 [.2582, .3814] | .321 | .8443 |
| align F1 split / merged | .0552 / .1085 | .055 / — | .9867 / .9905 |
| align F1 unrelated | .0524 [.0430, .0625] | .052 | .4658 |
| boundary F1 | .2367 [.2155, .2589] | .237 | .8508 [.8300, .8705] |
| boundary precision / recall | .1400 / .7671 | .140 / .767 | .7921 / .9190 |
| frag.sentence / frag.unit | 1.0464 / 1.1115 | 1.046 / 1.112 | 1.0067 / 1.1802 |
| missed changed sentences | .0945 [.0762, .1139] | .095 | .0100 |
| noise.changes.share | .8649 [.8503, .8782] | .865 | .4314 |
| ocr.flag.P (flagged changes no real edit explains) | .9320 [.9224, .9406] | .932 | — |
| ocr.flag.R (such changes that are flagged) | .6197 [.5889, .6492] | .620 | — |
| Verify passes / fallback | 1.0 / 0 | 1.0 / 0 | 1.0 / 0 |

The scan boundary and noise rows are low because recognition errors make most changes "noise" (86%), a limit of the recognition engine; the flags mark such changes (precision .93, recall .62 at threshold .95). The digital column is lower than at the previous measurement (boundary F1 .851 against .879, edited .929 against .944, unrelated .466 against .438; intervals overlap); it is the PDF source reading the same pairs after later changes to clause numbers and tables, and it is not a floor row.

## Speed: text core against the managed comparators

Comparators: DiffPlex 1.9.0 and DiffMatchPatch 5.0.0 (the diff-match-patch authors' C# package), restored in the benchmark project only. Ratio = ours ÷ comparator, so lower is better. Bar: geometric mean per comparator and area at most 1.0, no row above 1.25.

**Settings.** Single-threaded, workstation non-concurrent GC; DiffMatchPatch `Diff_Timeout = 0`; no cleanup in the diff rows; DiffPlex `ignoreWhitespace: false` with an explicit word separator set; DiffMatchPatch's line mode maps lines to characters inside the timed work (its helper is not public); cleanup rows time the copy of the input diff on both sides. BenchmarkDotNet 0.15.8, one launch per row with its default warm-up and iteration counts, `Release`. Inputs are corpus pairs of about 2 KB (Small), 40 KB (Medium) and 300–400 KB (Large; lines and words only), named in the raw tables. Each area ran as its own group of under 20 minutes. Raw BenchmarkDotNet output, the machine record and the input and edit-distance listing are kept with the run; peak working set is measured once per row in a cold child process.

| Area | Comparator | x64 geomean | x64 worst row | arm64 geomean | arm64 worst row | Verdict |
|---|---|---|---|---|---|---|
| lines (3 algorithms × 3 sizes) | DiffPlex lines | 0.19 | 0.32 | 0.19 | 0.34 | pass |
| lines | diff-match-patch line mode | 0.52 | 0.72 | 0.54 | 0.78 | pass |
| words (3 × 3) | DiffPlex words | 0.29 | 0.81 | 0.35 | 0.93 | pass |
| chars (3 × 2) | DiffPlex characters | 0.01 | 0.08 | 0.01 | 0.08 | pass |
| chars | diff-match-patch `diff_main` | 0.22 | 0.56 | 0.24 | 0.67 | pass |
| cleanup, semantic (2 sizes) | diff-match-patch | 0.32 | 0.48 | 0.33 | 0.48 | pass |
| cleanup, efficiency (2 sizes) | diff-match-patch | 0.51 | 0.89 | 0.53 | 0.93 | pass |
| patch apply (2 sizes) | diff-match-patch | 0.61 | 0.65 | 0.61 | 0.64 | pass |
| patch make (2 sizes) | diff-match-patch | 0.85 | **1.13** | 0.83 | **1.02** | pass (geomean under 1.0; worst row under 1.25) |
| patch toText | diff-match-patch | 0.21 | 0.32 | 0.18 | 0.26 | pass |
| patch fromText | diff-match-patch | 0.09 | 0.11 | 0.07 | 0.08 | pass |

Three-way merge has no managed comparator and is covered by the exact gate above, not a speed row.

- **Words are not equal work.** DiffPlex's word differ cuts words at the separators it is given, so it does not see our token stream: it reports fewer edits on the same input (Small 48 against our 57–61, Medium 60 against 69, Large 840 against 1,114). The ratios compare the time to produce each library's own script. Lines and characters agree on edit distance between all implementations except where a heuristic algorithm (patience, histogram) finds a longer, equally valid script than Myers.
- **Not covered.** No word rows against diff-match-patch (it has no word mode), no large-size character rows, no large-size cleanup or patch rows, no multi-threaded rows (the text core is single-threaded). Native tools (git, GNU diff) are not compared.

### Allocations (bytes per call, x64; arm64 agrees within 1%)

| Row | Ours | Comparator | Ratio |
|---|---|---|---|
| lines, Medium, Myers | 105,960 | DiffPlex 317,592 / diff-match-patch 342,648 | 0.33 / 0.31 |
| lines, Large, Myers | 960,825 | DiffPlex 2,132,880 / diff-match-patch 2,880,414 | 0.45 / 0.33 |
| words, Large, Myers | 3,031,091 | DiffPlex 10,878,894 | 0.28 |
| chars, Medium, Myers | 1,960 | DiffPlex 3,637,006 / diff-match-patch 229,568 | 0.00 / 0.01 |
| semantic cleanup, Small | 1,216 | diff-match-patch 21,512 | 0.06 |
| efficiency cleanup, Medium | 0 | diff-match-patch 328 | 0.00 |
| patch make, Medium | 253,104 | diff-match-patch 392,096 | 0.65 |
| patch apply, Medium | 325,328 | diff-match-patch 487,464 | 0.67 |

Every measured row allocates less than its comparator (allocation ratio at most 0.67).

### Memory

Peak working set above the inputs, x64, cold child process:

| Row | Ours | DiffPlex | diff-match-patch |
|---|---|---|---|
| lines, Large (Myers) | 5.7 MB | 5.3 MB | 4.0 MB |
| words, Large (Myers) | 14.7 MB | 12.7 MB | — |
| chars, Medium (Myers) | 2.3 MB | 7.5 MB | 2.8 MB |

At the two largest rows where we are higher the process baseline dominates (within about 8–16% of DiffPlex's); no bar is set on peak memory for the text core.

**Linear growth** (lines, Myers, bytes allocated per diff on inputs of 2 KB, 40 KB and 317 KB): 9,144, 230,184 and 1,514,568, that is 4.6, 5.8 and 4.8 bytes per input byte. The time grows the same way (1.7 µs, 68 µs and 792 µs).

## Budgets

A 100-page pair with about 300 changes, 4 cores (`DOTNET_PROCESSOR_COUNT=4`), one untimed warm-up and three timed repetitions per part, median reported, each part in its own process; method in [measurement.md](measurement.md). The peak is the operating system's maximum resident set size. Budgets are targets of this release.

| Row | Budget | x64 | arm64 | Verdict |
|---|---|---|---|---|
| PDF pair: read both, compare, mark the new pages | 2 s | 841 ms, peak 478 MB | 726 ms, peak 485 MB | pass |
| The same pair to a rendered redline, inline | 2 s and peak 1 GB | 1,178 ms, peak 639 MB | 1,197 ms, peak 608 MB | pass |
| The same, side by side | information | 1,717 ms, peak 718 MB | 1,738 ms, peak 686 MB | — |
| Memory above the inputs per page: markup / rendered inline / side by side | information | 4.1 / 5.7 / 6.5 MB | 4.1 / 5.3 / 6.1 MB | — |
| Scan pair: compare + mark after OCR (20-pair slice of `scan-v1`, about 6 pages per pair) | OCR time + 1 s | at most 145 ms (median 6 ms); OCR 1.7 s per pair (332 ms per page) | at most 122 ms (median 5 ms); OCR 9.2 s per pair (1.7 s per page) | pass |

The scan OCR time is the recognition engine's, not ours; on the Mac it was measured on a loaded machine.

## Platforms

| Check | x64 | arm64 | Verdict |
|---|---|---|---|
| Whole-solution tests (all test projects, Release) | 864 tests: 848 passed, 16 skipped, 0 failed | 864 tests: 846 passed, 18 skipped, 0 failed | pass |
| Build warnings with warnings as errors | 0 | 0 | pass |
| NativeAOT smoke app (every package's main path), `-warnaserror` | 0 warnings; runs and prints `ok`; 55.2 MB | 0 warnings; runs and prints `ok`; 53.8 MB | pass |
| NativeAOT `onediff` tool, `-warnaserror` | 0 warnings; `diff` exits 1 and prints the change; 48.3 MB | the same; 47.0 MB | pass |
| Pack dry run: exactly `RecursiveAI.OneDiff`, `.Documents`, `.Ocr`, `.Pdf`, `.Tool`, each with the licences, notices and readme | 5 packages, set matches | 5 packages, set matches | pass |

The test run and the pack dry run were repeated after the adapter packages moved to the renamed document library: the same counts (864 tests, 0 failed; 5 packages) on both machines. Skipped tests are those that need a native host or a file set that is not present (including the pinned failing read-back test above). Browser WebAssembly (headless Chrome 155, x64): Core 361 tests, 353 passed, 0 failed, 8 skipped; `.Documents` 200 (197 passed, 3 skipped); `.Pdf` 135 (119 passed, 16 skipped); `.Ocr` 18 (8 passed, 10 skipped); skips are tests that need files, threads, processes or AES, marked native.

## DOCX comparators on docx-v1 (information; not part of this release)

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

## Failures, rows below their floor, and gaps

- **Below its floor: PDF source, changes per gold unit** (1.174 [1.133, 1.219] against the recorded 1.135 [1.097, 1.179]; the intervals overlap). Every other `pdf-v1` row is at or above its floor. What remains is the table rows: an inserted or deleted table row is one change per cell (three for the generated tables), where before cell-per-block grids were read as paragraphs and equal short cells were matched to equal cells of other rows, so such rows cost 2.5 changes on average. A row costs three changes now because its cells are no longer matched to another row's equal cells; the changes per changed sentence and the display regions per gold unit (1.029) are unchanged by it.
- **At the floor within rounding:** PDF/PDF moved align F1 (.8429 against .843).
- **Scan limits:** scan boundary F1 (.237), split and merge recovery (F1 .055 and .109) and moved precision are limited by recognition errors and by paragraphs the recognition engine merges; the flags (precision .93, recall .62) mark rather than hide them. Latin-script scans only.
- **Known limit:** one 29.7 MB pair exceeds the renderer's display-list limit; the rendered redline gives a clear error for it. It is a limit, not a failure of the gates above.
- **Text-core rows that lose:** none. Patch make on the medium size is slower than diff-match-patch's by 1.13× on x64 and 1.02× on arm64, inside the bar (geomean .85 and .83); peak memory on the largest line and word inputs is above DiffPlex's (5.7 against 5.3 MB and 14.7 against 12.7 MB), and no bar is set on it.
- **Not run in this release:** DOCX engine rows and DOCX speed rows against the free DOCX tools, blind-preference rows, grid and slide rows, the model rows, and a WebAssembly timing row; the packages that produce them ship later. There is no managed comparator for PDF, scan, merge or document speed, so those rows are budgets only.
- **Platform coverage:** the browser suites ran on x64 only; native AOT, the whole-solution tests and the pack dry run ran on both machines. The scan-time and OCR rows were taken on a 20-pair slice.
