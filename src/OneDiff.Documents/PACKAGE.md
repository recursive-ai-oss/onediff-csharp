# RecursiveAI.OneDiff.Documents

**Text diff and document comparison for .NET.**

The document comparison engine: a compare model with source anchors, staged alignment (moves, splits, merges), an in-pair diff, grouping, typed changes with resolved values, cascades and defined-term renames, verification, a change ledger, combine and version chains, a change report, a versioned JSON form, and HTML redlines (inline and side by side). Every comparison is checked for completeness: old plus changes gives new, and new minus changes gives old; otherwise it falls back to a coarser complete grouping. Reads plain text; PDFs, scans and Word documents come from the adapter packages.

```csharp
using OneDiff.Documents;

Redline.Create("v1.txt", "v2.txt", "redline.html");          // the form follows the extension
var cmp = Comparison.Of("v1.txt", "v2.txt");
foreach (var c in cmp.Changes.Where(c => c.IsSubstantive))
    Console.WriteLine($"{c.Kind}: {c.Old.Text} -> {c.New.Text}");
```

Depends on `RecursiveAI.OneDiff` and `RecursiveAI.OneFont`.

More: [the compare model and the guarantee](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/compare-model.md), [change semantics](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/semantics.md), [outputs](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/output-html.md), [migrating from WmlComparer](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/migrating-from-wmlcomparer.md)

Source, issues and the full README: https://github.com/recursive-ai-oss/onediff-csharp. Measured results, including the rows that are pending or lose: [https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md).

This project is unaffiliated with any other project named OneDiff.

Licensed under MIT OR Apache-2.0. © Recursive AI, LLC.
