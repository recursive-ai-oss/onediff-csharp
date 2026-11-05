# Scans and images

How `RecursiveAI.OneDiff.Ocr` reads a scanned PDF or an image into the compare model, what the possible-OCR-error flag means, and how the scan gold set is built and scored.

## 1. What is read as a scan

`OcrDocumentSource` reads PNG, JPEG and TIFF images (recognized by their signatures, never by file name) and PDFs whose pages carry no text of their own.

- A PDF is a scan when none of the pages examined has at least `MinTextLayerCharacters` (default 20) non-space characters in its text layer. All pages are examined up to 8; beyond that, 8 pages spread evenly.
- A PDF with an invisible recognized-text layer therefore counts as a digital PDF and is read by the PDF source.
- An unreadable or encrypted PDF is not a scan; the PDF source reports why.
- Each image frame is one page. An image is wrapped in a one-page-per-frame PDF so that markings can be written on it. The marked file is checked against the wrapper's hash (`PdfOrigin.Sha256`), the compare model's `SourceHash` is the image's own hash.

`OcrRedline.Register()` registers the PDF source and the scan source, the scan source with a higher priority (`DocumentSources.Register(source, priority)`), so a scanned PDF goes to OCR whichever of `PdfRedline.Register()` and `OcrRedline.Register()` was called last. Without it, the PDF source refuses a PDF that has no text layer and says to reference this package. The `onediff` tool registers it.

## 2. Reading the text

The recognizer returns pages of blocks, lines and words, each with a quadrilateral and a confidence. The model is built the way the PDF source builds it from a text layer, with these differences, which exist because two scans of one page are never segmented the same way:

- **Block boundaries are soft.** A block that starts right under the open paragraph (a gap of at most 0.9 line heights, in the same column and type size) continues the paragraph, unless the paragraph's last line was a short line that ended a sentence. Where the recognizer cuts running text into blocks is its own choice and differs from scan to scan, so it is not a paragraph break. Inside a block a gap wider than 0.9 line heights (and, with four or more gaps, 1.6 times the median gap) ends a paragraph.
- **Clause numbers start list items.** A line that starts with a multi-level number (`2.3`, `4.1.2`) followed by text is a list item with the number as its `list.label` attribute, whatever the spacing, so renumbering reads as a cascade and not as an edit. A clause number that follows a sentence end inside a line also starts one.
- **Tables are off by default** (`ReadTables`). The recognizer finds the same table on two scans of a page only some of the time; a table on one side and running text on the other would be a difference of reading. The cell text still reads in order and every difference in it is shown. Set `ReadTables` to get table, row and cell nodes.
- **Running furniture.** A line in the top or bottom tenth of a page whose letters resemble (Dice similarity ≥ 0.65 over letter pairs) a line in the same margin of another page is a header or footer. So is a line of digits and punctuation alone, and the words for "page" or "page of" in English, German, French, Spanish, Italian, Dutch, Swedish, Polish and Turkish. One node stands for each running text; the copy read with the highest confidence is the one kept. Page numbers fold to `#`, joined with the characters a scan puts between them (`Page 2/.3`, `Page 2 / 3`); the fold is a ledger entry (`PageNumbers`).
- Line breaks and end-of-line hyphenation fold into the ledger as for a PDF (`LineBreaks`, `Hyphenation`).

Nothing the recognizer read is corrected or hidden: a recognition error is a change like any other, with the flag below.

### Anchors

Every token is anchored by `AnchorKind.OcrWords`: the quadrilaterals of the words (upright page space, points, origin at the lower left of the page as displayed, y up) and the word's confidence. A paragraph's anchor is one box per line. Offsets are UTF-16 offsets into the node's text.

## 3. The possible-OCR-error flag

`Change.OcrConfidence` is the lowest confidence over the words the change's edits touch, resolved per edit range on each side (words next to the edit that merely sit in the same unit do not count). A whole inserted or deleted block has no edits; every word of its side counts. `Change.PossibleOcrError` is true when it is below `CompareSettings.OcrConfidenceThreshold`.

- The flag never hides, drops or reorders a change.
- Moves, formatting and attribute changes have no confidence.
- JSON: optional `ocrConfidence` and `possibleOcrError` per change, `settings.ocrConfidenceThreshold` when not the default. Schema version stays 1.
- **Default threshold 0.95**, chosen on the dev scan set only (see §5): a lower threshold has higher precision and lower recall.

| threshold | precision | recall |
|---|---|---|
| 0.80 | .946 | .408 |
| 0.90 | .939 | .484 |
| 0.95 | .898 | .588 |
| 0.97 | .877 | .676 |
| 0.98 | .869 | .717 |
| 0.99 | .851 | .835 |
| 0.995 | .844 | .872 |

Precision is the share of flagged changes that no real edit explains; recall is the share of such changes that are flagged.

## 4. Gold set method (scan-v1)

`tools/OneDiff.GoldSets.Scan build` takes the PDFs of the digital gold set and scans each file of a pair independently: it renders at 200 or 300 dpi, blurs (sigma 0, 0, 0.7 or 1.2 at 300 dpi), skews by up to ±2°, adds salt-and-pepper noise (density 0, 0, 0.002, 0.006) and encodes each page as PNG or JPEG (quality 90, 75 or 55). All choices come from a seeded generator keyed by the file's name and the seed (20261104), so the set is reproducible. Languages: the Latin-script ones (en, de, fr, es), because the bundled recognizer reads Latin, Greek and Cyrillic.

The labels are the digital set's own. A scan's nodes are matched to the gold blocks by similarity; positions are carried over by a minimal character alignment of the whole page's text against the gold text, so a gold block that the recognizer cut into several nodes, or several that it joined, still receive their changes. The scorer's noise rows count a **spurious change**: a text change that overlaps no gold unit, or a move of a block no gold link moves.

## 5. Rows and cost

The rows are those of the digital gold sets with the same definitions, plus `noise.changes.share` (spurious changes over all changes), `noise.changes.per10kchars` and `ocr.flag.P/R/share`. The results are in `results/scan-v1-eval.md`.

A scan costs the recognizer's time plus the comparison. The recognizer reads about 0.5 s per page on one core of the reference machine; `results/scan-v1-eval.md` has the budget measurement.
