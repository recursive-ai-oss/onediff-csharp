# RecursiveAI.OneDiff.Pdf

**Text diff and document comparison for .NET.**

PDF comparison: a document source that reads digital PDFs into the compare model with glyph-quad anchors, a writer that marks the changes on the original pages with real PDF annotations, and a rendered redline PDF laid out from any comparison.

```csharp
using OneDiff.Documents;
using OneDiff.Pdf;

PdfRedline.Register();                                   // once at startup
Redline.Create("v1.pdf", "v2.pdf", "markup.pdf");        // highlights and notes on v2's pages
Comparison.Of("v1.pdf", "v2.pdf").SaveAs("redline.pdf", PdfRedline.Rendered);   // a new, tagged PDF
```

Depends on `RecursiveAI.OneDiff.Documents`, `RecursiveAI.OnePdf` and `RecursiveAI.OneRender.Pdf`.

More: [how PDFs are read and marked](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/pdf.md)

Source, issues and the full README: https://github.com/recursive-ai-oss/onediff-csharp. Measured results, including the rows that are pending or lose: [https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md).

This project is unaffiliated with any other project named OneDiff.

Licensed under MIT OR Apache-2.0. © Recursive AI, LLC.
