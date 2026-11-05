# Migrating from WmlComparer and Docxodus

This page maps the calls in the published README-level documentation of the Open-Xml-PowerTools `WmlComparer` family (as shown in the Docxodus package README) to the OneDiff calls that do the same job. It is a mapping only: OneDiff does not copy their types or shapes, and there is no compatibility layer. Where the documentation does not say what an option does exactly, the row says "closest" and you should check the result on your own documents. Rows marked *not provided* have no equivalent.

## What works at 1.0

Reading and writing Word files is in `RecursiveAI.OneDiff.Word`, which ships after 1.0 (it builds from this repository today). The 1.0 packages are `RecursiveAI.OneDiff`, `.Documents`, `.Pdf`, `.Ocr` and the `onediff` tool.

| Task | At 1.0, without `.Word` | With `.Word` |
|---|---|---|
| Compare two `.docx` files | **Not possible.** The input is recognized by its first bytes and the call throws `NotSupportedException`: "no document source is registered for this input, which looks like an Office Open XML or other zip-based document (packages RecursiveAI.OneDiff.Word, …). Reference that package and call its Register method once at startup." | `WordRedline.Register()` once, then `Comparison.Of("v1.docx", "v2.docx")` |
| Write a `.docx` with tracked changes | Not possible: asking for a `.docx` output names the package in the exception. | `comparison.SaveAs("redline.docx")` |
| Compare `.docx` against a PDF, write HTML, JSON or a PDF redline | Not possible (the `.docx` side cannot be read). | Works: `.Word` and `.Pdf` registered together. |
| Compare PDFs, text or scans; HTML, JSON, rendered-redline and on-page-markup outputs | Works. | Works. |

So a team that needs Word-in, Word-out redlines today uses the `.Word` package from this repository; nothing in the 1.0 set reads a `.docx`.

## The one-liner and the object model

`WmlComparer.Compare(a, b, settings)` and `DocxCompare.Compare(a, b)` each return a redlined document. The OneDiff equivalents split the same job in two, so that you can keep and inspect the comparison before choosing an output.

| WmlComparer / Docxodus | OneDiff | Notes |
|---|---|---|
| `new WmlDocument("v1.docx")` | A path, or `DocumentInput.FromBytes(bytes, name)` / `FromStream` / `FromString` | Inputs are sniffed by content, never by extension. |
| `var redline = WmlComparer.Compare(original, modified, settings); redline.SaveAs("redline.docx");` | `Redline.Create("v1.docx", "v2.docx", "redline.docx")` | The output form follows the extension of the output path. Needs `WordRedline.Register()` once. |
| the same, keeping the result | `var cmp = Comparison.Of("v1.docx", "v2.docx"); cmp.SaveAs("redline.docx");` | `Comparison` is the verified change list; any number of outputs can be written from it. `cmp.ToBytes(form)` gives bytes instead of a file. |
| `DocxCompare.Compare` compares the *accepted* view of each input | The default: `WordSourceOptions.Revisions = WordRevisionMode.Accepted` | `WordRevisionMode.AsOriginal` reads the documents as they were before their tracked changes; `Refuse` throws if a document holds any. Register a configured source with `WordRedline.Register(new WordSourceOptions { Revisions = … })`. |
| the redlined document opens in Word with real `w:ins` / `w:del` / moves | `DocxRedline`, built on the new document by default (`BuildOn = WordBuildOn.Old` builds on the old one) | Accept-all gives the new document and reject-all the old, over the compare model; what a revision cannot express comes back in `DocxRedlineResult.NotCarried` from `new DocxRedline().Create(cmp, stream)`. |

## Settings

