# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `RecursiveAI.OneDiff.Documents`: change semantics. `Change` gains `Type` (`ChangeType`: amount, date, duration, percentage, number, party, defined term, reference, numbering, formatting, typography), `OldValue` and `NewValue` (`ResolvedValue`: canonical, language-neutral forms such as `USD 25000`, `2027-03-01`, `30 d`) and `IsSubstantive`. With `CompareSettings.CollapseCascades`, `Stages.Group` lifts a renumbering and the references it caused, and a renamed defined term and its occurrences, into `GroupKind.Cascade` and `GroupKind.Rename` display groups; every underlying change stays in the list. With `TypedChanges`, `Stages.Classify` types and resolves changes. New public seam `IValueRecognizer` (`Stages.Classify(changes, recognizer)`), with `BuiltInValueRecognizer` as the default for the seven measured languages. The JSON gains optional `type`, `oldValue`, `newValue` and `substantive` members on changes (additive in schema version 1); the HTML pages list each cascade and rename group with its occurrences. See `docs/semantics.md`.
- `RecursiveAI.OneDiff.Word`: `WordDocumentSource` reads DOCX, DOCM, DOTX and DOTM through the OneOffice document hub into the compare model: sections with page setup, headers and footers, paragraphs and list items with resolved numbering labels, tables with merges, footnotes and endnotes, comments, fields (code and result), pictures (content hash, and a perceptual hash for PNG), text boxes, content controls, hyperlinks, resolved run and paragraph formatting, document properties and opaque parts. Anchors name the element and the run of every node and text segment, in the offset space of OneOffice's revision authoring. Tracked changes already in an input are accepted by default, or rejected, or refused (`WordRevisionMode`). Register with `WordRedline.Register()`. See `docs/word.md`.
- `RecursiveAI.OneDiff.Documents`: combine, version chains and the change report (`OneDiff.Documents.Combine`). `Combination.Of(original, copies)` compares every reviewer's copy with the original and combines the results: each change is attributed to the reviewers who made it, identical changes are merged (and each edit is attributed on its own), and changes that cannot all be applied are listed as conflicts with every version shown, never a winner. The changes attributed to a reviewer are exactly that reviewer's verified comparison, checked on every run (`Combination.Check`, with a fallback that lists every change on its own). `ComparisonChain.Of(versions)` verifies every step and the net comparison and says which steps touched each net change (`StepsOf`). `ChangeReport.From(comparison | combination | chain)` gives counts by kind and type, the substantive list with resolved values, conflicts and per-step counts, and renders one self-contained HTML page. `Redline.Combine(original, copies, output)` and `Redline.Chain(versions, output)` are the one-liners; the JSON forms are `onediff.combination` and `onediff.chain` (version 1, schemas in `docs/schema`), which embed unchanged `onediff.comparison` documents. See `docs/combine.md`.
- `RecursiveAI.OneDiff.Tool`: `onediff combine <original> <copy>... -o <out>` and `onediff chain <v1> <v2>... -o <out>`.

- `RecursiveAI.OneDiff.Ocr`: `OcrDocumentSource` reads scans into the flowing compare model through the local OCR engine of `RecursiveAI.OneOcr`: images (PNG, JPEG, TIFF, recognized by content) and PDFs whose pages carry no text of their own (`OcrDocumentSource.IsScannedPdf`). Paragraphs, list items, tables, and running headers and footers (found by their repetition across pages) become nodes whose words are anchored by `AnchorKind.OcrWords`: the page quadrilateral in upright page space and the engine's confidence. `OcrSourceOptions` carries the engine, resolution, limits and layout switches. `OcrRedline.Register()` registers the PDF and scan sources and the on-page markup form; a scan is marked on its own pages (an image on a PDF page made from it) by `PdfRedline`. See docs/ocr.md.
- `RecursiveAI.OneDiff.Documents`: possible OCR errors. `Change.OcrConfidence` is the lowest confidence of the recognized words a change's edits touch, and `Change.PossibleOcrError` is true when it is below `CompareSettings.OcrConfidenceThreshold` (default 0.8). The comparison JSON carries `ocrConfidence` and `possibleOcrError` on changes and `ocrConfidenceThreshold` in the settings when it differs from the default; all three are optional additions to schema version 1.
- `RecursiveAI.OneDiff.Pdf`: the markup writer places marks on `AnchorKind.OcrWords` anchors as it does on glyph quadrilaterals.

## [0.1.0-preview.1]

The first preview. Public API may change before 1.0.

### Added

#### `RecursiveAI.OneDiff` (text core, no dependency beyond the .NET base class library)

- Edit scripts as struct ranges (`Edit`, `EditScript`, `EditScriptBuilder`), `TokenInterner<T>`, and the `Diff` entry points for lines, words, characters and graphemes, with comparer-based variants (ignore case, ignore whitespace).
- Myers (linear space, with a work cap and a `DiffDiagnostic` when it is reached), patience and histogram diff over any sequence with an equality comparer.
- Line, word, character and grapheme tokenizers; word tokens are script-aware, so Han, Hiragana and similar scripts split per character.
- Semantic and efficiency cleanup, with lossless shifting of changes to word, sentence and line boundaries.
- Unified diff: make, parse and apply with offset and fuzz, reporting hunks that do not apply.
- diff-match-patch patch text: make, parse and apply with fuzzy matching, with UTF-16 or code-point offsets.
- Three-way (diff3) merge with `merge`, `diff3` and `zdiff3` conflict styles, resolution options and a structured conflict result.
- Terminal and Markdown renderers for line and word diffs.

#### `RecursiveAI.OneDiff.Documents` (document comparison engine)

- The compare model: block trees with source anchors, formatting runs and normalization folds; `IDocumentSource` with a plain-text source.
- The five stages (`Align`, `DiffPairs`, `Group`, `Classify`, `Verify`) over immutable values, an editable `Alignment`, and deterministic block alignment with moves, splits, merges and table-aware matching.
- `Comparison` with a change ledger, diagnostics and `Verify`: every comparison is checked for completeness (old plus changes gives new, new minus changes gives old) and falls back stepwise to a coarser complete grouping, or throws in strict mode.
- `CompareSettings` and `RedlineStyle` presets, and a versioned JSON form (schema v1) with a published schema.
- Self-contained HTML redlines, inline and side by side, with cover page, change bars and footer; the `Redline` statics (`Create`, `Compare`), `Comparison.SaveAs`, `Save` and `ToBytes`, and a registration seam (`DocumentSources.Register`, `Redline.Register`, `RedlineForm`) for adapter packages.

#### `RecursiveAI.OneDiff.Pdf` (PDF source and original-page markup)

- `RecursiveAI.OneDiff.Pdf`: `PdfDocumentSource` reads digital PDFs into the compare model (paragraphs joined across lines, columns and pages, list items, tables, running headers and footers, formatting, glyph-quad anchors; line breaks, hyphenation and page numbers go to the ledger) and `PdfRedline` marks a comparison on the original pages with real highlight, caret, underline, squiggly and strike-out annotations carrying the old text in their pop-ups. See `docs/pdf.md`.

#### Repository

- CI on linux-x64, linux-arm64, osx-arm64 and win-x64, a NativeAOT smoke app, and the core tests run as a browser WebAssembly app in headless Chrome.
- A release workflow that packs every packable project and publishes through nuget.org Trusted Publishing; see `docs/building.md`.

### Changed

- Building without `ONEDIFF_ARTIFACTS` set no longer fails on the default output folder.
