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

## 5. Tracked-changes output

`DocxRedline` writes a `Comparison` as a DOCX with real tracked changes (ECMA-376 Part 1 §17.13). `WordRedline.Register()` makes `Redline.Create("old.docx", "new.docx", "redline.docx")` and `comparison.SaveAs("redline.docx")` use it; `DocxRedline.Create` also returns what could not be carried.

### Method

- **Built on one document.** The output is the new document (default, `WordBuildOn.New`) or the old one (`WordBuildOn.Old`), reopened from the bytes it was read from, so its package, styles, numbering, headers, footers and anything the model does not itemize are the base document's. Everything the other (donor) document has is copied in as the opposite revision: deleted content when built on the new document, inserted content when built on the old. Accept-all gives the new document and reject-all the old, whichever is the base.
- **Revisions are made with OneOffice's `RevisionAuthor`** (insert, delete, move markers, property changes) and finished with edits to its markup tree, never with a parallel DOCX layer. The author is `DocxRedline.Author` (default `OneDiff`). No date is written unless `DocxRedline.Date` is set. Revision ids are numbered 1, 2, … in document order after everything is written, so equal inputs give equal bytes (checked: two runs, and x64 against arm64, see below).
- **Inputs.** Both documents of the comparison must have been read by `WordDocumentSource` in this process, because the writer needs their bytes and anchors (`InvalidOperationException` otherwise).

### What maps to which revision

| Difference | Revision |
|---|---|
| Inserted, deleted or replaced text inside a paragraph | `w:ins` / `w:del` around the runs (`w:delText` for deleted text); runs keep their properties and their hyperlink or smart-tag wrapper |
| Inserted or deleted paragraph, list item | the content and the paragraph mark (`w:pPr/w:rPr/w:ins` or `w:del`) |
| Inserted or deleted table, row, cell | `w:ins` / `w:del` in `w:trPr`, `w:cellIns` / `w:cellDel` in `w:tcPr`, and the paragraphs inside |
| Moved paragraph (also moved and edited) | `w:moveFrom` at one place and `w:moveTo` at the other, with `w:moveFromRangeStart/End` and `w:moveToRangeStart/End` sharing a name; text edits of a moved-and-edited paragraph are `w:ins`/`w:del` inside the `w:moveTo` copy of the base |
| Split and merge | the inserted or deleted paragraph marks of the two paragraphs |
| Paragraph, table, row, cell and section properties | `w:pPrChange`, `w:tblPrChange`, `w:trPrChange`, `w:tcPrChange`, `w:sectPrChange`, holding the properties reject-all restores |
| A paragraph whose objects (fields, pictures, notes, text boxes, content controls) or run formatting differ | the whole paragraph is replaced: the other document's paragraph as a deletion (insertion) next to the base paragraph as an insertion (deletion) |

Run-level formatting changes (`w:rPrChange`) are not written yet; a paragraph with a format-only difference is carried as a replacement, which is complete (accept and reject are exact) but coarser than Word would write.

### What is not carried (`NotCarried`)

A revision cannot express these differences, so they are listed in `DocxRedlineResult.NotCarried` (each entry is also a change or ledger entry of the comparison):

| Category | Why |
|---|---|
| `properties`, `comments` | document properties and comments are not revisable; a comment anchored in content that exists on one side only is dropped from the copy |
| `header-footer` | a header or footer that exists on one side only cannot be added or removed as a tracked change |
| `section` | a section break cannot be tracked; the blocks of a section that exists on one side only are |
| `content-control` | a block content control that exists on one side only, and changes of content-control properties; the control's content is tracked, its wrapper cannot be |
| `paragraph-mark-accept`, `paragraph-mark-reject` | a paragraph mark that has to go on accept or reject cannot when the paragraph is last in its container, comes before a table or block content control, or holds a section break: Word and OneOffice keep such a mark, which leaves an emptied paragraph. The writer moves the mark to the paragraph before it when that paragraph can take it (as Word does when Enter is pressed at the end of a paragraph) and merges an inserted and a deleted paragraph at the end of a container into one; this is the note when neither is possible |
| `unplaced`, `import` | a copied node or relationship that could not be placed (not seen in the gates) |

### Decisions

- The schema requires a date on `w:moveFromRangeStart` and `w:moveToRangeStart` (CT_MoveBookmark), so when no date was asked for these two markers carry `1970-01-01T00:00:00Z`; every other revision stays undated.
- A moved paragraph gets no `w:pPrChange`: it exists twice, each with its own properties, and a paragraph-mark revision beside a property change is not resolved by OneOffice on reject (see "OneOffice gaps").
- Two packages number their lists independently, so the same number can mean different labels. A change of paragraph properties compares a list (`w:numPr`) by what its number stands for in its own package (the definition without its ids) as well as by its XML; the record of the other version carries the other package's definition across (as for any copied list).
- A block content control that only the donor has is not copied, so what stood inside it is copied; what follows the control in the donor is placed after the last of those copies, not before them.
- Text of the other document is placed at paragraph level, outside hyperlinks and inline content controls and after the object runs that precede it, because a revision inside them would take the wrong wrapper with it.

### Checks

