# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- Documentation: the 1.0.1 known issue "scans cannot be read in browser WebAssembly" was a bug in this repository's browser test harness, not in the packages. Scans work in the browser with the published packages, and the browser test suite now runs the scan tests.

## [1.0.1] - 2026-11-05

### Fixed

- `RecursiveAI.OneDiff.Documents`: an unsupported Office input or output (DOCX, XLSX, PPTX) now says that support is coming in a 1.x release and suggests exporting the documents to PDF and comparing the PDFs, instead of naming packages that are not on nuget.org yet; the message starts with a capital letter.
- `RecursiveAI.OneDiff.Documents`: attribute changes in HTML and the rendered PDF redline show a readable label and unquoted values (`List number: 1.5 → 1.2`) instead of the internal key; `data-key`, `data-old` and `data-new` and the JSON are unchanged.
- `RecursiveAI.OneDiff.Documents`: the cover-page line "Hidden into the list below" reads "Not marked (listed below)".
- Documentation: the released sister-library versions, the worst speed row (1.13×), and a known issue: scans cannot be read in browser WebAssembly yet.

## [1.0.0] - 2026-11-05

The first stable release of `RecursiveAI.OneDiff`, `RecursiveAI.OneDiff.Documents`, `RecursiveAI.OneDiff.Pdf`, `RecursiveAI.OneDiff.Ocr` and the `onediff` tool (`RecursiveAI.OneDiff.Tool`). Results: [docs/results.md](docs/results.md).

### Added

- `RecursiveAI.OneDiff`: `TextDiff.Pieces` and `TextDiff.Lines` read a diff as typed runs and lines with line numbers; `Diff.SideBySide` lays a line diff out in aligned rows with the changed words marked; `DiffSettings` (algorithm and work cap) is taken by the `Diff` one-liners and `TextDiff.Diagnostic` reports the cap; `Diff.ApplyUnified`, `ApplyPatchText` and `PatchText` take the apply options, patcher and cancellation token; `TextMerge.Conflicts` and `ItemMerge<T>.Items` expose a merge's conflicts and merged items.
- `RecursiveAI.OneDiff`: `HunkResult.Line` and `DmpPatchResult.Location` are `int?` (null when not placed), and `DmpPatcher.Match` became `TryMatch`.
- `RecursiveAI.OneDiff`: the `*Using` comparer one-liners (`TextUsing`, `LinesUsing`, `WordsUsing`) and `Diff.Semantic` and `Diff.Efficient` take `DiffSettings`, so the work cap reaches every diff one-liner. `TextDiff.Diagnostic.Work` includes the items of the shared head and tail, so a small real diff no longer reports zero.
- `RecursiveAI.OneDiff.Documents`: `Redline.CreateAsync`, `Comparison.OfAsync`, `SaveAsync`, `ComparisonJson.ReadAsync` and `WriteAsync` work on streams that refuse synchronous I/O (for example ASP.NET Core request and response bodies), and every `SaveAs` overload takes a `CancellationToken`.
- `RecursiveAI.OneDiff.Documents`: `VerificationResult.VerifiedHere` says whether the completeness checks ran in this process on this value; `ComparisonFormatException` carries the position of a JSON syntax error (`LineNumber`, `BytePositionInLine`, `Path`).
- `RecursiveAI.OneDiff.Documents`: `CompareDocument.Origin` holds the bytes a document was read from (and the password), so a PDF is marked from the document itself; `PdfRedline.Auto` chooses from the new document's source format and its origin.
- `RecursiveAI.OneDiff.Documents`: `Redline.Register(source, priority)` and `DocumentSources.Register(source, priority)`: a source with a higher priority is asked first, so the order of registration no longer decides which source reads a file. `OcrRedline.Register()` registers the scan source above the PDF source.
- `RecursiveAI.OneDiff.Documents`: `RedlineStyle.MarkWholeWords` widens marks inside a word to the whole word, as the page markup and the change list already do.
- `RecursiveAI.OneDiff.Documents`: plain text reads leading clause markers (such as `1.1` or `(a)`) as list labels, with the rule the PDF source uses, so renumbering in plain text groups as a cascade the way it does for PDF.
- `RecursiveAI.OneDiff.Tool`: `onediff --version`, `--exit-code`, `--whole-words` and `--form`; the tool registers the scan source and reads scans.

In packages that are not on nuget.org yet, because they depend on previews of other libraries (`RecursiveAI.OneDiff.Word`, `RecursiveAI.OneDiff.Nlp`):

