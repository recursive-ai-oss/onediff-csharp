# OneDiff on the DOCX gold set

Rows for OneDiff on `docx-v1` (the 1,050 evaluation pairs, rendered to Word files in six looks), beside the free comparison tools in docs/results/docx-v1-comparators.md. Two rows per metric:

- **(a) Engine on the Word source:** the `Comparison` made from the Word files, scored against the gold blocks.
- **(b) Tracked output:** the DOCX redline we write, read back by the same tracked-changes reader the comparators go through.

Bootstrap: 10,000 resamples over documents, seed 20261104; verdicts come from a paired bootstrap on the same resamples.

## How fragmentation rows are read

A paragraph-granular tool trivially scores one change per sentence, so `frag.sentence` and `frag.unit` are read only beside boundary F1. A comparator wins a fragmentation row only if its boundary F1 is within the confidence interval of ours or better. Otherwise the row reads "not worse (fragmentation read with boundary F1)".

## Fragmentation against OfficeIMO

| Row | Ours | OfficeIMO | Boundary F1 ours | Boundary F1 OfficeIMO | Verdict |
|---|---|---|---|---|---|
| frag.sentence (tracked output) | 1.007 | 1.000 | .893 | .337 | not worse (fragmentation read with boundary F1) |

OfficeIMO marks whole paragraphs, so every changed sentence is touched by exactly one change; that is why its count is 1.000 while it finds far fewer of the actual change boundaries (.337 against .893).

<!-- The remaining tables (both row sets with paired verdicts per comparator, and the reader changes) are added after the final re-score. -->