- **Accept/reject gate** (`TrackedGate`): OneOffice's `AcceptAll` / `RejectAll` on the output, the result read back with `WordDocumentSource` and compared with the new (old) side under the comparison's own settings; the comparison must have no change. Differences of the categories above are let through only as the table says; every other change is a failure.
  - Property, comment, header, footer and section nodes, and the blocks inside a block content control or section that exists on one side only, are not compared (their wrapper is not carried).
  - As many empty-paragraph differences (insert or delete of an empty paragraph, an empty paragraph that differs only in its properties, list numbers shifted by it) are allowed as the writer listed `paragraph-mark-*` entries for that resolution. A missing empty paragraph, or more than listed, fails.
  - When the writer listed a `section` or block `content-control` entry, a moved paragraph without edits is allowed; an insert and a delete of the same text (or of the same object in a paragraph with the same text) count together as a move across the break; two fields that trade results across a section break are allowed.
  - Empty `w:hyperlink` and `w:smartTag` wrappers left after resolving are dropped before reading back (OneOffice leaves them; they hold nothing).
- **Schema**: the Open XML SDK validator (Office 2019) over every output. Errors the two inputs also have (same rule and element) are not counted against the output.
- **Determinism**: two runs give identical bytes; the digest of 20 seeded pairs built on either document is the same on x64 and arm64.

### Sides that are not Word files

`DocxRedline` also writes a comparison in which one or both documents were read from something else: a PDF (`PdfDocumentSource`), a scan, plain text, any source of the flowing kind. The tracked-changes writer needs a package to build on, so such a side is first **reconstructed** as a DOCX from its compare model (`Reconstruction`, internal), and the revisions are written between the two packages exactly as for two Word files. The comparison is the one that was verified; nothing is compared again.

- **What is written.** Paragraphs; list items with the label the model resolved (each distinct label is a list definition of its own whose level text is the label, so that the label reads back unchanged); tables (rows of cells, one paragraph or more in each cell); one default header and one default footer (the first header and the first footer of the model); the character formatting the model carries (`run.bold`, `run.italic`, `run.underline`, `run.strike`, `run.font`, `run.size`, `run.color`, `run.highlight`, `run.caps`, `run.vert`, `run.hidden`). Nothing else is invented: no page layout, no paragraph formatting, no styles, no pictures, notes, fields, comments, text boxes or sections.
- **Text.** The text written is the text the comparison worked on: the node's text with every fold whose ledger category the comparison's settings do not hide undone. Under `Legal` that is the normalized text, so a PDF paragraph is one reflowable paragraph; under `Strict` the line breaks and hyphens of the PDF are written (a line break as `w:br`), because those are then differences like any other and the offsets of the comparison's edits are in that text. A reconstruction therefore depends on the settings.
- **Labelled.** The core properties say so (`description` and `keywords` hold "reconstructed"), and, unless `DocxRedline.ReconstructionNote` is false, the last paragraph of the default header is a visible note ("Reconstructed by OneDiff from a document that is not a Word file: ..."). A Word file read by `WordDocumentSource` is never touched.
- **On the other side's package.** When exactly one side is a Word file, the other side is reconstructed on that file's package: its styles, numbering definitions, theme, document defaults and page setup are kept, its body and its header and footer references other than the default ones are removed, and the reconstructed body is written into it. Content copied between the two packages then looks the same in both (without this, a paragraph copied from the Word file takes the look of the other package's styles). When both sides are reconstructed, each is built on a blank package.
- **Which side is the base.** `WordBuildOn.New` (the default) builds on the new document: reconstructed when it is not a Word file, with the old document (real or reconstructed) as the donor of the deleted content. `WordBuildOn.Old` builds on the old one. A pair of a PDF and a Word file is therefore written in the new document's form by default, as the output is "the new document with the old shown as deleted"; the output is a `.docx` only when asked for (the extension or the form).
- **Anchors.** The model keeps its node ids (the alignment and the changes are keyed on them). The package is read back through the Word source, and each node of the model takes the text, folds, segments and anchor of the node read back (docs/pdf.md anchors are not carried: they point into the PDF). The text of every paragraph read back must equal the model's text under the settings, and a mismatch is an `InvalidOperationException` that names the node. A model with children of paragraphs (fields, pictures, notes, text boxes) is `NotSupportedException`: the reconstruction does not write them.
- **Not carried.** More than one header or footer in the model (a PDF whose running text changes between pages) cannot be held by one section's default header and footer: the others get no anchor and are listed as `header-footer` or `unplaced`. The section node of a PDF's furniture section is listed as `section`. The block content control, comment and paragraph-mark entries of the table above apply as for any pair.
- **Checks** (tests and tools/run-pdfdocx-gate.sh): the text and character formatting of each reconstructed paragraph read back equal the model's under the settings; accept-all and reject-all give the reconstructed (or real) new and old documents, read back through the Word source and compared under the comparison's settings (the table in "Checks" above); schema clean; two runs byte-identical.

### OneOffice gaps met (workarounds in the writer)

- Accepting or rejecting moves the content of a paragraph whose mark goes by serialized fragments, which can only be read back when the prefixes they use (`r:` for a hyperlink) are declared on the part's root; when they are not, the join fails silently and the emptied paragraph stays. The writer declares the well-known prefixes on every part root it saves.
- A paragraph whose pPr holds both a paragraph-mark revision and a `w:pPrChange` is not joined on reject (the property restore replaces the element the mark revision points to). The writer never produces that pair.
- A resolved revision leaves the empty wrapper of a hyperlink, smart tag, simple field or content control behind, and a paragraph that joins the next carries it along.
- There is no way to mark existing runs as inserted or deleted without going through delete text: the writer authors a deletion under a marker author and renames it (`w:del` to `w:ins`, `w:delText` to `w:t`).
- The notes part hands out an id a copied note already took; the writer chooses ids from what the part holds.
