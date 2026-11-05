# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

In packages that are not on nuget.org yet, because they depend on previews of other libraries (`RecursiveAI.OneDiff.Word`, `RecursiveAI.OneDiff.Nlp`):

- `RecursiveAI.OneDiff.Word`: `DocxRedline` writes a comparison as a DOCX with real tracked changes, built on the new document (default) or the old one: `w:ins`/`w:del` for text, paragraphs, rows and cells, `w:moveFrom`/`w:moveTo` with range markers, property changes for paragraphs, tables, rows, cells and sections. Accepting every revision gives the new document and rejecting every one the old. The author is a setting, no date is written unless asked, ids are numbered in document order, and the output is byte-identical from run to run. Format-only edits are run property changes, each change is written as one replaced unit, splits and merges use paragraph marks, changed field results and edited text boxes are tracked in place, and the source reads document properties, the list labels of every story and text boxes through the hub (OneOffice 0.1.0-preview.3). Differences a revision cannot carry are returned in `DocxRedlineResult.NotCarried`. `WordRedline.Register()` makes `Redline.Create` and `SaveAs` use it for `.docx`. See `docs/word.md`.
- `RecursiveAI.OneDiff.Word`: `WordDocumentSource` reads DOCX, DOCM, DOTX and DOTM through the OneOffice document hub into the compare model: sections with page setup, headers and footers, paragraphs and list items with resolved numbering labels, tables with merges, footnotes and endnotes, comments, fields (code and result), pictures (content hash, and a perceptual hash for PNG), text boxes, content controls, hyperlinks, resolved run and paragraph formatting, document properties and opaque parts. Anchors name the element and the run of every node and text segment, in the offset space of OneOffice's revision authoring. Tracked changes already in an input are accepted by default, or rejected, or refused (`WordRevisionMode`). Register with `WordRedline.Register()`. See `docs/word.md`.
- `RecursiveAI.OneDiff.Nlp`: `NlpValueRecognizer`, an `IValueRecognizer` built on OneNLP's recognizers. It reads written-out English values (`forty-two`, `six percent`, `thirty (30) days`, `two hundred thousand dollars`, `1 year and 6 months`) into the same canonical values as the built-in recognizer, and leaves everything else (other languages, party names, unresolved dates) to it. `CompareSettings.Legal.UseOneNlp()` selects it for one comparison and `OneNlpRecognizers.Register()` for the process. See `docs/nlp.md`.

## [0.2.0-preview.1]

The first release on nuget.org: `RecursiveAI.OneDiff`, `RecursiveAI.OneDiff.Documents`, `RecursiveAI.OneDiff.Pdf` and `RecursiveAI.OneDiff.Tool`. A first preview, 0.1.0-preview.1, was never published; its contents are included below under that heading. The public API may change before 1.0.

### Added

