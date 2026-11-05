# RecursiveAI.OneDiff.Tool

**Text diff and document comparison for .NET.**

The `onediff` command-line tool: `diff`, `patch`, `merge`, `compare`, `combine` and `chain` over the OneDiff libraries, for text and PDF files. Exit codes follow GNU diff, GNU patch and git merge-file.

```text
dotnet tool install --global RecursiveAI.OneDiff.Tool
onediff diff -u old.txt new.txt
onediff compare v1.pdf v2.pdf -o markup.pdf     # changes marked on the pages of v2.pdf
onediff compare v1.txt v2.txt -o redline.pdf     # a rendered redline PDF
```

The tool carries the PDF packages (`RecursiveAI.OneDiff.Pdf`, OnePDF and OneRender), so it is larger than the text tools. Only Latin text is drawn in rendered redlines. Word documents are not read by this tool.

More: [patches](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/patches.md), [merge](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/merge.md)

Source, issues and the full README: https://github.com/recursive-ai-oss/onediff-csharp. Measured results, including the rows that are pending or lose: [https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md](https://github.com/recursive-ai-oss/onediff-csharp/blob/main/docs/results.md).

This project is unaffiliated with any other project named OneDiff.

Licensed under MIT OR Apache-2.0. © Recursive AI, LLC.
