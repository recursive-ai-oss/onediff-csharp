# RecursiveAI.OneDiff.Ocr

**Text diff and document comparison for .NET.**

Scan comparison: a document source that reads scanned PDFs and images (PNG, JPEG, TIFF) through a local OCR engine into the compare model. Every word is anchored by its page quadrilateral and its recognition confidence, differences that may be recognition errors are flagged and never hidden, and the changes are marked on the original pages with real PDF annotations. Nothing leaves the machine.

```csharp
using OneDiff.Documents;
using OneDiff.Ocr;

OcrRedline.Register();                                  // scans first, digital PDFs still go to the PDF source
using var cmp = Comparison.Of("scan-v1.pdf", "scan-v2.pdf");
foreach (var c in cmp.Changes.Where(c => c.PossibleOcrError))
    Console.WriteLine($"{c.Old} -> {c.New} (confidence {c.OcrConfidence:0.00})");
```

Depends on `RecursiveAI.OneDiff.Pdf`, `RecursiveAI.OneOcr` and `RecursiveAI.OneOcr.Pdf`. See [docs/ocr.md](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/ocr.md).

Source, issues and the full README: https://github.com/recursive-ai-oss/onediff-csharp. Measured results, including the rows that do not meet their bar yet: [https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md).

This project is unaffiliated with any other project named OneDiff.

Licensed under MIT OR Apache-2.0. © Recursive AI, LLC.
