# Combine set (combine-v1): first floors

The reference combination (`Combination.Of`, comparison settings: nothing hidden, cascades collapsed, typed changes on) over the combine set. Method and gold derivation: `measurement.md` → Combine and chain. Rows from `OneDiff.GoldSets score-combine`, 10,000 bootstrap resamples over the 210 original documents (seed 20261104).

**Set** `combine-v1`, sha256 `5c55ebb558a267e2264c7f84401b79d7d9208ed29bcb29157192abbb92def406` (generated with seed 20261104; the same hash on x64 and arm64): 630 cases = the 210 eval documents of the synthetic set (30 per language, seven languages) × 2, 3 and 4 reviewers; 1,890 reviewer copies, 5,283 combined changes, 509 of them shared, 455 conflicts (cascade-only conflicts excluded). Every original is an eval document (checked against `split.json` of the synthetic set). It trains nothing.

| Row (all 7 languages) | Value | 95% CI | n / d |
|---|---|---|---|
| invertible per reviewer, combination check, fallback 0 | 1.0000 | [1.0000, 1.0000] | 630 / 630 cases |
| attribution accuracy | 0.9937 | [0.9910, 0.9962] | 4,572 / 4,601 gold edits |
| shared-edit merge precision | 0.9787 | [0.9610, 0.9927] | 505 / 516 |
| shared-edit merge recall | 0.9956 | [0.9886, 1.0000] | 449 / 451 |
| conflict precision | 0.9648 | [0.9453, 0.9819] | 439 / 455 |
| conflict recall | 0.9843 | [0.9723, 0.9953] | 439 / 446 |

Per language (attribution accuracy, conflict precision, conflict recall): ar .992 / .967 / .967, de .994 / .985 / .985, en .996 / .970 / 1.000, es .986 / .983 / .983, fr .995 / .958 / 1.000, ja .992 / .924 / .968, zh 1.000 / .968 / .984. Every per-language row with its interval is in the tool's JSON output.

These are the first floors: they only rise.

## What the misses are

41 of the 630 cases have some miss. The ones examined (explain mode, `--explain <case>`) fall in three groups:

- **Alignment.** In `ar-004-c3` the aligner pairs a short edited paragraph (a jurisdiction swapped in a six-word sentence) as a move plus an insertion, so the reviewer's edit is not found at its place in the original and cannot be attributed to it. It is the alignment of short, similar paragraphs, not the combination; reported for the aligner.
- **Gold granularity.** Gold edits are the generator's units; the scorer narrows each to the characters that really change (a character diff widened to whole words), but a rewrite that moves words around still scatters, so an engine edit at the edge can touch it or not.
- **Insertions in a word** (`deb→deberá`): a point at the edge of a gold span counts as touching it.

No miss is a completeness failure: all 630 cases verify at fallback 0 and the per-reviewer projections are exact.

## Sweeps and other gates

| Gate | Cases | Failures |
|---|---|---|
| combine property sweep (random trees, 1 to 4 reviewers, shared mutations replayed from one stream, strict and legal settings) | 60,000 (seeds 0 to 59,999) | 0 |
| chain sweep (3 to 5 steps) | 20,000 (seeds 0 to 19,999) | 0 |
| combine set: Check(), no error diagnostics, fallback 0 | 630 | 0 |
| determinism: JSON and report page, two runs in one process | 20 cases | identical |
| digest of the first 20 cases (JSON + report hashes) | x64 (Ryzen 9 7950X3D) `886eadd77dfd3273d6dd783b3d3febbed3fb3e64241ac9ae754d57a8d5813390` | equal on arm64 (Apple M2) |

Indicative timing (`time-combine`, 100 pages of 40 paragraphs, 4 reviewers of 75 edited paragraphs each, 307 combined changes, DOTNET_PROCESSOR_COUNT=4, Release, three warm repetitions after one warm-up): comparing the four copies, combining and checking took 1.15 to 1.57 s with the shared machine at a load average of 24 to 25 (before and after the run); writing the JSON (18.4 MB, it embeds the four comparisons) and the report page 0.14 s; `Check()` alone 0.37 s. Indicative only; the machine was busy.
