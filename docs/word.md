# Word comparison

How `RecursiveAI.OneDiff.Word` reads a Word document into the compare model, and the anchors a tracked-changes writer builds on. Everything stated here is checked by the package's tests.

## 1. Reading a document

`WordDocumentSource` recognizes a Word package by content, never by file name: the OneOffice format sniffer must name DOCX, DOCM, DOTX or DOTM (an OPC package whose main part is a word-processing document). A compound file holding an encrypted package is recognized too, so that reading it says "encrypted" instead of "unsupported". The document is opened through the OneOffice document hub (`Document.Open`); nothing reads the package a second way, except the small core-properties part, which is parsed with the hub's markup layer.

Register the source once with `WordRedline.Register()`; after that `Redline.Create("a.docx", "b.docx", "out.html")` (or `.json`) works, and so does a pair that mixes formats of the flowing kind, such as `a.docx` and `b.pdf` with `PdfRedline.Register()` as well.

### Structure

| Source content | Node |
|---|---|
| A section of the document (it ends at a paragraph that carries section properties; the last one at the body's) | `Section`, with `section.page` (page size, orientation, margins, columns, break type, title page, page numbering, right-to-left) |
| A header or footer the section owns (default, first page, even pages; inherited ones are not repeated) | `Header` / `Footer` under the section, first, with the attribute `hf.type` |
| A paragraph | `Paragraph`; a paragraph with a resolved number is a `ListItem` with `list.label`, `list.level` and `list.format` |
| A table, a row, a cell | `Table` → `TableRow` → `TableCell`; `table.span` (grid span), `table.vmerge` and `table.hmerge` (`restart`, `continue`), `table.header`, `table.gridbefore`/`gridafter`, `table.style` |
| A block content control | `ContentControl` with `sdt.tag`, `sdt.alias`, `sdt.type`, holding its blocks |
| Footnote, endnote | `Footnote` / `Endnote` as a child of the paragraph that holds the reference, holding the note's blocks |
| Comment | `Comment` as a child of the paragraph that holds the reference, holding its blocks; `comment.author`, `comment.initials`, `comment.date`, `comment.reply`, `comment.done` |
| Field (complex or simple) | `Field` as a child of its paragraph: the result is the node's text and the instruction is `field.code` (whitespace collapsed) |
| Inline content control | `ContentControl` as a child of its paragraph, the text it holds as its text |
| Picture | `Image` as a child of its paragraph: `hash.content` (SHA-256 of the image part), `hash.perceptual` when the format is decoded, `image.alt`, `image.extent` (EMU), `image.link` for a linked picture |
| Chart, shape, diagram, ink | `Image` with `image.kind=drawing` and a SHA-256 of its markup as `hash.content` |
| Text box | `TextBox` as a child of its paragraph, holding one `Paragraph` per paragraph of the box (text only) |
| Core and custom document properties | one `Property` per property after the sections (`prop.name`; custom ones are `custom:<name>`) |
| Custom XML, embedded objects, charts, diagrams, macros, ink, glossary parts | `Opaque` parts known by a SHA-256 of their bytes (reported "differs, not itemized") |

The paragraph's text holds U+FFFC where a field, a picture, a text box, a note reference or an inline content control sits; the child node carries the object, in order. A comment leaves no mark in the text, because comments are hidden into the ledger by default and a mark would show as a change; its place is its position among the paragraph's children.

Not in the model: math, bookmarks and range markers, proofing marks, document settings, themes, font tables, fields inside the instruction of another field (their result reads as part of the outer instruction), and the formatting of text inside a text box. A perceptual hash needs a decoder, and only non-interlaced PNG is decoded; every other format carries the content hash alone.

### Text and formatting

Text is what the document shows: `w:t` as written, a tab as `\t`, a line or page break as `\n`, a no-break hyphen as U+2011, a soft hyphen as U+00AD. Deleted and moved-away revision text is not read (see section 2). The text is then normalized as in every source (quotes, dashes, runs of spaces; control character U+001E), and each fold goes to the ledger.

Run formatting is the resolved formatting, with styles, theme and document defaults applied through the hub: `run.bold`, `run.italic`, `run.underline` (the underline kind), `run.strike` (`1` or `double`), `run.font`, `run.size` (points, `10.5` for half points), `run.color` (`RRGGBB`; automatic is absent), `run.highlight`, `run.caps` (`caps` or `small`), `run.vert` (`superscript`, `subscript`), `run.hidden`, and `run.link` (the hyperlink target, `#anchor` for an internal one). Only non-default values are present.

Paragraph attributes are resolved too: `para.style` (the style's name), `para.align` (absent for left), `para.indent` (`l`, `r`, `f`, `h` in twentieths of a point, zero omitted), `para.spacing` (`b`, `a`, `l` and the line rule), `para.keep` (`next`, `lines`, `pagebreak`).

## 2. Input revisions

`WordSourceOptions.Revisions` says what to do with tracked changes already in an input:

| Mode | Effect |
|---|---|
| `Accepted` (default) | Every revision in every part is accepted through the hub before reading: insertions stay, deletions and moved-away text go, property changes keep the current properties |
| `AsOriginal` | Every revision is rejected: the document as it was before anyone changed it |
| `Refuse` | An input that holds any revision is rejected with `WordSourceFailure.HasRevisions`; one without reads normally |

The accepted or rejected tree lives in memory only; the input bytes are never changed.

## 3. Anchors

Anchors are the contract with the writer. Every anchor this source writes is a node anchor (`AnchorKind.Node`) whose reference is a string of this grammar:

```
reference = "docx:" part "#" path [ "@" offset "+" length ]
part      = the package part name, e.g. /word/document.xml, /word/header1.xml, /word/footnotes.xml
path      = element index { "." element index }          (empty: the part's root element)
```

- **An element index** is the position of an element among the *element* children of its parent (text, comments and processing instructions are not counted; `mc:AlternateContent` and everything else are), starting at 0. The path starts at the part's root element, which is not counted: `0.3.12` is the 13th element child of the 4th element child of the first child of the root. For `/word/document.xml` the first step is `w:body`.
- **Part names** come from the package: the main part, the header or footer part, and the footnotes, endnotes or comments part found by relationship.
- **A node anchor** names the element of the node: `w:p` for a paragraph or list item, `w:tbl`, `w:tr`, `w:tc`, `w:sdt` for a block content control; the header, footer, note or comment root element (`w:hdr`, `w:ftr`, `w:footnote`, `w:endnote`, `w:comment`) for those; the section's `w:sectPr` (the body's, for the last section, or the body itself when there is none) for a section; the run that holds `fldChar begin` (or the `w:fldSimple` or `w:sdt`) for a field or inline content control; the `w:drawing`, `w:pict` or `w:object` for an image; the `w:txbxContent` for a text box. A property node names its part with an empty path; an opaque part names its part name.
- **A segment anchor** (the node's `Segments`) names a stretch of the node's text: the `w:r` it came from (for text inside a text box, the `w:p`), then `@offset+length`, where `offset` and `length` are UTF-16 units of the **paragraph's current text** — the text the hub reads for the nearest enclosing `w:p`: tabs and breaks one character each, field results included, field instructions, drawings and note references none. This is the offset space of `RevisionAuthor.InsertText`, `DeleteText`, `ReplaceText` and `FormatText`, so a writer places `w:ins`, `w:del` and `w:rPrChange` without recounting. A U+FFFC placeholder's segment has the object's reference run, the offset where it starts and the length of the object's visible text (for a field, its result; for a picture or a note reference, 0).
- **Folds.** The node text is normalized and a segment's `Start`/`Length` are in normalized text. To place an edit at a normalized position, map it back through `Node.Folds` to a position in `Node.RawText` (the text before normalization, with U+FFFC for objects), find the segment that covers it, and add the distance from the segment's raw start to the segment's `offset`. For a segment of plain text the raw characters equal the stretch `[offset, offset+length)` of the hub's paragraph text; the tests check this for every paragraph without folds.
- **Which tree.** Paths are counted in the tree as the source read it, which is the tree after the revision mode of section 2 was applied. A writer opens the same bytes, applies the same mode (`Accepted`/`AsOriginal` through `Document.Revisions`), and resolves references against that tree before it adds its own revisions. The Word source remembers the bytes and mode of every document it returns (`WordOrigins`, internal) so that a writer can reopen the document from a comparison.
- **Stability.** References are positions, so they are stable for the same bytes and mode and for nothing else; they are never reused across documents. They are not stored for editing: a writer that edits the document must resolve all the references it needs before the first edit, or resolve in reverse document order, because an edit moves later elements.

Resolution is `WordAnchors.TryParse` and `WordAnchors.Follow` (internal, tested): parse the reference, then follow the path from the part's root element. Header, footer, note and comment parts are reached through the hub's stories (`HeaderFooter.Container`, `Note.Container`, `Comment.Container`), whose markup documents hold the roots.

## 4. Limits, errors and determinism

- Malformed, truncated, encrypted, oversize and non-Word input gives `InvalidDataException` with a `WordSourceFailure` (`Malformed`, `Encrypted`, `TooLarge`, `HasRevisions`) readable through `WordSourceErrors.FailureOf`; no other exception type leaves `Read` except `OperationCanceledException`. Corrupted packages are the hub's typed `OfficeException`s, translated.
- `WordSourceOptions` bounds the characters of text (64 million), the nodes (8 million) and an image part's size (256 MiB); the hub's `OfficeLimits` (part sizes, expansion ratio, XML depth) apply underneath. Cancellation is checked per paragraph and per block.
- Encrypted packages are not decrypted by this package (the hub's cryptography package is a separate dependency); they are rejected as `Encrypted`.
- The same bytes give the same document; the same pair gives byte-identical comparison JSON. The model has no clock, random or machine-dependent input.
