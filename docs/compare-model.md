# The compare model, the stages and the completeness guarantee

This note describes the contracts of `RecursiveAI.OneDiff.Documents`: what a compared document is, how a comparison is built from five stages, and how every comparison is proved complete. Sources, alignment, in-pair diff, grouping, writers and models plug in through the types named here.

## 1. The guarantee

For every comparison:

1. **Every difference is accounted for.** Each difference between the two compare models is either a *change* or an entry in the *ledger* (hidden by a setting: category, count, locations). Content outside the model is reported "differs, not itemized" from a content hash.
2. **Invertible.** Old with the changes applied is new; new with the changes removed is old. This is checked by replaying the changes' edits over the compared content in both directions, not by trusting the stage that made them.
3. **Both sides, both anchors.** Every change carries the old and new text it concerns and an anchor for each side that resolves back to the source.
4. **Grouping is display.** Changes are atomic data. A `ChangeGroup` lists change ids and carries no data, so removing or reshaping groups never loses or alters a change.

`Stages.Verify` checks all four on every comparison. On failure it falls back, step by step, to a coarser grouping that is still complete, and records why in the diagnostics; with `CompareSettings.ThrowOnFailure` it throws `ComparisonVerificationException` instead. Without any model the deterministic stages keep every promise; a model may reorder, group and score but cannot hide a change, because whatever it produces is verified like everything else.

## 2. The compare model

A `CompareDocument` is an immutable forest of `Node`s in document (pre-order) order. `Nodes[i].Id == i`, and a parent always has a smaller id than its children, so a node's id is its position. A document also has a display `Name`, the `SourceHash` of the input bytes, a `SourceFormat`, and `Opaque` parts.

A `Node` has:

| Member | Meaning |
|---|---|
| `Kind` | `Section`, `Paragraph`, `ListItem`, `Table`, `TableRow`, `TableCell`, `Footnote`, `Endnote`, `Header`, `Footer`, `Comment`, `Field`, `Image`, `TextBox`, `ContentControl`, `Property` |
| `Text` | normalized text the node's own content is compared on (empty for pure containers); never contains U+001E |
| `Runs` | character formatting over `Text`: sorted, non-overlapping, adjacent equal runs merged, never an empty format |
| `Attributes` | block-level attributes as a sorted string map (paragraph style, list label and level, hashes, field code, property name) |
| `Anchor`, `Segments` | where the node came from; segments anchor pieces of the text (a line, a word) |
| `Folds` | what normalization changed (below) |

Conventions for the flowing kind:

- **List items** carry their resolved label in `list.label` (and `list.level`, `list.format`), so a renumbering is an attribute change, not a text change.
- **Fields** are `Field` nodes under their paragraph: the code is `field.code`, the result is the node's text. A source that wants the field to appear in the paragraph's text puts U+FFFC there; the child node holds the content.
- **Hyperlinks** are the `run.link` attribute of the linked text.
- **Images** are `Image` nodes with `hash.content` and, when a source has one, `hash.perceptual`.
- **Comments** and **document properties** are nodes (`Comment`, with block children; `Property`, named by `prop.name`, the value is the text). They can be hidden into the ledger.
- Attribute keys have a prefix that says which hiding category they belong to: `run.` (character formatting), `para.`, `list.`, `section.`, `field.`. Other prefixes (`hash.`, `table.`, `prop.`, …) are content or structure and are never hidden.

**Slides and grids** use the same `Node` shape. Adding them means adding `NodeKind` values (slide, shape, sheet, row, column, cell), keeping geometry, formulas (as R1C1) and number formats in `Attributes`, using `Anchor.ForCell` for cells, and choosing the matching `DocumentKind`. `Stages.Align` refuses to compare documents of different kinds. No new stage contract is needed.

### Anchors

An `Anchor` is a source location the engine copies but never interprets. The anchor kinds are `Node` (an opaque adapter reference, for example a hub node), `PdfQuads` (glyph quadrilaterals with their pages), `OcrWords` (word quadrilaterals plus the lowest confidence), `Cell` (sheet and A1 address) and `TextSpan` (a character range of a named text part). `.Documents` references no source library; adapters choose the `Reference` string. Anchors are the contract between sources and writers: a source attaches them, the engine copies them onto changes, a writer resolves them.

`Anchor.Union` combines neighbouring anchors (hull of text spans, concatenated quads with the lowest OCR confidence, otherwise the first). A change side's anchor is the union of the anchors of the node segments its range overlaps, or the node's anchor when it has no segments. Verification recomputes that and compares.

