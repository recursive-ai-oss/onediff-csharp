# Third-party notices

OneDiff's own code is licensed `MIT OR Apache-2.0`. Material from other projects that ships with a package, or that the tests use, is listed here.

| Material | Used by | Licence | Notes |
|---|---|---|---|
| RecursiveAI.OnePdf (PDF reading and annotation writing) | `RecursiveAI.OneDiff.Pdf` | MIT OR Apache-2.0 | A dependency, not bundled. The tests also use `RecursiveAI.OnePdf.Layout`, `.Fonts` and `.Hyphenation` to write their PDFs from text. |
| RecursiveAI.OneOffice.Documents (word-processing document hub) with RecursiveAI.OneArchive and RecursiveAI.OneCompress | `RecursiveAI.OneDiff.Word` | MIT OR Apache-2.0 | A dependency, not bundled. |
| RecursiveAI.OneOcr, RecursiveAI.OneOcr.Pdf and, through the first, RecursiveAI.OneOcr.Models (local OCR engine, PDF page reader and the recognizer weights) with RecursiveAI.OneImage and RecursiveAI.OneImage.Pdf | `RecursiveAI.OneDiff.Ocr` | MIT OR Apache-2.0 | Dependencies, not bundled. The weights are carried by `RecursiveAI.OneOcr.Models`, which ships its own THIRD-PARTY-NOTICES.md: the Unicode License v3 for the normalization tables, and the credits and licences of the training data and of the fonts that rendered synthetic training text (CORD v2, DocLayNet 1.0.0, PubLayNet 1.0.0, Project Gutenberg texts, public web pages of US federal agencies, generated receipts, synthetic pages). Keep that file with any redistribution of those packages. |
| Synthetic scan pages (the scan gold set) | tests and tooling only | our own | Pages of our synthetic pairs, drawn to pixels and degraded with seeded noise by `tools/OneDiff.GoldSets.Scan`; nothing is committed. |