- Word packages: the document library OneDocument (formerly OneOffice) is now referenced as `RecursiveAI.OneDocument*` 0.2.0-preview.2.
- `RecursiveAI.OneDiff.Word`: `DocxRedline` writes a comparison as a DOCX with real tracked changes, built on the new document (default) or the old one: `w:ins`/`w:del` for text, paragraphs, rows and cells, `w:moveFrom`/`w:moveTo` with range markers, property changes for paragraphs, tables, rows, cells and sections. Accepting every revision gives the new document and rejecting every one the old. The author is a setting, no date is written unless asked, ids are numbered in document order, and the output is byte-identical from run to run. Format-only edits are run property changes, each change is written as one replaced unit, splits and merges use paragraph marks, changed field results and edited text boxes are tracked in place, and the source reads document properties, the list labels of every story and text boxes through the hub (OneDocument 0.2.0-preview.2). Differences a revision cannot carry are returned in `DocxRedlineResult.NotCarried`. `WordRedline.Register()` makes `Redline.Create` and `SaveAs` use it for `.docx`. See `docs/word.md`.
- `RecursiveAI.OneDiff.Word`: `WordDocumentSource` reads DOCX, DOCM, DOTX and DOTM through the OneDocument document hub into the compare model: sections with page setup, headers and footers, paragraphs and list items with resolved numbering labels, tables with merges, footnotes and endnotes, comments, fields (code and result), pictures (content hash, and a perceptual hash for PNG), text boxes, content controls, hyperlinks, resolved run and paragraph formatting, document properties and opaque parts. Anchors name the element and the run of every node and text segment, in the offset space of OneDocument's revision authoring. Tracked changes already in an input are accepted by default, or rejected, or refused (`WordRevisionMode`). Register with `WordRedline.Register()`. See `docs/word.md`.
- `RecursiveAI.OneDiff.Word`: `DocxRedline` writes a tracked-changes DOCX for a PDF, a scan, plain text or any other flowing source too. A side that is not a Word file is reconstructed as a DOCX from its compare model (paragraphs, list items with their labels, tables, one header and one footer, character formatting), on the other side's package when that one is a Word file, and labelled as reconstructed in its core properties and by a note in its header (`DocxRedline.ReconstructionNote`); the revisions are then written as for two Word files. The comparison is the one that was verified. See `docs/word.md` and `docs/results/pdf-docx.md`.
- `RecursiveAI.OneDiff.Word`: a paragraph whose list is numbered by different definitions in the two packages now gets a property change (the same number can mean different labels in two packages), and blocks that followed a block content control that only the other document has are placed after the copies of what stood inside it, not before.
- `RecursiveAI.OneDiff.Nlp`: `NlpValueRecognizer`, an `IValueRecognizer` built on OneNLP's recognizers. It reads written-out English values (`forty-two`, `six percent`, `thirty (30) days`, `two hundred thousand dollars`, `1 year and 6 months`) into the same canonical values as the built-in recognizer, and leaves everything else (other languages, party names, unresolved dates) to it. `CompareSettings.Legal.UseOneNlp()` selects it for one comparison and `OneNlpRecognizers.Register()` for the process. See `docs/nlp.md`.

### Changed

- **Breaking:** `ComparisonJson.Read` and `ReadAsync` re-run the completeness checks by default (`verify: true`), so `Verification.Passed` is true only when this process proved it, and a tampered file shows failures. Pass `verify: false` for the cheaper read: the value then reports `VerifiedHere == false` and `Passed == false`, and `Comparison.Check()` returns the failures without changing it (a `Comparison` is immutable; read again with `verify: true` for a proven value).
- **Breaking:** a PDF with pages but no text is refused (`InvalidDataException`, `PdfSourceErrors.FailureOf` = `NoTextLayer`, with a message naming `RecursiveAI.OneDiff.Ocr`) instead of being compared as an empty document; a PDF whose pages have no content at all is refused as `Malformed` and described as damaged, with or without the scan source registered. Content OnePDF could not recover becomes the comparison diagnostic `source.<side>.pdf.unrecovered`, which strict settings throw on. A scan in which the OCR engine finds no word carries `source.<side>.ocr.nothingRecognized`, with the same strict behaviour.
- **Breaking:** `PdfRedline.Auto` and `SaveAs(".pdf")` no longer consult process-wide state. A comparison read back from JSON has no PDF to mark and says so (use `PdfRedline.Rendered`, or `MarkNewDocument` with the PDF's bytes).
- `ItemMerge` is an unsealed base class of `ItemMerge<T>`.
- `RecursiveAI.OneDiff`: the line type of `TextDiff.Lines` and `TextDiff.Pieces` is named `DiffRun` (it was `DiffPiece`, which clashed with DiffPlex's type in files that import both during a migration).
- `RecursiveAI.OneDiff.Documents`: plain-text comparisons now carry list items, so the JSON of existing text comparisons changes where lines start with clause markers.
- Exception messages start with a capital letter.

### Removed

- `RecursiveAI.OneDiff.Documents`: `Redline.Compare`, the alias of `Comparison.Of`. Use `Comparison.Of`.

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

- `RecursiveAI.OneDiff.Word` (DOCX source and tracked-changes writer) and `RecursiveAI.OneDiff.Nlp` (OneNLP value recognizer) are in the repository but not published, because they depend on previews of OneDocument and OneNLP that are not on nuget.org. Change semantics, combine, chains, the change report and the recognizer seam live in `RecursiveAI.OneDiff.Documents` and are in this release.
- Scans, spreadsheets, slides, the model and the OneDocument rendering package are not built yet.

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