### Normalization and the ledger

A normalizer (`ITextNormalizer`) looks at one position of raw text and may replace the characters there. `TextNormalization.Normalize` runs them and returns the normalized text plus a `NormalizationFold` per replacement: its ledger category, its place in the normalized text, and the original characters. A node keeps both, so `Node.RawText` always reconstructs the source text. The reference `TypographicNormalizer` folds quotes, the dash family and runs of horizontal whitespace; PDF sources add `LineBreaks`, `Hyphenation` and `PageNumbers` folds in the same way.

What the settings hide is a **projection** of the model:

- A fold whose category is hidden stays folded; a fold whose category is *not* hidden is undone, so its difference shows as ordinary text.
- Run and block attributes whose category is hidden are removed from the compared content. `Comment` and `Property` subtrees are hidden whole.
- The ledger then lists every difference that exists in the full model but not in the compared projection, outside the regions a change already shows: each place where a hidden fold, hidden format run or hidden attribute differs between paired nodes, each hidden node added, removed or changed, and each opaque part that differs. Differences inside a region a change shows belong to that change.

`LedgerBuilder` computes the ledger from the alignment, the edits and the settings only, so verification recomputes it and fails when an entry is missing.

## 3. The alignment

An `Alignment` says which nodes correspond. It holds both documents, the settings, and `AlignmentLink`s: `Match` (one old node is one new node in the same place), `Move` (same node elsewhere), `Split` (one old, several new) and `Merge` (several old, one new). A node is in at most one link. Nodes in no link are deleted (old) or inserted (new). A link joins nodes of one `NodeKind`.

Edit it with `Pin(old, new)`, `Force(oldNodes, newNodes, kind)` and `Unpin(linkId)`; each returns a corrected copy, normalized. Run `Stages.Align(old, new, seed: alignment)` to keep pinned links and re-pair the rest, or just run `DiffPairs` onwards on the copy.

**Normalization of links** (`Alignment.Normalize`) decides which one-to-one links are `Move`. A link is a `Match` only if its old parent is paired with its new parent and it belongs to the longest run of siblings that kept their order; every other one-to-one link is a `Move`. Hidden-subtree links and splits and merges are exempt. Verification checks the same two rules on every `Match`, so a reorder can never hide: it has to be a visible `Move` change.

## 4. The stages

All stages are pure functions of immutable values, thread-safe, and take a `CancellationToken`; the cost cap (`CompareSettings.CostCap`) travels with the settings in the alignment. Exceeding a cap never throws: the stage returns a coarser, still complete result and adds a `Warning` diagnostic.

```csharp
Alignment    Stages.Align(old, new, scorer?, settings?, seed?, ct)
PairDiffs    Stages.DiffPairs(alignment, ct)
ChangeSet    Stages.Group(pairDiffs, ct)
ChangeSet    Stages.Classify(changeSet, ct)
Comparison   Stages.Verify(changeSet, ct)
Comparison.Of(old, new, settings?, scorer?, ct)   // the five in a row
```

| Stage | Reference implementation | Contract the next stage relies on |
|---|---|---|
| `Align` | the text core over subtree hashes of all nodes in document order (equal text, tables and sections in order; repeats pair in order), equal subtrees that moved, then text blocks: candidates from a shingle index, splits and merges from containment, ambiguous pairs judged by `IPairScorer` in batches, a global maximum-weight assignment weighing similarity, same changed stretch, matched neighbours and position; then containers (tables, rows, cells, sections, notes) by matched content and by position between matched siblings; then `Normalize` | links satisfy the rules above |
| `DiffPairs` | per link, word-level text-core diff of the joined compared text; format-only edits for equal text with different runs; attribute edits; the ledger | for every non-hidden link, edits that replay old → new and back; `Link i` is indexed by link id |
| `Group` | one `Change` per diff region, one per whole inserted/deleted node, one `Move` per moved link, one `Split`/`Merge` per such link, one `Attribute` per link with attribute edits; one display group per change | every inserted/deleted node and every link is explained; ids unique; groups partition the changes |
| `Classify` | `Decision(Rule, "reference", 1.0)` on every change, no typing | none beyond `Group` |
| `Verify` | the checks in §5, fallback, `Comparison` | — |

