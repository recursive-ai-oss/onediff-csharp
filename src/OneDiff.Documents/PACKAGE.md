# RecursiveAI.OneDiff.Documents

**Text diff and document comparison for .NET.**

The document comparison engine: a compare model with source anchors, staged alignment (moves, splits, merges), in-pair diff, grouping, classification (typed changes with resolved values, cascades and defined-term renames) and verification, a change ledger, combine and version chains, a change report, and a versioned JSON form. Every comparison is checked for completeness: old plus changes gives new, and new minus changes gives old; otherwise it falls back to a coarser complete grouping. HTML redlines, inline and side by side.

```csharp
using OneDiff.Documents;

Redline.Create("v1.txt", "v2.txt", "redline.html");
```

Depends on `RecursiveAI.OneDiff` and `RecursiveAI.OneFont`.

> **Preview.** Early releases; the public API may change before 1.0.

Source, issues and the full README: https://github.com/recursive-ai-oss/onediff-csharp. Measured results, including the rows that do not meet their bar yet: [https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md).

This project is unaffiliated with any other project named OneDiff.

Licensed under MIT OR Apache-2.0. © Recursive AI, LLC.
