# DOCX comparators on the DOCX gold set (indicative)

First rows for the free comparison tools on `docx-v1`: the 1,050 evaluation pairs of the synthetic set, rendered to Word files in six looks (see docs/measurement.md → DOCX gold set). Every number is **indicative**: one run of each tool, with default settings except where noted, on the machine named below. The engine's own rows are not here yet; the slot is `score --cmp DIR --blocks DIR` over the engine's comparisons of the same files.

## Tools, as they report themselves

| Tool | Version reported | How it was run | Settings |
|---|---|---|---|
| Docxodus | 1.0.0+34eb9a3354fa81a9f670fa0bec13b4f641adacee (NuGet package 15.0.0) | in process, `DocxCompare.Compare` | defaults |
| OfficeIMO.Word | 3.5.3+86d6ef4132250e435be585292be76157f98f3f5b | in process, `WordDocumentComparer.CreateRedlineDocument` | mode InPlaceTarget; summary, findings table and feature/review/formatting findings off; fixed author and date |
| WmlComparer (Open-XML-PowerTools engine) | python-redlines 2.0.0 and python-redlines-ooxmlpowertools 2.0.0 | `XmlPowerToolsEngine.run_redline`, one process per pair, 4 at a time | defaults, author "comparator" |
| LibreOffice Compare Document | 26.8.2.2 (9dea406ab769…), release tarball sha256 8290c8f4…a476 | headless, `.uno:CompareDocuments`: the new file is the open document, the old file the one chosen; saved as DOCX | defaults |

OfficeIMO's `Compare(source, target)` does not produce a redline document; the redline form above is the one its documentation describes for that. Its output keeps no move, split or merge vocabulary, and neither does LibreOffice's or WmlComparer's: their rows for moved, split and merged are zero because the tool cannot express the link, not because the reader missed it (moves are paired when a tool writes move range markers, as Docxodus does).

## How the rows are made

Each output is read back through OneOffice's revision model (the tool's own API is not used) into the scorer's form: a change is a run of adjacent revisions in one paragraph with no non-blank unchanged text between them; the two halves of a move are one change; formatting revisions carry no text and are not changes. Accept-all and reject-all run through OneOffice on copies, and the paragraph sequence left is compared with the new and the old input. A pair a tool did not finish scores as an empty result in "All pairs" and is left out in "Completed pairs only". Bootstrap: 10,000 resamples over the 210 base documents, seed 20261104 (1,000 for the per-look tables).

Machine: AMD Ryzen 9 7950X3D, 32 logical cores, x64; four pairs in parallel; load average around 14 beside other jobs (indicative runs only); SDK 10.0.403, runtime 10.0.14, OneOffice 0.1.0-preview.1.

## Run and round-trip

| Tool | Pairs | Ran | Failed | Median ms (ok) | Opened | Accept-all = new | Reject-all = old | Revisions (median) |
|---|---|---|---|---|---|---|---|---|
| docxodus | 1050 | 1050 (100.0%) | 0 | 111 | 1050 | 1050 (100.0%) | 1050 (100.0%) | 7 |
| officeimo | 1050 | 1050 (100.0%) | 0 | 107 | 1050 | 1050 (100.0%) | 958 (91.2%) | 5 |
| wmlcomparer | 1050 | 1050 (100.0%) | 0 | 489 | 1050 | 1050 (100.0%) | 1050 (100.0%) | 6 |
| libreoffice | 1050 | 884 (84.2%) | 166 | 144 | 884 | 883 (84.1%) | 763 (72.7%) | 10 |

Accept and reject rates are over all pairs (a pair with no output counts as a miss); paragraph sequences compared through OneOffice's accept-all and reject-all.

## All pairs (a pair without output scores as an empty result)

