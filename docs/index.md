# OneDiff documentation

**Text diff and document comparison for .NET.** Start with the [README](../README.md) for what OneDiff is and how to install it; this page says where everything else is.

## Learn it by doing

- [samples/OneDiff.Samples](../samples/OneDiff.Samples/Samples.cs): one task per method, each one or two lines. Diff two strings, make and apply a unified patch, merge three ways, compare PDFs to a redline PDF or to marks on the pages, save and read back the JSON change list, HTML side by side, compare scans, walk the substantive changes, combine reviewers, compare a version chain, pin an alignment and re-run. They run in the test suite.
- [samples/README.md](samples/README.md): the redline looks (blackline, side by side, Word, developer), as HTML and PDF files you can open.

## Coming from another library

- [DiffPlex → `Diff`](migrating-from-diffplex.md)
- [`WmlComparer` and Docxodus → `Redline` and `Comparison`](migrating-from-wmlcomparer.md), including what works before the Word package is released.

## The text core (`RecursiveAI.OneDiff`)

- [diff-algorithms.md](diff-algorithms.md): Myers, patience and histogram, their tie-breaking and cost cap.
- [patches.md](patches.md): unified diffs and diff-match-patch patch text, with fuzzy apply and conflict reporting.
- [merge.md](merge.md): three-way merge, the conflict styles, and the alignment that agrees with git.
- [text-output.md](text-output.md): terminal and Markdown renderers.

## Document comparison (`RecursiveAI.OneDiff.Documents`)

Read in this order:

1. [compare-model.md](compare-model.md): the compare model, anchors, the five stages, and the completeness guarantee with its checks.
2. [alignment.md](alignment.md): how blocks are paired, including moves, splits and merges.
3. [grouping.md](grouping.md): the word-level diff inside a pair, and how edits become changes.
4. [semantics.md](semantics.md): typed values, cascades, defined-term renames and the substantive filter.
5. [combine.md](combine.md): combining reviewers' copies, version chains and the change report.
6. [output-html.md](output-html.md): the HTML writers, the `Redline` entry points, and how adapter packages register inputs and output forms.
7. [schema/comparison-v1.json](schema/comparison-v1.json): the JSON change list (also [combination](schema/combination-v1.json) and [chain](schema/chain-v1.json)).

## Adapter packages

- [pdf.md](pdf.md): reading PDFs, marks on the original pages, and the rendered redline.
- [ocr.md](ocr.md): scans, word confidence and the possible-recognition-error flag.
- [word.md](word.md) and [nlp.md](nlp.md): the Word and OneNLP packages (released after the first release).

## Evidence and method

- [results.md](results.md): every measured result, including rows that are pending or lose. Nothing is claimed beyond it.
- [measurement.md](measurement.md): how the gates, gold sets, comparator runs and speed rows are produced and reproduced.
- [building.md](building.md): building, tests, continuous integration and releases.
