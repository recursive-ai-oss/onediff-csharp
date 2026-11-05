# PDF comparison

How `RecursiveAI.OneDiff.Pdf` reads a digital PDF into the compare model, and how it marks a comparison on the original pages. Everything stated here is checked by the package's tests.

## 1. Reading a PDF

`PdfDocumentSource` recognizes a PDF by its `%PDF-` header within the first 1024 bytes (ISO 32000-2 7.5.2), never by file name. It reads the text with OnePDF's extraction (glyphs, words, lines, blocks in reading order, header and footer roles) and builds the flowing compare model.

### Structure

| Source content | Node |
|---|---|
| Body block | `Paragraph`; a block that starts with a bullet, a number or a letter marker (`•`, `-`, `1.`, `(a)`, `iv)`) is a `ListItem` with the marker as the `list.label` attribute and the rest as its text |
| A marker that the text analysis made a block of its own, beside a block on the same baseline | the same `ListItem` |
| Block recognized as a table | `Table` → `TableRow` (a line) → `TableCell` (the words between two cell starts) → `Paragraph` |
| Running headers and footers | a second `Section` after the body: one `Header` and one `Footer` node for a page whose header or footer text differs from the previous page's, holding its paragraphs. Pages that repeat the text add nothing, so a document with more pages does not gain a header per page |

Body paragraphs are joined where layout cut them:

- **Across pages:** the last paragraph of a page and the first of the next are one paragraph when the first does not end a sentence (`. ! ? : ;` and their CJK, Arabic and Devanagari forms, looking through closing quotes and brackets) and the second starts with a letter that is not upper case.
- **Across columns:** within a page, a block that starts higher up the page than the previous one ended continues it under the same rule.
- **Split-off lines:** a block that starts closer under the previous line than any paragraph gap (less than 0.4 line heights) continues it under the same rule. The text analysis splits a justified first line off this way.
- A list item is continued the same way; a table never is.

Not in the model: images, vector graphics, annotations, form fields and document properties.

### Formatting

Each word carries the formatting of its first glyph: `run.bold` (font name contains bold, black, heavy, semibold or demi), `run.italic` (italic or oblique), `run.font` (the font name up to the first `-` or `,`), `run.size` (points, rounded to half a point) and `run.color` (`#RRGGBB`, left out for black). A run goes on over the space to a following word with the same formatting.

### Ledger folds

| Fold | Category | Where |
|---|---|---|
| line break inside a paragraph → one space (nothing between two characters of a script written without spaces: CJK, fullwidth forms, Thai) | `LineBreaks` | body and furniture |
| hyphen (`-`, U+2010, soft hyphen) at the end of a line, preceded by a letter, followed by a lower-case letter on the next line → nothing | `Hyphenation` | body and furniture |
| number equal to the page's number, the page's label or the page count, in a header or footer → `#` | `PageNumbers` | furniture |
| quotes, dashes, runs of spaces | as for every source | body and furniture |

Each fold keeps the original characters, so settings that do not hide a category show its differences as ordinary changes.

### Anchors

A node's anchor has one quadrilateral per line (for a list item, the line includes the marker). For any other block it is the hull of the block's own words, one box per run of neighbouring words: a run ends where the line changes, the reading direction turns, or a gap wider than two line heights opens, because whatever lies in such a gap (another table cell or column) is not the block's. Its text is covered by segments, one per word, one per punctuation mark and one per space between two words on a line (cut as the text core's word tokenizer cuts; a space at a line or page break, or across a gap wider than two line heights, has none), each with one quadrilateral: **a change found by a word diff therefore begins and ends on segment boundaries, and its anchor covers exactly the changed text**.