| Row | docxodus | officeimo | wmlcomparer | libreoffice |
|---|---|---|---|---|
| align.F1.same | 1.000 [1.000, 1.000] | 1.000 [1.000, 1.000] | 1.000 [1.000, 1.000] | 0.883 [0.869, 0.897] |
| align.F1.edited | 0.952 [0.943, 0.960] | 0.934 [0.923, 0.944] | 0.928 [0.916, 0.939] | 0.802 [0.771, 0.829] |
| align.F1.moved | 0.919 [0.885, 0.948] | 0.000 [0.000, 0.000] | 0.000 [0.000, 0.000] | 0.000 [0.000, 0.000] |
| align.F1.split | 0.992 [0.972, 1.000] | 0.000 [0.000, 0.000] | 0.000 [0.000, 0.000] | 0.000 [0.000, 0.000] |
| align.F1.merged | 0.989 [0.970, 1.000] | 0.000 [0.000, 0.000] | 0.000 [0.000, 0.000] | 0.000 [0.000, 0.000] |
| align.F1.unrelated | 0.851 [0.820, 0.878] | 0.731 [0.694, 0.766] | 0.740 [0.703, 0.776] | 0.094 [0.082, 0.106] |
| boundary.F1 | 0.689 [0.668, 0.710] | 0.337 [0.312, 0.364] | 0.561 [0.526, 0.594] | 0.435 [0.408, 0.462] |
| boundary.P | 0.671 [0.643, 0.697] | 0.341 [0.316, 0.368] | 0.546 [0.513, 0.578] | 0.353 [0.327, 0.382] |
| boundary.R | 0.709 [0.690, 0.727] | 0.333 [0.307, 0.360] | 0.577 [0.540, 0.612] | 0.565 [0.524, 0.604] |
| frag.sentence | 1.144 [1.115, 1.177] | 1.000 [1.000, 1.000] | 1.060 [1.048, 1.073] | 1.050 [1.038, 1.064] |
| frag.unit | 1.405 [1.362, 1.452] | 1.215 [1.184, 1.249] | 1.314 [1.278, 1.351] | 1.350 [1.308, 1.396] |
| missed.sentence | 0.072 [0.063, 0.082] | 0.050 [0.041, 0.059] | 0.043 [0.035, 0.051] | 0.221 [0.182, 0.261] |

## Completed pairs only

| Row | docxodus | officeimo | wmlcomparer | libreoffice |
|---|---|---|---|---|
| align.F1.same | 1.000 [1.000, 1.000] | 1.000 [1.000, 1.000] | 1.000 [1.000, 1.000] | 0.967 [0.962, 0.971] |
| align.F1.edited | 0.952 [0.943, 0.960] | 0.934 [0.923, 0.944] | 0.928 [0.916, 0.939] | 0.880 [0.861, 0.896] |
| align.F1.moved | 0.919 [0.885, 0.948] | 0.000 [0.000, 0.000] | 0.000 [0.000, 0.000] | 0.000 [0.000, 0.000] |
| align.F1.split | 0.992 [0.972, 1.000] | 0.000 [0.000, 0.000] | 0.000 [0.000, 0.000] | 0.000 [0.000, 0.000] |
| align.F1.merged | 0.989 [0.970, 1.000] | 0.000 [0.000, 0.000] | 0.000 [0.000, 0.000] | 0.000 [0.000, 0.000] |
| align.F1.unrelated | 0.851 [0.820, 0.878] | 0.731 [0.694, 0.766] | 0.740 [0.703, 0.776] | 0.235 [0.215, 0.254] |
| boundary.F1 | 0.689 [0.668, 0.710] | 0.337 [0.312, 0.364] | 0.561 [0.526, 0.594] | 0.464 [0.438, 0.492] |
| boundary.P | 0.671 [0.643, 0.697] | 0.341 [0.316, 0.368] | 0.546 [0.513, 0.578] | 0.353 [0.327, 0.382] |
| boundary.R | 0.709 [0.690, 0.727] | 0.333 [0.307, 0.360] | 0.577 [0.540, 0.612] | 0.677 [0.644, 0.708] |
| frag.sentence | 1.144 [1.115, 1.177] | 1.000 [1.000, 1.000] | 1.060 [1.048, 1.073] | 1.050 [1.038, 1.064] |
| frag.unit | 1.405 [1.362, 1.452] | 1.215 [1.184, 1.249] | 1.314 [1.278, 1.351] | 1.350 [1.308, 1.396] |
| missed.sentence | 0.072 [0.063, 0.082] | 0.050 [0.041, 0.059] | 0.043 [0.035, 0.051] | 0.068 [0.057, 0.081] |

## Per rendering look (1,000 resamples)


**align.F1.edited**

