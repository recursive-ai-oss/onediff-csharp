# OneDiff

**Text diff and document comparison for .NET.**

This project is unaffiliated with any other project named OneDiff, including the Python diffusion-model accelerator of that name.

OneDiff is a clean-room diff and comparison library for .NET, in two layers:

- **A text diff core** (`RecursiveAI.OneDiff`): Myers, patience and histogram diff over any sequence; line, word, character and grapheme tokenizers; semantic and efficiency cleanup; unified and diff-match-patch patches with fuzzy apply; three-way merge in git's conflict styles. It depends on nothing beyond the .NET base class library.
- **Document comparison** (`RecursiveAI.OneDiff.Documents` and the adapters): a compare model with source anchors, alignment that follows moves, splits and merges, typed changes with resolved values (`30 days → 60 days`), renumbering and renamed defined terms as single changes, combining reviewers' copies, and version chains. **PDFs** (digital, and scans through a local OCR engine) are read into the model; the changes are marked on the original pages with real annotations, written as a rendered redline PDF, as HTML (inline or side by side), or as a versioned JSON change list.

Every comparison is checked for completeness: old plus the changes gives new, and new minus the changes gives old. If a grouping cannot be inverted, the engine falls back to a coarser complete one and says so; strict mode throws instead. Differences that a setting hides (typography, page numbers, comments, …) are counted in a ledger, never dropped. Nothing needs a model: the deterministic engine keeps that promise on its own.

It targets `net10.0`, uses no native code, and is built to be NativeAOT-, trimming- and WebAssembly-friendly. The same inputs and settings give byte-identical output. Nothing touches the network.

## Install

```
dotnet add package RecursiveAI.OneDiff            # the text core
dotnet add package RecursiveAI.OneDiff.Documents  # document comparison, HTML and JSON
dotnet add package RecursiveAI.OneDiff.Pdf        # PDF in, PDF out
dotnet add package RecursiveAI.OneDiff.Ocr        # scans
dotnet tool install --global RecursiveAI.OneDiff.Tool   # the `onediff` command
```

| Package | Contents |
|---|---|
| `RecursiveAI.OneDiff` | The text core. Depends on nothing beyond the base class library. |
| `RecursiveAI.OneDiff.Documents` | The comparison engine: compare model, alignment, grouping, typed changes, verification, combine and chains, the change report, JSON, HTML redlines. Depends on the core and `RecursiveAI.OneFont`. |
| `RecursiveAI.OneDiff.Pdf` | Reads digital PDFs; marks changes on the original pages; writes a rendered redline PDF. Depends on `.Documents`, `RecursiveAI.OnePdf` and `RecursiveAI.OneRender.Pdf`. |
| `RecursiveAI.OneDiff.Ocr` | Reads scanned PDFs and images through `RecursiveAI.OneOcr`, with each word's confidence; differences that may be recognition errors are flagged. |
| `RecursiveAI.OneDiff.Tool` | The `onediff` tool: `diff`, `patch`, `merge`, `compare`, `combine`, `chain`. |

Coming in later releases: Word documents in and tracked-changes DOCX out (`RecursiveAI.OneDiff.Word`, in this repository and not in the first release), spreadsheets, slides, and the OneNLP value recognizer (`RecursiveAI.OneDiff.Nlp`). Until then a Word file is refused with a message that names the package.

## The happy path

Each line below is a runnable sample in [samples/OneDiff.Samples](samples/OneDiff.Samples/Samples.cs).