| WmlComparer / Docxodus | OneDiff | Notes |
|---|---|---|
| `settings.AuthorForRevisions = "Redline"` | `cmp.SaveAs("redline.docx", new DocxRedline { Author = "Redline" })` | Default author `OneDiff`. |
| a revision date | `new DocxRedline { Date = someDate }` | No date is written unless you set one, so equal inputs give byte-identical files. |
| `settings.DetailThreshold` | *Not provided as such.* Closest: `CompareSettings.MatchThreshold` (how similar two paragraphs must be to count as the same one, default 0.5), `SplitMergeThreshold` and `MoveThreshold` | These govern how blocks are matched, not how finely words are compared; there is no single "detail" knob. |
| (no equivalent documented) | `CompareSettings.Legal` (default), `Strict`, `TextOnly`, `Hidden` categories, `CollapseCascades`, `TypedChanges`, `DetectMoves`, `CostCap`, `ThrowOnFailure` | See [compare-model.md](compare-model.md) and [semantics.md](semantics.md). `Strict` ignores nothing and throws instead of falling back. |

## Reading the revisions

| WmlComparer / Docxodus | OneDiff | Notes |
|---|---|---|
| `WmlComparer.GetRevisions(result, settings)`, a list of revisions | `cmp.Changes`, an `ImmutableArray<Change>` | OneDiff reads the changes from the comparison, not back out of the output file. |
| `rev.RevisionType` (Inserted, Deleted, Moved) | `change.Kind`: `Insert`, `Delete`, `Replace`, `Move`, `Split`, `Merge`, `Format`, `Attribute` | A replacement is one change with both sides, not a delete plus an insert. |
| `rev.Text` | `change.Old.Text` and `change.New.Text` | Every change carries both sides and both anchors. |
| `rev.MoveGroupId` | A `Move` change holds both places; `cmp.Groups` lists display groups | |
| `rev.Author` | *Not applicable* to a single comparison. `Redline.Combine` / `Combination` attribute each change to the reviewer's copy it came from. | |
| `GetEditScriptJson()` | `Redline.Create(a, b, "changes.json")` or `ComparisonJson.Write(cmp)`; `ComparisonJson.Read(bytes)` reads it back | A versioned schema, [schema/comparison-v1.json](schema/comparison-v1.json). |
| `GetSemanticChanges()` | `change.Type`, `change.OldValue` / `NewValue`, `change.IsSubstantive`, and `cmp.Ledger` | Typed values (`30 d → 60 d`), the substantive filter and the ledger of differences hidden by settings. |
| accept / reject revisions | *Not provided.* OneDiff writes redlines; accepting and rejecting is OneOffice's job (`RevisionAuthor`), which OneDiff uses to verify its own output. | |

```csharp
using OneDiff.Documents;
using OneDiff.Word;

WordRedline.Register();                                   // once at startup

var cmp = Comparison.Of("v1.docx", "v2.docx");
cmp.SaveAs("redline.docx", new DocxRedline { Author = "Redline" });

foreach (var change in cmp.Changes.Where(c => c.IsSubstantive))
    Console.WriteLine($"{change.Kind}: {change.Old.Text} -> {change.New.Text}");
```

## Consolidating reviewers' copies

Docxodus's README describes an N-way consolidate against one base. The OneDiff call is `Redline.Combine("original.docx", ["alice.docx", "bob.docx"], "combined.html")` (or `Combination.Of`), which attributes each change to its reviewer and flags conflicting edits without picking a winner. The output today is the change report (`.html`) or the JSON form; a DOCX with one revision author per reviewer comes with a later `.Word` release. See [combine.md](combine.md).

## Not covered here

Rendering, editing sessions, markdown projection, content-control helpers and the other parts of the Docxodus toolkit are not comparison, and OneDiff has no counterpart. The npm, Python and CLI surfaces of those packages have no OneDiff counterpart either; the OneDiff command line is `onediff compare v1.pdf v2.pdf -o markup.pdf` for the 1.0 formats.

The snippets and mappings above that show behaviour are covered by tests in the repository (`MigrationPageTests` in the `.Word` tests, and the unsupported-input message in the samples tests).
