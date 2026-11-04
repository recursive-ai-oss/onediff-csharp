# In-pair diff and grouping

How the edits inside a pair of aligned blocks are found, and how they become the changes a reader sees. Everything here is deterministic: the same inputs and settings give the same changes in the same order.

## 1. The in-pair diff (`Stages.DiffPairs`)

For every link of the alignment (hidden links are skipped), the **joined compared text** of each side is compared. A split or merge joins its nodes with U+001E; a link of one node on each side uses just the node's compared text. Equal text costs nothing.

1. **Words.** Each text is cut at UAX #29 word boundaries (section 4, no tailoring; OneFont's `TextSegmentation`). Segments include white space and punctuation, so they partition the text and every edit maps to an exact character range. Han ideographs and kana are one segment per character (UAX #29 has no dictionary); Thai, Lao, Khmer and Myanmar runs are one segment per run of letters.
2. **Script.** The configured algorithm (`CompareSettings.Algorithm`, Myers by default) diffs the segment sequences. If the number of segment pairs exceeds `CompareSettings.CostCap`, the link falls back to one replacement between the common start and end and a `diffpairs.cap` warning says so.
3. **Cleanup.** The text core's semantic cleanup runs over the script (`DiffCleanup.Semantic`): an equal stretch no longer than the edits on both sides of it is absorbed, so a stray "the" does not cut one rewritten phrase in two, and a lone insertion or deletion slides to a word edge. Cleanup never changes what the script turns into what; the new text and the old text stay exactly reproducible.
4. **Refinement.** A region that replaced one segment by one segment and holds digits on both sides ("1,250,000" → "1,520,000"), and a region in a script without spaces between words on both sides, is diffed again by grapheme clusters, and the refinement is kept if something is shared and it falls into at most three pieces. Other replaced words stay whole: "Parties" replaced by "Party" is one edit, not "ies" by "y".
5. **Formatting.** Text that is equal on both sides can still differ in formatting. The text is cut wherever either side's formatting changes, and each piece is labelled with exactly what differs (`run.bold` off to on, `run.font` A to B). Neighbouring pieces with the same label are one edit. An edit whose old and new text are equal is a **format-only edit**; its `OldRuns` and `NewRuns` carry the formatting on each side. Formatting categories the settings hide never reach this stage; they are in the ledger.

The result is a `LinkDiff` per link: ordered, non-overlapping `RegionEdit`s in the coordinates of the joined text, which replay old → new and new → old over text and formatting, plus the attribute edits of the nodes. Verification replays them.

## 2. Change units (`Stages.Group`)

The edits are **atomic data** and stay atomic. Grouping chooses **change units**: each unit is one `Change` holding one or more of the link's edits. The units of a link partition its edits, and a unit's sides are ranges of the joined text that **contain** all its edits (a side may show more than its edits, never less). Whole-node insertions and deletions, moves, splits, merges and attribute changes are made as before. `ChangeGroup`s are display regions over change ids and hold no data: one per change, except that a moved node and everything inside it (its attribute changes and text edits) form one region, and so do the cells of one table row that were inserted or deleted whole.

Rules, in order, per ordinary link:

1. **Values.** Each text edit is widened to cover every value it touches on each side: a date (`March 3, 2025`, `2026-01-31`, `3/4/2025`, `2025年3月4日`), an amount (`$1,250,000`, `USD 5,000`, `5,000 euros`, with million/billion), a percentage (`12.5%`, `12 percent`), a duration (`30 days`, `10 business days`, `三十日`, `30日`), or a plain number. So "30 days" becoming "60 days" is one change reading `30 days → 60 days`, while its edit is the digit. The patterns are simple and deterministic; they are not recognizers (what a value means is decided later).
2. **Overlap.** Units that meet or overlap are joined. A formatting edit that overlaps a unit becomes part of that unit; this can make a unit reach its neighbour, so joining and taking in repeat until nothing changes.
3. **Breaks.** Units never join across a **sentence end** (UAX #29 section 5, on either side's text) or a **clause break**: `;` or `:` followed by white space, or a line break. A unit may still contain an edit that itself crosses a boundary.
4. **Density, per clause** (the run of units between two breaks). With changed words counted on both sides (a word is two half-words; each ideograph, kana or Thai character is one):
   - at least two units, and at least **a quarter** of the clause's words changed: all the clause's edits become one unit widened to the whole clause on both sides;
   - at least three units, and at least **12%** changed: likewise;
   - otherwise units join only when the unchanged text between them is **two words or less**.
5. **Formatting units.** Format-only edits that no text unit took in become format changes (`ChangeKind.Format`): one per edit, and neighbours with the **same label** join across white space alone, if no text unit lies between.

A unit's kind is `Format` when all its edits are format-only, otherwise `Insert` when its old side is empty, `Delete` when its new side is empty, else `Replace`. Units are ordered by where they start in the new text. The thresholds are agent defaults, set on the synthetic gold set and open to change on evidence.

What is deliberately not here: cascades (renumbering and cross-references), defined-term renames, typed values with resolved meaning, and substantive filtering. Each can take units, or join them under a `ChangeGroup` of its own kind, without changing an edit.

## 3. Why the units are not merged edits

Merging scattered edits into one big replace edit would be simpler, but it would turn unchanged words into deleted and inserted ones, and a writer that wants the finer view (words struck inside a rewritten sentence) could no longer have it. Keeping the atomic edits in the unit gives both: a writer that shows units reads `Change.Old` / `Change.New`; a writer that shows detail reads `Change.Edits`.

## 4. Verification

`Verify` checks every claim here without trusting this code: each edit replays, each change names its link's nodes, its sides' texts and anchors are the source's, its edits lie inside its sides, and every change is in exactly one group. If a check fails, the engine falls back to the reference stages on the same alignment (one change per edit), then to whole-block replacement.
