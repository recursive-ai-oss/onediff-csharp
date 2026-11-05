# RecursiveAI.OneDiff

**Text diff and document comparison for .NET.**

The text core: Myers (linear space), patience and histogram diff over any sequence; line, word, character and grapheme tokenizers; semantic and efficiency cleanup; unified and diff-match-patch patches with fuzzy apply; and three-way (diff3) merge with git's conflict styles. No dependency beyond the .NET base class library; `net10.0`, no native code, NativeAOT and trimming friendly.

```csharp
using OneDiff;
using OneDiff.Merge;

string patch = Diff.Unified(oldText, newText);              // a unified diff
string text  = Diff.ApplyUnified(oldText, patch).Text;     // and back to the new text
TextMerge m  = Diff.Merge(baseText, ours, theirs);         // m.Text, m.HasConflicts
```

Depends on nothing but the base class library.

More: [migrating from DiffPlex](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/migrating-from-diffplex.md), [algorithms](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/diff-algorithms.md), [patches](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/patches.md), [merge](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/merge.md)

Source, issues and the full README: https://github.com/recursive-ai-oss/onediff-csharp. Measured results, including the rows that are pending or lose: [https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md).

This project is unaffiliated with any other project named OneDiff.

Licensed under MIT OR Apache-2.0. © Recursive AI, LLC.
