# OneDiff

**Text diff and document comparison for .NET.**

OneDiff is a clean-room diff and comparison library for .NET, in two layers:

- **A text diff core:** Myers, patience and histogram diff over any sequence, line, word, character and grapheme tokenizers, semantic cleanup, unified and diff-match-patch patches with fuzzy apply, and three-way merge. It depends on nothing beyond the .NET base class library.
- **Document comparison:** a compare model with source anchors, alignment that follows moves, splits and merges, typed changes with resolved values, renumbering and renamed defined terms as single changes, combining reviewers' copies, and version chains. **PDFs** are read into the model and the changes are marked on the original pages with real annotations, or written as a rendered redline PDF.

Every comparison is checked for completeness: old plus the changes gives new, and new minus the changes gives old. If a grouping cannot be inverted, the engine falls back to a coarser complete one and says so; strict mode throws instead. The deterministic engine keeps that promise on its own.

It targets `net10.0`, uses no native code, and is designed to be NativeAOT-, trimming- and WebAssembly-friendly. The same inputs and settings give byte-identical output.

> **Preview.** This is an early preview, and APIs may change before 1.0. Measured results, including the rows that do not meet their bar and the ones still pending, are in [docs/results.md](docs/results.md); nothing beyond that page is claimed.

This project is unaffiliated with any other project named OneDiff, including the Python diffusion-model accelerator of that name.

## Packages

On nuget.org in this preview:

| Package | Contents |
|---|---|
| `RecursiveAI.OneDiff` | The text core: diff algorithms, tokenizers, cleanup, unified and diff-match-patch patches, three-way merge. Depends on nothing beyond the .NET base class library. |
| `RecursiveAI.OneDiff.Documents` | The document comparison engine: compare model, alignment, grouping, typed changes, cascades and renames, verification, combine and chains, the change report, JSON, and HTML redlines. Depends on the core and `RecursiveAI.OneFont`. |
| `RecursiveAI.OneDiff.Pdf` | PDF compare: reads digital PDFs, marks the changes on the original pages, and writes a rendered redline PDF. Depends on `.Documents`, `RecursiveAI.OnePdf` and `RecursiveAI.OneRender.Pdf`. |
| `RecursiveAI.OneDiff.Tool` | The `onediff` command-line tool: `diff`, `patch`, `merge`, `compare`, `combine` and `chain`. |

Preview, not yet on nuget.org (they depend on previews of other libraries that are not published yet; they build from this repository):

| Package | Contents |
|---|---|
| `RecursiveAI.OneDiff.Word` | DOCX source through the OneOffice document hub, and a writer for DOCX with real tracked changes. |
| `RecursiveAI.OneDiff.Nlp` | Typed values from OneNLP: written-out numbers, amounts, durations and percentages. |

Further packages (scans, spreadsheets, slides, the model) arrive later.

## Examples

```csharp
using OneDiff;
using OneDiff.Documents;
using OneDiff.Pdf;

// Text core: the edits between two strings, as a unified diff.
string patch = Diff.Unified(oldText, newText);

// A PDF compare, marked on the new document's own pages (highlights, with the old text in the pop-ups).
PdfRedline.Register();                                              // once at startup
Redline.Create("v1.pdf", "v2.pdf", "markup.pdf");

// A rendered redline PDF: a new, tagged document laid out from the comparison.
var cmp = Comparison.Of("v1.pdf", "v2.pdf");
cmp.SaveAs("redline.pdf", PdfRedline.Rendered);
```

Inputs are recognized by their content, and the output form follows the extension of the output path. More, including every redline look: [docs/samples/README.md](docs/samples/README.md). Coming from DiffPlex: [docs/migrating-from-diffplex.md](docs/migrating-from-diffplex.md).

## Results

[docs/results.md](docs/results.md) is the results table: the exact gates, the speed rows against DiffPlex and diff-match-patch, the PDF engine's gates, the gold-set scores, and the rows that are still pending or lose.

## Building

```
dotnet build -c Release
dotnet test --solution OneDiff.slnx -c Release
```

Build output goes outside the checkout; set `ONEDIFF_ARTIFACTS` to choose where. See [docs/building.md](docs/building.md) for CI, packing and preview dependencies.

## License

Dual-licensed under either of [MIT](LICENSE-MIT) or [Apache-2.0](LICENSE-APACHE), at your option. © Recursive AI, LLC.
