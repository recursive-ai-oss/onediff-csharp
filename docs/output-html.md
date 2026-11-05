# HTML output and the redline entry points

This note describes what `RecursiveAI.OneDiff.Documents` writes as HTML, how a page maps back to the comparison it came from, and how adapter packages plug their inputs and output forms into `Redline.Create` and `Comparison.SaveAs`.

## 1. The page

Both layouts are one self-contained file: UTF-8 without a byte order mark, LF line ends, inline CSS, no scripts, no external fetches (no `src`, no `url()`, no `@import`, no link other than in-page `#` anchors), system font stacks. The same comparison and style give the same bytes on every platform; the clock is never read. A timestamp appears only when `RedlineStyle.Timestamp` is set.

```
<html lang>            RedlineStyle.Language ("en"); the page furniture is English
<head>                 charset, viewport, generator, <title>, <style>
<body class="inline|sbs">
  <header class="cover|top">   <h1>; with CoverPage: file names, settings, hidden categories,
                                completeness line, counts by change type; then the key
  <main aria-label="Redline">  the layout below
  <section class="ledger">     "Differences not shown": counts per category, first 50 locations each
  <section class="notes">      warnings and errors from the stages (only when there are any)
  <footer>                     "Compared by OneDiff x.y.z", input names with SHA-256, timestamp if given
```

**Style as data.** The sheet is generated from the `RedlineStyle`: each mark (insertion, deletion, move from/to, formatting) becomes colour plus a `text-decoration` (`underline`, `underline double`, `line-through`, `line-through double`) or a highlight (the colour mixed 78% with white behind near-black text, and a 2 px bottom rule in the full colour). Glyphs are written into the markup (`<span class="gl" aria-hidden="true">`), so a change never depends on colour alone where the style gives a glyph. Change bars are a left border in the margin (`ChangeBars`, `ChangeBarColor`). `CoverPage`, `Footer`, `TypeTags`, `Title` and `Language` do what their names say. Colours are checked to be `#RRGGBB`; anything else is replaced by black, so a style from an untrusted source cannot reach the sheet.

