# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

#### Repository

- CI on linux-x64, linux-arm64, osx-arm64 and win-x64, a NativeAOT smoke app, and the core tests run as a browser WebAssembly app in headless Chrome.
- A release workflow that packs every packable project and publishes through nuget.org Trusted Publishing; see `docs/building.md`.

### Changed

- Building without `ONEDIFF_ARTIFACTS` set no longer fails on the default output folder.
