# Third-party notices

OneDiff's own code is licensed `MIT OR Apache-2.0`. Material from other projects that ships with a package, or that the tests use, is listed here.

| Material | Used by | Licence | Notes |
|---|---|---|---|
| RecursiveAI.OnePdf (PDF reading and annotation writing) | `RecursiveAI.OneDiff.Pdf` | MIT OR Apache-2.0 | A dependency, not bundled. The tests also use `RecursiveAI.OnePdf.Layout`, `.Fonts` and `.Hyphenation` to write their PDFs from text. |
| RecursiveAI.OneRender.Pdf (HTML to tagged PDF) | `RecursiveAI.OneDiff.Pdf` | MIT OR Apache-2.0 | A dependency, not bundled. It brings OneRender, OneHtml and OneImage, and `RecursiveAI.OneRender.Fonts`: Liberation Sans, Serif and Mono, Carlito and Caladea, unmodified, `(MIT OR Apache-2.0) AND OFL-1.1`. Subsets of Liberation Sans are embedded in the PDFs a rendered redline writes; the SIL Open Font License 1.1 permits that. The tests also use `RecursiveAI.OneRender.Fonts.Cjk` (Noto Sans CJK), `.Fonts.Noto.Arabic` and `.Fonts.Noto.Devanagari` (Noto Sans), all OFL-1.1, to render those scripts. |