**Presets.** Blackline (the default): blue double-underlined insertions, red struck deletions, green double-struck/double-underlined moves, bars, cover page, footer. SideBySide: colour-blind-safe blue (#0072B2) and orange (#E69F00) highlights with `+`, `−`, `→`, `←` glyphs; no bars, cover page or footer; the side-by-side form picks it when the settings still hold the default blackline. Word: red underlined insertions and struck deletions, green moves, a bar. Developer: green and red backgrounds with `+`/`-`, purple moves.

**Contrast of the preset colours on white** (WCAG 2.x ratios; 4.5 is AA for body text, 3 for large text and graphics). Blackline: insertion 8.59, deletion 4.00, moves 5.14, formatting 4.00. Word: insertions/deletions/formatting 6.48, moves 2.87. SideBySide and Developer put near-black text (`#1a1a1a`) on the tint, 12.2–14.6, so the mark colours (SideBySide orange 2.25 and grey 2.85 against white) are only the rule under the highlight and the glyph, never the text. Where text itself takes the mark colour and falls below 4.5 (Blackline deletion and formatting, Word moves), the mark is also a decoration and hidden text names it, so the information is not carried by colour; the values are in the style presets, not the writer.

## 2. Inline layout

The new document's tree in order. Each node is a `div.blk.k-<kind>` (a real `table`/`tr`/`td` for tables) with `data-new` (new node id), `data-old` (the linked old node ids, space separated) and `data-link` (the alignment link). Its own text is one `p.t` (`white-space: pre-wrap`), a list label is a `span.lbl` beside it, other kinds get a `div.kl` label (Footnote, Image, …). Children nest inside the block.

Text inside `p.t`:

| Markup | Meaning |
|---|---|
| bare text | unchanged |
| `<ins data-change=N>` | inserted text of change N |
| `<del data-change=N>` | deleted text of change N, in place |
| `<span class="fmt" data-change=N title=…>` | text whose formatting changed (the title says how) |
| `<span class="mv to|from" data-role data-change id=mvt-N|mvf-N>` | the new / old place of a moved block, with an `a.mvlink` to the other place |
| `<span class="sep">¶</span>` inside `del` | a node boundary inside deleted text (a merge) |
| `<span class="pbrk">¶</span>` inside `ins`/`del` | a paragraph break that was inserted/removed (a split/merge at a block boundary) |
| `<span class="ctl" data-cp=hex>` | a control character or unpaired surrogate HTML cannot carry |
| `<span class="sr">`, `<span class="gl">`, `<span class="tag">` | hidden screen-reader label, glyph, type tag: not document text |

Deleted blocks and the old place of moved blocks appear as `div.blk.ghost` (with `data-old`) where they used to be: before the next kept block, and ahead of blocks inserted just before it, so a removed block reads before its replacement. Attribute changes (style, list label, hashes) are `div.attrs > span.ae[data-change data-key data-old data-new]` under the block, shown as a label and unquoted values (`List number: 1.5 → 1.2`, `(none)` for a missing side); the machine-readable key and values stay in the attributes. Hidden content (comments, properties under the Legal settings) is not drawn; it is in the ledger section.

A change that belongs to a link whose other side the settings hide is a ledger entry, not an element.

## 3. Side-by-side layout

`table.sbs`: header cells "Old: name" / "New: name", one row per block in the merged order. A kept block is on both sides; deleted blocks only left, inserted only right, a moved block twice (old place left with `mv from`, new place right with `mv to`). A split or merge is one row with all its blocks. The left cell holds only unchanged and deleted text, the right only unchanged and inserted text; a formatting change shows on both sides. Container blocks with no text and no change get no row. Indentation is the tree depth (`d1`…`d5`).

## 4. How a page reads back

The tests parse every page they write and check, per comparison and per preset and layout:

1. the text of each `p.t` with `ins` removed (and `del` kept) equals the old node's compared text; with `del` removed it equals the new node's — for every visible node, including splits and merges (blocks joined by U+001E) and ghosts;
2. every change is exactly the elements its edits call for: whole-node insert/delete = one `ins`/`del` with the change's text; edits = `ins`/`del`/`fmt` elements whose concatenated text equals the edits' new/old/formatted text (side-by-side drops U+001E); a move = one `from` and one `to`; each attribute edit = one `ae` with its key and values; no element refers to an unknown change;
3. hidden nodes are absent; nothing in the page can run or fetch anything.

"Compared text" is the text under the settings (`ProjectedGroup`): hidden normalizations (typographic quotes, dashes, whitespace under Legal) stay folded and are counted in the ledger section rather than drawn. HTML validity is checked separately with the W3C Nu validator.

## 5. Escaping

All text from documents, names, hashes, ledger details and diagnostics goes through one escaper. Text: `&`, `<`, `>` are entities; characters HTML cannot carry (C0 controls other than tab and LF, DEL and C1 controls, unpaired surrogates, noncharacters) become `span.ctl` with the code point in `data-cp` and a visible control-picture glyph, so reading the page back gives the original characters. Attribute values: `&`, `"`, `<`, `>` are entities and the uncarriable characters become U+FFFD. `Language` must be letters, digits and hyphens or it falls back to `en`.

## 6. Entry points and the registration seam

```csharp
Redline.Create("v1.txt", "v2.txt", "redline.html");                 // form from the extension
Redline.Create("v1.txt", "v2.txt", "side.html", RedlineForm.HtmlSideBySide);
var cmp = Comparison.Of("v1.txt", "v2.txt", CompareSettings.Legal);   // inputs sniffed by content
cmp.SaveAs("redline.html");                                           // settings' style
cmp.SaveAs("redline.json");
cmp.SaveAs("redline.html", RedlineStyle.Developer);
byte[] bytes = cmp.ToBytes(RedlineForm.Html);                         // or cmp.Save(stream, form)
```

`RedlineForm` is a small abstract class (name, extensions, preferred style, `Write`). A form passed explicitly needs no registration. `DocumentSources` and `Redline` hold two process-wide lists, filled by explicit calls and never by discovery:

- `DocumentSources.Register(IDocumentSource, priority = 0)` (or `Redline.Register(source, priority)`): sources consulted before the built-in plain-text one, highest priority first and the newest first among equals; same `Name` replaces. A specialist source (the scan source) registers above the source that would also accept its input, so the order of `Register` calls does not matter.
- `Redline.Register(RedlineForm)`: forms selected by extension; the latest registration of an extension wins; same `Name` replaces.

Both are thread-safe and idempotent. An adapter package exposes one `Register()` that makes those calls. What a user writes:

```csharp
PdfRedline.Register();                               // once at startup; each adapter package has one
Redline.Create("a.pdf", "b.pdf", "out.pdf");
```

Without that call the failure names the package: `No output form is registered for '.pdf'. Reference RecursiveAI.OneDiff.Pdf and call its Register method once at startup.`, and an unreadable PDF input says it looks like a PDF and names the same package. Code that wants no global state uses `CompareDocument.From(input, source)`, `Comparison.Of(oldDocument, newDocument)` and a form's `Write` directly. A module initializer was rejected: it would run only after the adapter assembly is touched, which is exactly what `Redline.Create("a.pdf", …)` does not do, and library module initializers are discouraged for trimming and AOT.

When no style is given: the form's `PreferredStyle` (side-by-side: `SideBySide`) if the settings' style is still the default blackline, otherwise the settings' style.

## 7. Command line

`onediff compare <old> <new> <output> [--form html|html-side-by-side|json] [--settings legal|strict|text] [--style blackline|sidebyside|word|developer] [--title T] [--language tag] [--timestamp ISO-8601] [--no-cover] [--no-footer] [--no-bars] [--type-tags]` over `Redline.Create`.

## 8. Samples

`docs/samples/<preset>-inline.html` and `<preset>-side-by-side.html` for the four presets, written from a short pair of documents made for the purpose (a replaced number, a deleted clause, an inserted clause, a moved clause that renumbers, a formatting change, a typographic difference in the ledger). A test compares them with what the writers produce now; `ONEDIFF_UPDATE_SAMPLES=1` rewrites them.
