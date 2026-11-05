# RecursiveAI.OneDiff.Ocr

**Text diff and document comparison for .NET.**

Scan comparison: a document source that reads scanned PDFs and images through a local OCR engine (`RecursiveAI.OneOcr`) into the compare model, with every word anchored by its page quadrilateral and its recognition confidence. A difference in words that were read with low confidence is flagged as a possible recognition error, never hidden. Changes are marked on the original scan pages, as for a digital PDF.

```csharp
using OneDiff.Documents;
using OneDiff.Ocr;

OcrRedline.Register();                                  // once at startup (also registers PDF)
Redline.Create("scan-v1.pdf", "scan-v2.png", "marked.pdf");
var flagged = Comparison.Of("scan-v1.pdf", "scan-v2.png").Changes.Where(c => c.PossibleOcrError);
```

Depends on `RecursiveAI.OneDiff.Pdf` and `RecursiveAI.OneOcr`.

More: [scans and the recognition flag](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/ocr.md)

Source, issues and the full README: https://github.com/recursive-ai-oss/onediff-csharp. Measured results, including the rows that are pending or lose: [https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md).

This project is unaffiliated with any other project named OneDiff.

Licensed under MIT OR Apache-2.0. © Recursive AI, LLC.
