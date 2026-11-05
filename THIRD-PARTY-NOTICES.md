# Third-party notices

OneDiff's own code is licensed `MIT OR Apache-2.0`. Material from other projects that ships with a package, or that the tests use, is listed here.

| Material | Used by | Licence | Notes |
|---|---|---|---|
| RecursiveAI.OnePdf (PDF reading and annotation writing) | `RecursiveAI.OneDiff.Pdf` | MIT OR Apache-2.0 | A dependency, not bundled. The tests also use `RecursiveAI.OnePdf.Layout`, `.Fonts` and `.Hyphenation` to write their PDFs from text. |
| RecursiveAI.OneOffice.Documents (word-processing document hub) with RecursiveAI.OneArchive and RecursiveAI.OneCompress | `RecursiveAI.OneDiff.Word` | MIT OR Apache-2.0 | A dependency, not bundled. |
| RecursiveAI.OneNlp (recognizers for numbers, money, percentages, durations and dates) | `RecursiveAI.OneDiff.Nlp` | MIT OR Apache-2.0 | A dependency, not bundled; it depends on RecursiveAI.OneFont. Its own notices (a language-identification model trained on openly licensed text, including CC BY-SA 4.0 Wikipedia text, and the IANA, ISO 4217 and holiday data in its rules) ship in its package. This package does not call the language identifier. |
