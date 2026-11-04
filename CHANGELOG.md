# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `RecursiveAI.OneDiff.Documents`: self-contained HTML redlines (`HtmlRedline`: inline and side by side, rendering `RedlineStyle` with cover page, change bars and footer), the `Redline` statics (`Create`, `Compare`), `Comparison.SaveAs` / `Save` / `ToBytes`, `Comparison.Of` over paths and inputs, and a registration seam (`DocumentSources.Register`, `Redline.Register`, `RedlineForm`) for adapter packages. `onediff compare`.
- `RecursiveAI.OneDiff.Documents`: the document engine's contracts. The compare model (block trees with source anchors, formatting runs, normalization folds), `IDocumentSource` with a plain-text source, the five stages (`Align`, `DiffPairs`, `Group`, `Classify`, `Verify`) with reference implementations, an editable `Alignment`, `Comparison` with its change ledger and diagnostics, `CompareSettings` and `RedlineStyle` presets, and a versioned JSON form (schema v1). Verification proves every comparison complete and falls back stepwise to a coarser complete grouping when it is not.
- Repository skeleton: the `RecursiveAI.OneDiff` core project, CI, tests and the command-line tool shell.
- Edit-script types (`Edit`, `EditScript`, `EditScriptBuilder`), `TokenInterner<T>`, line, word, character and grapheme tokenizers, and the `Diff` entry points.
