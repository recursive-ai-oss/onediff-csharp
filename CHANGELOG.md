# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `RecursiveAI.OneDiff.Pdf`: `PdfDocumentSource` reads digital PDFs into the compare model (paragraphs joined across lines, columns and pages, list items, tables, running headers and footers, formatting, glyph-quad anchors; line breaks, hyphenation and page numbers go to the ledger) and `PdfRedline` marks a comparison on the original pages with real highlight, caret, underline, squiggly and strike-out annotations carrying the old text in their pop-ups. See `docs/pdf.md`.
- `RecursiveAI.OneDiff.Documents`: the document engine's contracts. The compare model (block trees with source anchors, formatting runs, normalization folds), `IDocumentSource` with a plain-text source, the five stages (`Align`, `DiffPairs`, `Group`, `Classify`, `Verify`) with reference implementations, an editable `Alignment`, `Comparison` with its change ledger and diagnostics, `CompareSettings` and `RedlineStyle` presets, and a versioned JSON form (schema v1). Verification proves every comparison complete and falls back stepwise to a coarser complete grouping when it is not.
- Repository skeleton: the `RecursiveAI.OneDiff` core project, CI, tests and the command-line tool shell.
- Edit-script types (`Edit`, `EditScript`, `EditScriptBuilder`), `TokenInterner<T>`, line, word, character and grapheme tokenizers, and the `Diff` entry points.