Quadrilaterals use the coordinates OnePDF reports: points, **upright page space** (origin at the lower left of the page as displayed, y up, the page's `/Rotate` and crop box applied; ISO 32000-2 7.7.3.3), a zero-based page index, and the corners in the order **lower left, lower right, upper right, upper left** of the text. A token's quadrilateral comes from its glyphs when the glyphs spell the word (left-to-right, horizontal text), otherwise from sharing the word's box evenly along the writing direction. In right-to-left lines the glyph order is not stated precisely enough to rely on, so the order whose glyph texts spell the word is found by trying the plausible ones.

**Overlapping glyphs.** Glyph boxes of different words, or of other characters of the same word, can overlap on the page (a line of underscores running under the next word, a line of stars with text printed over it). A token's box is cut back, midway between its own outermost glyph centre and the foreign glyph's centre, until no glyph that is not the token's own has its centre inside it; a glyph from the line above or below is cut off vertically. Where a foreign glyph lies between two of the token's own glyphs, no box can leave it out, and a highlight takes it with them: the page genuinely has two texts in one place.

### Errors and limits

`Read` throws `InvalidDataException` for an input it cannot read; `PdfSourceErrors.FailureOf` returns why: `Malformed`, `Encrypted` (no password, or a wrong one; `PdfSourceOptions.Password` supplies it) or `TooLarge` (`MaxPages` default 10,000; `MaxCharacters` default 64 million; OnePDF's own limits). A truncated or damaged file that OnePDF can repair is read as repaired. Cancellation throws `OperationCanceledException`.

## 2. Original-page markup

`PdfRedline` (a sealed class and a `RedlineForm`; preset `PdfRedline.OnPages`) writes real markup annotations (ISO 32000-2 12.5.6.10 to 12.5.6.14) onto the pages of the new PDF with `MarkNewDocument`, and optionally onto the old one with `MarkOldDocument`. The PDF's own bytes must be the input the comparison read (their SHA-256 is checked against the document's source hash). Page content is never touched.

| Change | On the new document | On the old document |
|---|---|---|
| Insert | Highlight, insertion colour | – |
| Replace, Split, Merge | Highlight, insertion colour; pop-up holds the old text | StrikeOut, deletion colour; pop-up holds the new text |
| Delete | Caret where the text was, deletion colour; pop-up holds the old text | StrikeOut |
| Move | Highlight, move-to colour; pop-up holds the old text | Highlight, move-from colour |
| Format | Underline, formatting colour; the first line names the old and new formatting | Underline |
| Attribute (list label) | Squiggly, formatting colour; the first line names the attribute and both values | Squiggly |

- **Colours and glyphs** come from `PdfRedline.Style`, default `RedlineStyle.SideBySide` (blue insertions, orange deletions, `+` and `−`).
- **Where a caret goes.** For a deletion inside a paragraph, at the end of the text before it (or the start of the paragraph's text when the deletion is first). For a deleted paragraph, after the last text before it that has a place in the new document, or at the start of the document's text when there is none.
- **Contents.** The first line names the change and starts with the style's glyph (`+ Inserted`, `+ Replaced`, `− Deleted`, `← Moved`, …); the text after the first line feed is the other side's text exactly (`PdfRedline.SplitContents`). `/T` is `Author` (default `OneDiff`).
- **Names.** `/NM` is `onediff-c{change id}-p{page}`, with `.{n}` for further annotations of the same change on the same page (a change of more than 2048 quadrilaterals). A change spanning pages gets one annotation per page. Every annotation maps to one change; every change has annotations or is listed in `PdfMarkupResult.Skipped`. The only skipped changes are those of containers with no text of their own (a section, a table, a row), whose content carries its own changes.
- **Quadrilaterals** on one line that follow one another are merged, so a phrase is one band.
- **Pop-ups** sit to the right of the marked area when the page has room, else to its left, closed.
- **Rotation.** Anchors are in upright space; the writer maps them to default user space through the page's crop box and `/Rotate`.
- **Determinism.** The same comparison, PDF and settings give the same bytes on every platform. `/M` is written only when `ModifiedDate` is set; nothing reads the clock. Identifiers come from the change ids and OnePDF's hash-derived `/ID`.
- **Saving.** Incremental by default: the output is the original bytes followed by an update, so signatures over the original stay in place where their permissions allow markup (12.8.2.2). A file that was rebuilt on opening cannot take an update and is rewritten whole; `PdfMarkupResult.SavedIncrementally` says which happened. `Incremental = false` always rewrites.
- **Encrypted input.** Needs `PdfRedline.Password`; the output keeps the input's encryption.

### As an output form

`PdfRedline.Register()` registers `PdfDocumentSource` and `PdfRedline.Auto` (name `pdf-auto`, extension `.pdf`) with the `Redline` statics, once and idempotently. **`Auto` follows the new document** [Agent default]: when the comparison's new document was read from a PDF, it writes `PdfRedline.OnPages` (the new document with the marks, with the `SideBySide` look unless the caller or the settings chose one); otherwise it writes the rendered redline of section 4. So `Redline.Create("a.pdf", "b.pdf", "out.pdf")` marks the new PDF's pages, `Redline.Create("a.txt", "b.txt", "out.pdf")` renders a blackline, and a pair with a PDF as the new document and a text file as the old one is marked on the PDF. Both forms stay selectable by name: `cmp.SaveAs("out.pdf", PdfRedline.Rendered)` renders a PDF pair, and `PdfRedline.OnPages` on a comparison whose new document is not a PDF throws.

The marking form needs the new document's bytes, which a `Comparison` does not hold: `PdfDocumentSource.Read` keeps the bytes (and the password) of every document it reads in a process-wide weak table keyed by the compare document, so the form finds them while the document is alive. A comparison whose new document did not come from a PDF read in this process (a deserialized one, or one built by hand) cannot be marked through `SaveAs`; the form throws `InvalidOperationException` saying so, and `MarkNewDocument(comparison, bytes, stream)` takes the bytes directly. The style passed to `SaveAs` replaces `Style`.

### Whole words on the page

A word diff can end inside a word (a character-level refinement of a number or an identifier), and the text core reads some runs as one word (`10.8`, `a_b`). The page marks **whole tokens of the text core's word tokenizer**: a changed range that cuts a token is rounded out to the token. The other side's text in the pop-up is exact. A change that is only white space across a line break has no box of its own and is anchored on its block; the check has no text to compare for it.

## 3. Reading the marks back

The tests re-extract every marked PDF with OnePDF and check, for every pair: each annotation has a change; each change has its annotations or a skip reason; the text under the quadrilaterals of the annotations of a change equals the change's new text (spaces, line-end hyphens and typographic variants aside, and with folded page numbers read as the digits the page shows); each pop-up's text after the first line equals the change's old text. The text under a change that cuts a token is compared with the change's text rounded out to the token, taken from the text as the page shows it. Where glyph boxes of different words overlap on the page and a highlight cannot avoid taking the other word (see Overlapping glyphs), the text under it may also be given with the overlapping words left out; the check accepts that, and only that, as equal.

The gate set is run by `PdfGateRunTests` (environment `ONEDIFF_PDF_GATE_DIR`, `_FROM`, `_COUNT`, `_JOBS`, `_OUT`) over every pair, with the Legal and the Strict settings: verification passed with no fallback and a clean re-check; the marks re-extracted as above on the new document, and the old document's marks likewise; a second run giving byte-identical comparison JSON and marked PDFs.

## 4. Rendered redline

`PdfRedline.Rendered` (inline) and `PdfRedline.RenderedSideBySide` are `RenderedPdfRedline` forms: a new PDF laid out from the comparison alone, for any kind of input, plain text included. The package builds the same page as the HTML redline (`docs/output-html.md`: cover page, key, marks, ledger, footer with the input names and hashes) with a style sheet for paper, and renders it with OneRender's `HtmlPdf.ToPdf`. `Render(comparison, stream, style)` is `Write` plus a result with the page count and the renderer's notes.

- **Layout.** A4 (`Paper = PdfPaper.Letter` for Letter: `new RenderedPdfRedline { Paper = PdfPaper.Letter }`), margins 18 mm and 16 mm, the running page number in the bottom margin. Side by side is landscape: one grid row per block, old on the left and new on the right, and a row is kept whole on a page when it fits on one (see the limits below). The cover page, change bars and footer follow the style (`CoverPage`, `ChangeBars`, `Footer`); the timestamp only when the style carries one. Each block takes its direction from its first strong character (`unicode-bidi: plaintext`).
- **Look.** The comparison settings' style unless the caller passes one; `Blackline` for the inline layout and `SideBySide` for the side-by-side layout when the settings left the style at the default. Marks are drawn with the style's colours, decorations (underline, double underline, strike-through, highlight) and glyphs. A control character the page shows as a control picture takes the colour of the change it is in.
- **Static.** The page uses no script, no network and no font outside the ones the renderer carries (Liberation Sans). The text-for-screen-readers labels of the HTML page ("inserted:", "deleted:") are not written, since the colour, decoration and glyph of the style carry the meaning and the key explains them. The page's closed ledger sections are written open.
- **Scripts.** The renderer draws a script only when a font that covers it is referenced by the application: `RecursiveAI.OneRender.Fonts.Cjk`, `.Fonts.Noto.Arabic`, `.Fonts.Noto.Devanagari` and the other `.Fonts.Noto.<Script>` packages, `.Fonts.Emoji`. Each package registers itself when the application references it; nothing in `RecursiveAI.OneDiff.Pdf` changes. Without a font the characters are not drawn but stay in the text layer, so extraction and search still find them (the renderer's notes do not say so; see the limits).
- **Tagged.** The output is a tagged PDF with the document language (`RedlineStyle.Language`) and title (`RedlineStyle.Title`, or "Comparison of *old* and *new*") set; headings, paragraphs, the counts and ledger tables and the lists carry the renderer's structure types.
- **Deterministic.** The same comparison and style give the same bytes: no creation date is written and the clock is never read.
- **Limits** (renderer 1.3.2; `OneRenderGapTests` pins each with a minimal case):
  - a table row with two cells longer than a page loses the lines that pass the bottom of the page when it is cut there. The side-by-side page therefore keeps each row whole where it fits on a page and does not repeat the column headings on each page; a row taller than a page can still lose lines at its cut;
  - the zero-width joiner of an emoji sequence is not in the text layer when the emoji font is absent;
  - after a colour emoji (with `RecursiveAI.OneRender.Fonts.Emoji` referenced) the text that follows on the page is written with an invisible text mode.

### Reading the rendered pages back

`RenderedGate` (in the tests) re-extracts a rendered PDF with OnePDF and checks it against the comparison. For an inline page it puts every glyph in a class by its fill colour (inserted, deleted, moved from, moved to, formatting, plain; a style whose colours differ is needed, and the gate uses the blackline with its own colour for moves from). Without the classes that are not in the new document it must hold the new text, and without those not in the old document the old text, in order, block by block; each edit's inserted text must be in the inserted class and its deleted text in the deleted class. For a side-by-side page the left half must hold the old blocks and the right half the new blocks. Text is compared with whitespace and joiners left out, as the page shows it (control characters as their control pictures, the folds of categories that are not hidden undone). A comparison with right-to-left text anywhere is checked as a bag of characters instead of in sequence, because the extraction does not read neutral characters and mixed-direction runs back in logical order.

What the extraction cannot see: underlines, strike-throughs and highlights are drawn as shapes, so a style whose marks differ only by decoration (the Word preset's insertions and deletions are both red) is checked for text only; the page's colour is the only styling it reads.

The text corpus and the PDF pairs are run by `RenderedGateRunTests` (`ONEDIFF_RENDER_TEXT_CORPUS`, `ONEDIFF_PDF_GATE_DIR`, `ONEDIFF_RENDER_FROM`, `_COUNT`, `_EVERY`, `_JOBS`, `_OUT`); `tools/run-render-gate.sh` runs them in queued chunks.
