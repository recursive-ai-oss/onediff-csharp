# RecursiveAI.OneDiff

**Text diff and document comparison for .NET.**

The text core: Myers (linear space), patience and histogram diff over any sequence, line, word, character and grapheme tokenizers, semantic and efficiency cleanup, unified and diff-match-patch patches with fuzzy apply, and three-way (diff3) merge with git's conflict styles. No dependency beyond the .NET base class library; `net10.0`, no native code, NativeAOT and trimming friendly.

```csharp
using OneDiff;

string patch = Diff.Unified(oldText, newText);
```

Migrating from DiffPlex: [docs/migrating-from-diffplex.md](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/migrating-from-diffplex.md).

> **Preview.** Early releases; the public API may change before 1.0.

Source, issues and the full README: https://github.com/recursive-ai-oss/onediff-csharp. Measured results, including the rows that do not meet their bar yet: [https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md).

This project is unaffiliated with any other project named OneDiff.

Licensed under MIT OR Apache-2.0. © Recursive AI, LLC.