| Look | docxodus | officeimo | wmlcomparer | libreoffice |
|---|---|---|---|---|
| look 0 | 0.980 [0.967, 0.991] | 0.944 [0.924, 0.961] | 0.936 [0.912, 0.954] | 0.906 [0.876, 0.931] |
| look 1 | 0.991 [0.976, 1.000] | 0.925 [0.886, 0.957] | 0.924 [0.884, 0.956] | 0.879 [0.826, 0.922] |
| look 2 | 0.990 [0.979, 0.998] | 0.934 [0.905, 0.957] | 0.935 [0.904, 0.958] | 0.929 [0.901, 0.953] |
| look 3 | 0.890 [0.851, 0.923] | 0.920 [0.893, 0.943] | 0.911 [0.878, 0.938] | 0.796 [0.732, 0.846] |
| look 4 | 0.918 [0.887, 0.943] | 0.932 [0.906, 0.955] | 0.936 [0.909, 0.960] | 0.857 [0.808, 0.896] |
| look 5 | 0.916 [0.876, 0.947] | 0.939 [0.907, 0.962] | 0.919 [0.882, 0.948] | 0.000 [0.000, 0.000] |

**boundary.F1**

| Look | docxodus | officeimo | wmlcomparer | libreoffice |
|---|---|---|---|---|
| look 0 | 0.693 [0.636, 0.743] | 0.335 [0.278, 0.393] | 0.588 [0.519, 0.654] | 0.452 [0.396, 0.510] |
| look 1 | 0.671 [0.600, 0.737] | 0.318 [0.255, 0.388] | 0.494 [0.402, 0.582] | 0.481 [0.408, 0.556] |
| look 2 | 0.711 [0.657, 0.758] | 0.319 [0.261, 0.382] | 0.564 [0.484, 0.633] | 0.466 [0.405, 0.528] |
| look 3 | 0.665 [0.606, 0.719] | 0.362 [0.296, 0.427] | 0.597 [0.540, 0.656] | 0.465 [0.408, 0.530] |
| look 4 | 0.689 [0.627, 0.744] | 0.327 [0.269, 0.394] | 0.584 [0.511, 0.651] | 0.465 [0.400, 0.528] |
| look 5 | 0.698 [0.638, 0.754] | 0.362 [0.298, 0.425] | 0.517 [0.442, 0.598] | 0.000 [0.000, 0.000] |

**frag.sentence**

| Look | docxodus | officeimo | wmlcomparer | libreoffice |
|---|---|---|---|---|
| look 0 | 1.123 [1.079, 1.181] | 1.000 [1.000, 1.000] | 1.057 [1.032, 1.090] | 1.047 [1.024, 1.077] |
| look 1 | 1.242 [1.122, 1.371] | 1.000 [1.000, 1.000] | 1.044 [1.016, 1.085] | 1.036 [1.015, 1.064] |
| look 2 | 1.147 [1.085, 1.228] | 1.000 [1.000, 1.000] | 1.061 [1.033, 1.094] | 1.064 [1.032, 1.103] |
| look 3 | 1.121 [1.067, 1.190] | 1.000 [1.000, 1.000] | 1.052 [1.023, 1.085] | 1.051 [1.023, 1.084] |
| look 4 | 1.124 [1.076, 1.189] | 1.000 [1.000, 1.000] | 1.067 [1.038, 1.101] | 1.048 [1.026, 1.077] |
| look 5 | 1.134 [1.071, 1.202] | 1.000 [1.000, 1.000] | 1.074 [1.044, 1.109] | - |

**missed.sentence**

| Look | docxodus | officeimo | wmlcomparer | libreoffice |
|---|---|---|---|---|
| look 0 | 0.080 [0.055, 0.112] | 0.051 [0.031, 0.077] | 0.047 [0.029, 0.072] | 0.060 [0.039, 0.086] |
| look 1 | 0.066 [0.035, 0.105] | 0.045 [0.019, 0.074] | 0.038 [0.014, 0.066] | 0.060 [0.032, 0.094] |
| look 2 | 0.077 [0.050, 0.111] | 0.061 [0.037, 0.090] | 0.047 [0.027, 0.073] | 0.067 [0.041, 0.098] |
| look 3 | 0.057 [0.033, 0.087] | 0.042 [0.020, 0.070] | 0.035 [0.016, 0.057] | 0.086 [0.053, 0.126] |
| look 4 | 0.078 [0.050, 0.109] | 0.049 [0.028, 0.074] | 0.044 [0.024, 0.069] | 0.070 [0.043, 0.101] |
| look 5 | 0.067 [0.045, 0.094] | 0.046 [0.026, 0.069] | 0.043 [0.024, 0.067] | 1.000 [1.000, 1.000] |

## Reading the rows