```csharp
using OneDiff;
using OneDiff.Documents;
using OneDiff.Documents.Serialization;
using OneDiff.Merge;
using OneDiff.Pdf;

// Text: the words that changed, a unified patch, a three-way merge.
string marked  = Diff.ToMarkdownInline(Diff.Words(oldText, newText));
string patch   = Diff.Unified(oldText, newText);
TextMerge both = Diff.Merge(baseText, ours, theirs);      // both.Text, both.HasConflicts

// PDF: a redline on the new document's own pages (highlights, with the old text in the pop-ups).
PdfRedline.Register();                                    // once at startup
Redline.Create("v1.pdf", "v2.pdf", "markup.pdf");

// PDF: a rendered, tagged redline PDF; or an HTML page with the two documents side by side.
var cmp = Comparison.Of("v1.pdf", "v2.pdf");
cmp.SaveAs("redline.pdf", PdfRedline.Rendered);
cmp.SaveAs("side-by-side.html", RedlineForm.HtmlSideBySide);

// The change list: walk what changed in substance, or save it as JSON and read it back.
foreach (var c in cmp.Changes.Where(c => c.IsSubstantive))
    Console.WriteLine($"{c.Kind}: {c.Old.Text} -> {c.New.Text}");
Redline.Create("v1.pdf", "v2.pdf", "changes.json");
Comparison again = ComparisonJson.Read(File.ReadAllBytes("changes.json"));
```

Inputs are recognized by their content, and the output form follows the extension of the output path. The same from a shell: `onediff compare v1.pdf v2.pdf -o markup.pdf`. A PDF that has no text layer is refused with a message that names `RecursiveAI.OneDiff.Ocr` (call `OcrRedline.Register()` to read scans); it is never compared as an empty document. For streams that refuse synchronous I/O, such as an ASP.NET Core request body, use `Redline.CreateAsync`, `Comparison.OfAsync`, `SaveAsync` and `ComparisonJson.ReadAsync`.

## The low level

Every layer is public: the comparison is five stages over immutable values, and the alignment between the two documents is a value you can correct and run again.

```csharp
using OneDiff.Documents;
using OneDiff.Documents.Model;      // CompareDocument
using OneDiff.Documents.Pipeline;   // Alignment

CompareDocument a = CompareDocument.FromText(oldText, "old");
CompareDocument b = CompareDocument.FromText(newText, "new");

Alignment alignment = Stages.Align(a, b);
alignment = alignment.Pin(oldNodeId, newNodeId);          // a reviewer's correction

Comparison result = Stages.Verify(Stages.Classify(Stages.Group(Stages.DiffPairs(alignment))));
```

## Documentation

Start at [docs/index.md](docs/index.md). The pages that matter first:

- Samples, every redline look, and how to choose a form: [docs/samples/README.md](docs/samples/README.md)
- Coming from DiffPlex: [docs/migrating-from-diffplex.md](docs/migrating-from-diffplex.md); from `WmlComparer` or Docxodus: [docs/migrating-from-wmlcomparer.md](docs/migrating-from-wmlcomparer.md)
- What a comparison is and how it is proved complete: [docs/compare-model.md](docs/compare-model.md)
- Measured results, including the rows that are pending or lose: [docs/results.md](docs/results.md). Nothing beyond that page is claimed. In short: Myers' edit length equals GNU diff's minimum on 9,483 corpus pairs, three-way merge equals `git merge-file` byte for byte on 2,599 real triples in all three conflict styles, and in the speed rows against DiffPlex and diff-match-patch every area's geometric-mean time ratio is below 1.0 (the worst single row is 1.13×, patch make against diff-match-patch on x64).

## Known issues

- In browser WebAssembly, reading scans with `RecursiveAI.OneDiff.Ocr` fails with a `TypeLoadException` from the image library it uses (OneImage). Scans compare normally on every native platform; text, document and digital-PDF comparison work in the browser.

## Building

```
dotnet build -c Release
dotnet test --solution OneDiff.slnx -c Release
```

Build output goes outside the checkout; set `ONEDIFF_ARTIFACTS` to choose where. See [docs/building.md](docs/building.md).

## License

Dual-licensed under either of [MIT](LICENSE-MIT) or [Apache-2.0](LICENSE-APACHE), at your option. © Recursive AI, LLC.
