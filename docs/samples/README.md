# Samples

The runnable code samples (one task per method) are in [samples/OneDiff.Samples](../../samples/OneDiff.Samples/Samples.cs). This page covers the rendered files and the choice of form.

Every file here is made from a short pair of documents written for the purpose (a list of clauses with an edit, an insertion, a deletion, a move and a formatting change), by the tests that check the writers. A change in the look shows up as a change to these files in review. To rewrite them after an intended change, run the tests with `ONEDIFF_UPDATE_SAMPLES=1`.

| Files | What they show |
|---|---|
| `<preset>-inline.html`, `<preset>-side-by-side.html` | The HTML redline in each preset: `blackline`, `sidebyside`, `word`, `developer`. |
| `<preset>-inline.pdf`, `<preset>-side-by-side.pdf` | The rendered redline PDF in the same presets and layouts: a new, tagged PDF laid out from the comparison (A4, landscape for side by side). |
| `markup-on-pages.pdf` | Original-page markup: two short PDFs compared, and the changes marked as annotations on the new PDF's pages (the pages themselves are untouched). |

## Happy path, one or two lines each

A PDF output needs `PdfRedline.Register()` once at startup. The output form follows the extension of the output path, and inputs are recognized by their content.

```csharp
// Text core: the edits between two strings, as a unified diff.
string patch = Diff.Unified(oldText, newText);

// Redline as a self-contained HTML page (inline), and as the versioned change list (JSON).
Redline.Create("v1.txt", "v2.txt", "redline.html");
Redline.Create("v1.txt", "v2.txt", "changes.json");

// Redline as a PDF. Any input kind renders to a blackline; a PDF as the new document is marked on its own pages.
PdfRedline.Register();
Redline.Create("v1.txt", "v2.txt", "redline.pdf");                  // rendered blackline
Redline.Create("v1.pdf", "v2.pdf", "markup.pdf");                   // highlights and notes on v2's pages

// Choose the form or the look yourself.
var cmp = Comparison.Of("v1.pdf", "v2.pdf");
cmp.SaveAs("redline.pdf", PdfRedline.Rendered);                     // rendered redline from PDF inputs
cmp.SaveAs("side-by-side.pdf", PdfRedline.RenderedSideBySide, RedlineStyle.SideBySide);
cmp.SaveAs("redline.html", RedlineForm.HtmlSideBySide);
```