- Docxodus is the only tool whose output links moved, split and merged paragraphs, and it leads the alignment and boundary rows. It also fragments the most (frag.sentence 1.144, frag.unit 1.405) and misses more changed sentences than OfficeIMO and WmlComparer (.072 against .050 and .043). OfficeIMO's redline is paragraph-granular (one change per changed paragraph, so fragmentation 1.0), which shows as boundary precision .341 and boundary F1 .337.
- Accept-all reproduces the new document for every output that LibreOffice finished except one; reject-all reproduces the old document for Docxodus and WmlComparer on every pair, for OfficeIMO on 958 of 1,050 and for LibreOffice on 763 of 884.
- LibreOffice did not open the 166 files of look 5 (a block-level content control around a paragraph that also holds a footnote reference); removing either the control or the reference makes the file load. Those pairs are run failures here.
- OfficeIMO's `Compare` crashed on 41 of 60 pairs of an earlier trial until the looks with bookmarks and note references were separated out; its redline form is the one used.

## Real DOCX pairs (exact gates only, no labels)

Eight pairs of related Bonterms templates (CC BY 4.0; `tools/real-pairs-pins.json`, section `docx`; they are different templates, not versions of one). Each tool ran on every pair and each output opened.

| Tool | Ran | Accept-all = new | Reject-all = old |
|---|---|---|---|
| Docxodus | 8/8 | 8/8 | 8/8 |
| OfficeIMO | 8/8 | 6/8 | 1/8 |
| WmlComparer | 8/8 | 8/8 | 8/8 |
| LibreOffice | 8/8 | 8/8 | 8/8 |

## PDF-sourced comparisons through the block maps

`OneDiff.GoldSets.Pdf compare` runs the engine's PDF source on the rendered PDFs and writes comparisons; `score --cmp DIR --blocks DIR` maps their nodes onto the gold blocks. On every fifth pair of pdf-v1 (210 synthetic pairs, 10,000 resamples), 19,575 of 21,614 content nodes matched a gold block (the rest are page furniture and extraction artefacts that have no gold block) and 0 comparisons failed Verify or fell back:

| Row | PDF source (mapped) | Same pairs, gold-document input |
|---|---|---|
| align.F1.edited | .909 [.884, .931] | .977 [.967, .986] |
| align.F1.moved | .843 [.757, .913] | .984 [.957, 1.000] |
| align.F1.split | .781 [.606, .902] | 1.000 |
| align.F1.merged | .846 [.750, .925] | 1.000 |
| align.F1.unrelated | .263 [.222, .304] | .914 [.878, .946] |
| boundary.F1 | .792 [.757, .823] | .903 [.881, .924] |
| frag.sentence | 1.006 [1.001, 1.012] | 1.006 [1.001, 1.011] |
| frag.unit | 1.135 [1.097, 1.179] | 1.179 [1.138, 1.223] |
| missed.sentence | .108 [.080, .137] | .004 [.000, .010] |

The gap between the two columns is the extraction (hyphenation, columns, tables) and the unmatched furniture nodes, which the metric most likely counts as unrelated (not separated out here); it is the cost of reading a PDF, not of the scorer.

## Speed (indicative)

Document in, tracked-changes document out, in process, single threaded, BenchmarkDotNet 0.15.8 (1 warm-up, 5 iterations, 1 launch), .NET 10.0.14 on an AMD Ryzen 9 7950X3D (x64, AVX-512). Run as a shared-queue job with the machine busy (load average 13.6 before, 13.2 after), so the intervals are wide and nothing here is a published row. Pairs are joined gold-set pairs: n1 is 59 paragraphs with 4 changes, n8 about 420 paragraphs with 79, n24 about 1,270 paragraphs with 228.

| Size | Docxodus mean | allocated | OfficeIMO mean | allocated |
|---|---|---|---|---|
| n1 | 22.2 ms (±5.6) | 19.7 MB | 71.2 ms (±14.3) | 49.8 MB |
| n8 | 142.8 ms (±220) | 94.3 MB | 258.7 ms (±35.2) | 337.8 MB |
| n24 | 459.2 ms (±222) | 281.4 MB | 566.1 ms (±158) | 746.7 MB |

Out-of-process, per pair on the 1,050 small pairs (information only; includes process start): Docxodus 111 ms median in process, OfficeIMO 107 ms, WmlComparer 489 ms, LibreOffice 144 ms.
