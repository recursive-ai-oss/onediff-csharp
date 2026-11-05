# RecursiveAI.OneDiff.Pdf

**Text diff and document comparison for .NET.**

PDF comparison: a document source that reads digital PDFs into the compare model with glyph-quad anchors, a writer that marks the changes on the original pages with real PDF annotations, and a rendered redline PDF laid out from any comparison.

```csharp
using OneDiff.Documents;
using OneDiff.Pdf;

PdfRedline.Register();
Redline.Create("v1.pdf", "v2.pdf", "markup.pdf");   // highlights and notes on v2's pages
```

Depends on `RecursiveAI.OneDiff.Documents`, `RecursiveAI.OnePdf` and `RecursiveAI.OneRender.Pdf`. See [docs/pdf.md](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/pdf.md).

> **Preview.** Early releases; the public API may change before 1.0.

Source, issues and the full README: https://github.com/recursive-ai-oss/onediff-csharp. Measured results, including the rows that do not meet their bar yet: [https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md).

This project is unaffiliated with any other project named OneDiff.

Licensed under MIT OR Apache-2.0. © Recursive AI, LLC.