- `RecursiveAI.OneDiff.Documents`: change semantics. `Change` gains `Type` (`ChangeType`: amount, date, duration, percentage, number, party, defined term, reference, numbering, formatting, typography), `OldValue` and `NewValue` (`ResolvedValue`: canonical, language-neutral forms such as `USD 25000`, `2027-03-01`, `30 d`) and `IsSubstantive`. With `CompareSettings.CollapseCascades`, `Stages.Group` lifts a renumbering and the references it caused, and a renamed defined term and its occurrences, into `GroupKind.Cascade` and `GroupKind.Rename` display groups; every underlying change stays in the list. With `TypedChanges`, `Stages.Classify` types and resolves changes. New public seam `IValueRecognizer` (`Stages.Classify(changes, recognizer)`), with `BuiltInValueRecognizer` as the default for the seven measured languages. The JSON gains optional `type`, `oldValue`, `newValue` and `substantive` members on changes (additive in schema version 1); the HTML pages list each cascade and rename group with its occurrences. See `docs/semantics.md`.
- `RecursiveAI.OneDiff.Documents`: combine, version chains and the change report (`OneDiff.Documents.Combine`). `Combination.Of(original, copies)` compares every reviewer's copy with the original and combines the results: each change is attributed to the reviewers who made it, identical changes are merged (and each edit is attributed on its own), and changes that cannot all be applied are listed as conflicts with every version shown, never a winner. The changes attributed to a reviewer are exactly that reviewer's verified comparison, checked on every run (`Combination.Check`, with a fallback that lists every change on its own). `ComparisonChain.Of(versions)` verifies every step and the net comparison and says which steps touched each net change (`StepsOf`). `ChangeReport.From(comparison | combination | chain)` gives counts by kind and type, the substantive list with resolved values, conflicts and per-step counts, and renders one self-contained HTML page. `Redline.Combine(original, copies, output)` and `Redline.Chain(versions, output)` are the one-liners; the JSON forms are `onediff.combination` and `onediff.chain` (version 1, schemas in `docs/schema`), which embed unchanged `onediff.comparison` documents. See `docs/combine.md`.
- `RecursiveAI.OneDiff.Tool`: `onediff compare` reads PDFs and writes `.pdf` output (the new PDF with the changes marked on its pages, or a rendered redline for other inputs), and takes `-o <output>`; the tool references `RecursiveAI.OneDiff.Pdf`. Rendered redlines draw Latin text only (no script font packages).
- `RecursiveAI.OneDiff.Tool`: `onediff combine <original> <copy>... -o <out>` and `onediff chain <v1> <v2>... -o <out>`.
- `RecursiveAI.OneDiff.Documents`: `CompareSettings.ValueRecognizer` and `ValueRecognizers.Register` choose the value recognizer of a comparison (settings, else the registered one, else the built-in one); the fallback stages read it from the settings too. Not written to JSON.
- `RecursiveAI.OneDiff.Pdf`: the rendered redline. `PdfRedline.Rendered` and `PdfRedline.RenderedSideBySide` (`RenderedPdfRedline`, with `Paper` and `Render`) write a tagged PDF laid out from any comparison, plain text included: the cover page, key, marks, ledger and footer of the HTML redline set on A4 or Letter with a running page number, in any `RedlineStyle`, byte-identical across runs. It depends on `RecursiveAI.OneRender.Pdf`.
- `PdfRedline.Auto`, registered for `.pdf` by `PdfRedline.Register()`: original-page markup when the new document is a PDF, a rendered redline otherwise. Both forms can still be named.
- Release packaging: the release workflow publishes the projects that set `PublishToNuGet`; a local `dotnet pack` still packs every packable project. Each package has its own readme. The OnePDF and OneFont pins are two properties in `Directory.Packages.props`.
- Sample PDFs for every preset and layout, and one original-page markup sample, under `docs/samples`, with a README of the one-line recipes.

### Changed

- `PdfRedline.Register()` registers `PdfRedline.Auto` for `.pdf` instead of `PdfRedline.OnPages` alone; a PDF as the new input is marked on its pages as before.

### Not in this release

- `RecursiveAI.OneDiff.Word` (DOCX source and tracked-changes writer) and `RecursiveAI.OneDiff.Nlp` (OneNLP value recognizer) are in the repository but not published, because they depend on previews of OneOffice and OneNLP that are not on nuget.org. Change semantics, combine, chains, the change report and the recognizer seam live in `RecursiveAI.OneDiff.Documents` and are in this release.
- Scans, spreadsheets, slides, the model and the OneOffice rendering package are not built yet.

- `RecursiveAI.OneDiff.Ocr`: `OcrDocumentSource` reads scans into the flowing compare model through the local OCR engine of `RecursiveAI.OneOcr`: images (PNG, JPEG, TIFF, recognized by content) and PDFs whose pages carry no text of their own (`OcrDocumentSource.IsScannedPdf`). Paragraphs, list items (clause numbers included), and running headers and footers (found by their repetition across pages) become nodes whose words are anchored by `AnchorKind.OcrWords`: the page quadrilateral in upright page space and the engine's confidence. `OcrSourceOptions` carries the engine, resolution, limits and layout switches. `OcrRedline.Register()` registers the PDF and scan sources and the on-page markup form; a scan is marked on its own pages (an image on a PDF page made from it) by `PdfRedline`. See docs/ocr.md.
- `RecursiveAI.OneDiff.Documents`: possible OCR errors. `Change.OcrConfidence` is the lowest confidence of the recognized words a change's edits touch, and `Change.PossibleOcrError` is true when it is below `CompareSettings.OcrConfidenceThreshold` (default 0.95, chosen on a development scan set). The comparison JSON carries `ocrConfidence` and `possibleOcrError` on changes and `ocrConfidenceThreshold` in the settings when it differs from the default; all three are optional additions to schema version 1.
- Gold sets: scan-v1, the Latin-script pairs of the digital gold set scanned under seeded degradations (`tools/OneDiff.GoldSets.Scan`), with exact gates, noise and flag rows and an indicative cost row. See docs/results/scan-v1-eval.md.
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
- A release workflow that publishes through nuget.org Trusted Publishing; see `docs/building.md`.

### Changed

- Building without `ONEDIFF_ARTIFACTS` set no longer fails on the default output folder.