Seams: `IPairScorer` (`Score`, `ScoreBatch`) for alignment candidates, `ITextNormalizer` for sources, `IDocumentSource` for inputs. A scorer can only reweigh pairings; every pairing is verified afterwards. Recognizers (typed values) will join as a seam on `Classify`; `Change.Properties` is the slot for their results and `Change.Decision` records rule, model or user with a score.

### Change shapes

- **Whole node** inserted or deleted: `LinkId = -1`, one side present, no edits.
- **Region** (Insert, Delete, Replace, Format) inside a link: one or more `RegionEdit`s. Coordinates are in the *joined compared text* of the link's nodes (one node: its compared text; a split or merge: the nodes joined with U+001E). An insertion has a zero-length old range, a deletion a zero-length new range, a format change equal old and new text with different runs. `OldRuns`/`NewRuns` carry the formatting of the replaced and replacing characters relative to the edit.
- **Move**: no edits; both sides cover the whole node. Edits to a moved node are separate changes with the same `LinkId`.
- **Split/Merge**: one change for the link carrying all of its edits.
- **Attribute**: `AttributeEdit`s per (old node, new node) pair; absent values are `null`.

## 5. Verification

`Verifier` takes only an alignment, changes, groups and a ledger, and checks:

| Code | Check |
|---|---|
| V100–V104 | change ids unique and non-negative; groups unique; groups list only known changes; every change in exactly one group; sides and edit lists present |
| V105–V113 | a change attached to no link is a whole-node insert/delete of an unmatched node with no edits; link ids valid; the change names the link's nodes; sides have well-formed ranges, the text of that range, and an anchor equal to the one the model resolves; single-edit changes cover their edit's ranges |
| V200 | every visible old node is in exactly one link or deleted exactly once; every visible new node likewise inserted |
| V300–V304 | hidden links have no changes; a `Move`/`Split`/`Merge` link has exactly one change of that kind; the link's edits replay old → new **and** new → old over text and formatting; attribute edits replay both ways |
| V400–V401 | every `Match` keeps its parent and its sibling order |
| V500 | the ledger lists every hidden difference |

**Fallback** (when not strict), each step checked again: **1** regroup with the reference stages on the same alignment (drops a bad grouping or a bad in-pair diff); **2** replace every changed block as a whole (drops a bad word diff); **3** nothing aligned, so every block is deleted and inserted (drops a bad alignment). `VerificationResult.FallbackLevel` records the level, and the failures that triggered it are `Error` diagnostics (`verify.V302`, …) followed by a `verify.fallback` warning. The fallback rate is 0 on the reference pipeline's gate sets; any non-zero rate is a defect in the stage that produced the failing result.

## 6. JSON

`ComparisonJson.Write` / `Read`; schema version 1 in `docs/schema/comparison-v1.json`. The output is compact UTF-8, keys in a fixed order, empty optional members omitted, non-ASCII escaped, so equal comparisons give equal bytes on every platform; the reader is bounded (`ComparisonJsonLimits`: input bytes, array length, depth) and throws only `ComparisonFormatException`. Reading validates structure but does not re-prove completeness: call `Comparison.Check()`.

## 7. Plain text

`PlainTextSource` reads UTF-8/UTF-16 text (by byte order mark, otherwise UTF-8) into one `Section` holding a `Paragraph` per run of non-blank lines; the lines are kept, joined by LF. It normalizes with the typographic normalizer and gives each paragraph a `TextSpan` anchor into the decoded text and a segment per line. Input containing a zero byte (and no UTF-16 mark) is not text. Sources are chosen by content (`DocumentSources.Sniff`), never by file name.

## 8. What a follow-on unit needs to know

- **Alignment:** described in `alignment.md`. A replacement stage returns a normalized `Alignment`; a scorer plugs in through `IPairScorer`.
- **In-pair diff and grouping:** produce `LinkDiff`s with replayable `RegionEdit`s; group by emitting `Change`s whose edits are a partition of the link's edits, and `ChangeGroup`s over their ids. Never put data in a group.
- **Ledger:** extend `LedgerBuilder` for new categories (page numbers, hyphenation); `Verify` recomputes it from edits.
- **Writers:** read `Comparison`; resolve `Anchor`s; use `Change.Old/New` for text and `Groups` for display.
- **Sources:** build with `CompareDocumentBuilder`, normalize with `TextNormalization`, attach anchors and segments, set `SourceHash`; implement `IDocumentSource.CanRead` by content.
