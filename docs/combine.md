# Combine, version chains and the change report

Normative description of `Combination`, `ComparisonChain` and `ChangeReport` in `RecursiveAI.OneDiff.Documents` (namespace `OneDiff.Documents.Combine`). The compare model, anchors, alignment and grouping they build on are in `compare-model.md`, `alignment.md` and `grouping.md`.

## 1. Combine

**Input.** One original document and N ≥ 1 reviewers' copies of it. Reviewer *k* is the *k*-th copy; its index is its position in the list. Every copy is compared with the original by the ordinary stages (`Comparison.Of(original, copy_k, settings)`), so each reviewer has a verified, invertible `Comparison` over the **same** `CompareDocument` object. `Combination.From` accepts ready comparisons and requires that they share that object (node ids are then the same everywhere).

**Result.** `Combination` holds the original, the reviewers, their comparisons, the combined changes, the conflicts, diagnostics and a verification record. The combined result never contains a decision between reviewers: nothing is dropped, preferred or merged away unless two reviewers made *the same* change.

### 1.1 Combined changes

A *combined change* stands for one change of one reviewer's comparison or, when several reviewers made the same change, for all of those. It carries the first holder's `Change` (both sides, both anchors, edits), the reviewers, the holders (`reviewer`, `change id` in that reviewer's comparison), the reviewers of each edit, and the conflict it is in (or -1). Combined changes are numbered 0..n-1 in the order of the original: by the first old node, then an insertion after the node it follows, then the position inside the node, then reviewer, then the reviewer's own change number. The order is total, so the output is deterministic.

### 1.2 What a change claims

Every change makes *claims* about the original, in the original's coordinates. A claim is a kind, the old nodes it concerns, a character range (for text), a key and an effect:

| Claim | From | Range / key | Effect |
|---|---|---|---|
| Text | each edit of a change | old nodes of the link, `[OldStart, OldEnd)` in their joined text (a point when the range is empty) | the new text and its formatting runs (`txt`), or the new formatting only (`fmt`) |
| Whole | a node deleted whole | the node | `del` |
| Structure | a split or merge | the old nodes | kind, number of new nodes, new text |
| Attr | each attribute edit | node and attribute key | the new value |
| Moved | a move | the old node; key = destination | `mv` |
| Insert | a node inserted whole | key = place: the old node of the linked parent, the old node of the nearest linked sibling before it, the number of unlinked siblings between, and the path below the top of an inserted subtree | node kind, text, attributes, runs |

A change with no claim by these rules still claims its old nodes (Structure) or, with none, its place (Insert), so no change can pass unnoticed next to another's.

### 1.3 Identical changes are merged

Two reviewers' changes are **the same** when they have the same kind, the same claims (kind, nodes, range, key, effect) and the same new text. They become one combined change attributed to both. Numbers that differ between comparisons (change ids, new-side node ids and offsets) play no part. A reviewer never merges with itself.

**Edit-level attribution.** A reviewer's change groups edits into display units, and two reviewers may group the same edit differently (one changed a neighbouring word too). Such changes are not merged, but each edit is attributed on its own: `EditReviewers[i]` lists every reviewer who made the *same edit* as edit *i* of the shown change — the same place in the original, the same new text and formatting — whether or not their change is the one shown. So a shared edit is never mistaken for a private one, and a combined change is never hidden by another.

### 1.4 Conflicts

Claims of **different reviewers** conflict when they cannot all be applied to the original. Identical claims never conflict. The rules:

| Claims | Conflict when | Kind |
|---|---|---|
| Whole × anything on a shared node | always (a node deleted by one reviewer and edited, moved, restructured or given an attribute by another) | `EditAndDelete` |
| Text × Text, same old nodes | the ranges overlap: they share a character; or a point lies strictly inside the other range; or two points coincide | `Edits` |
| Text × Text, different but intersecting old nodes | always (a merge or split edit against an edit of one of its nodes; coordinates are not comparable) | `Edits` |
| Moved × Moved, same node | different destinations | `Move` |
| Structure × Structure, shared node | different effect | `Structure` |
| Attr × Attr, same node and key | different values | `Attribute` |
| Insert × Insert | the same place, different content | `Insert` |

**Derived claims never conflict.** A list label (`para.label`) that changed, a change of type numbering, and a reference rewritten to follow a renumbering (type reference, not substantive) are consequences of other changes: two reviewers who deleted different clauses renumber the same later clause differently, and merging both deletions gives a third number that neither chose. These changes are attributed and merged like any other and never flagged; the changes that cause them are the ones that conflict, when they do. (The reference rule needs `TypedChanges`; list labels do not.)

A point at the edge of a range does not conflict with it; two edits on different words of one paragraph do not conflict. Text × Structure, Text × Attr, Text × Moved, and a moved node's inner edits against its move do not conflict: they commute.

A *conflict* is a connected component of conflicting combined changes, with at least two changes of at least two reviewers. Its `Kind` is the most serious (lowest in the table above, by enum order) of its pairs; `Reviewers` lists those involved. Conflicts are numbered in the order of their first change. **Every conflicting version is shown; none is preferred.** A change shared by reviewers A and B that overlaps a change of C puts A's, B's and C's versions in one conflict.

### 1.5 Verification

`Combination.Check()` returns failures with codes:

| Code | Check |
|---|---|
| C001, C002 | each reviewer's comparison was made from the combination's original and passes `Comparison.Check` |
| C101–C104 | combined change ids are 0..n-1; each has holders; every holder exists; no reviewer change is attributed twice |
| C105 | every holder's change equals the shown change in kind, old side, edits (place, new text, formatting) and new text |
| C106, C107, C108 | the reviewers equal the holders' reviewers; edit attributions are as many as edits, include every holder, and name only reviewers who made that edit |
| C109 | every change of every reviewer is in some combined change |
| C201–C204 | the stored conflicts equal the conflicts recomputed from the changes (ids, kinds, members, reviewers), each has ≥2 changes of ≥2 reviewers, and each change's conflict id is right |

C101–C104 and C109 together say that the changes attributed to reviewer *k* are exactly the changes of *k*'s comparison, each once. Projecting the combined result onto *k* (`ChangesOf(k)`, each by its holder entry for *k*) therefore gives the very change list of *k*'s verified comparison, so old + changes = *k*'s copy and the copy − changes = old, per reviewer, on every run.

**Fallback.** If a check fails, the combination is rebuilt without merging (every reviewer's change listed on its own, conflicts recomputed) with a `combine.fallback` warning and `combine.C…` error diagnostics for the failures that were recovered from (`Verification.FallbackLevel` 1). If it still fails, `ComparisonVerificationException`. With `CompareSettings.ThrowOnFailure` (strict) the first failure throws. Without the model, nothing here depends on it.

### 1.6 Entry points

`Combination.Of(original, copies, settings, scorer, token)`; `Combination.From(comparisons, token)`; `Redline.Combine(originalPath, copyPaths, outputPath, settings)` (thin: reads the files, `Combination.Of`, `SaveAs`); CLI `onediff combine <original> <copy>... -o <out>`. `Combination.SaveAs` writes the change report (`.html`) or the JSON form (`.json`). Per-reviewer inline redline documents and DOCX with revision authors per reviewer come with the Word writer.

## 2. Version chains

`ComparisonChain.Of(versions, settings, scorer, token)` compares version *i* with *i* + 1 for every step and the first version with the last (the *net* comparison). Step *i*'s new document is step *i* + 1's old document: the same object, so the steps compose. Every step and the net comparison are ordinary verified comparisons; `Check()` re-runs them (H001–H004) and confirms the steps meet and the net runs from the first version to the last.

**Step attribution.** Each change belongs to the step that made it (a step's changes are its own comparison's). For the net view, `StepsOf(netChangeId)` lists the steps that touched a net change. A step touches it when

1. one of its changes concerns a block the net change's old blocks became (followed forward through the steps' alignments), or that the net change's new blocks came from (followed backward);
2. one of those blocks is deleted or created in that step (a block removed with its parent has no change of its own);
3. for a net move: the step moved anything (moving one block changes the relative order of the others, so the net comparison may show a different block as moved);
4. otherwise (the net comparison paired interchangeable blocks, say empty cells, differently from the steps): the steps that made a change of the same kind, and failing that any step that changed anything.

The grain is the block: two edits to one paragraph in different steps are both attributed to both steps in the net view; the step comparisons say exactly which step made which edit.

Entry points: `ComparisonChain.Of`, `Redline.Chain(versionPaths, outputPath, settings)`, CLI `onediff chain <v1> <v2>... -o <out>`.

## 3. Change report

`ChangeReport.From(comparison | combination | chain)` is a plain data object; `ToHtml()` renders it as one self-contained page (inline style, no scripts, no time or environment: the same report gives the same bytes).

- **Counts** by change kind and by change type (only those that occur), the number of substantive changes, and the ledger (hidden differences by category).
- **The substantive list** (`IsSubstantive`): location, kind, the old and new text (shortened to 200 characters), and the resolved values (`Duration 30 d → 60 d`). *Location* is `§` and the list label of the block or its nearest labelled ancestor (`§4.2`), otherwise `¶` and the block's number among paragraphs and list items.
- **Combination:** changes attributed per reviewer, how many were made by one, two, … reviewers, and a section per conflict listing every reviewer's version side by side with the reviewers named.
- **Chain:** a section per step with its counts and substantive list, then the net section, each net change with the steps that touched it.

## 4. JSON forms

Both embed complete `onediff.comparison` documents (see `schema/comparison-v1.json`, unchanged) and add attribution. Schemas: `schema/combination-v1.json`, `schema/chain-v1.json`. Written with `Utf8JsonWriter`; byte-identical for equal inputs.

`onediff.combination` v1: `original` {name, hash}; `reviewers` [{index, name, hash}]; `verification` {passed, fallbackLevel, failureCount}; `changes` [{id, kind, reviewers [], holders [{reviewer, change}], editReviewers [[…]] (omitted without edits), conflict (omitted when none)}]; `conflicts` [{id, kind (`editAndDelete`, `edits`, `move`, `structure`, `attribute`, `insert`), changes [], reviewers []}]; `diagnostics`; `comparisons` [full comparison per reviewer, by index]. A combined change's content is the holder's change in `comparisons[reviewer].changes[change]`.

`onediff.chain` v1: `versions` [{name, hash}]; `steps` [{index, from, to, comparison}]; `net` {comparison, changeSteps [{change, steps []}]}; `diagnostics`.

The existing `onediff.comparison` document is unchanged. There is no per-change step or reviewer member in it, because a comparison has one old and one new side: attribution belongs to the document that holds several of them.

## 5. Limits and cost

Claims are grouped by old node and by insertion place; pairs are compared only inside a group, so cost is quadratic only in the edits to one node. The combination's own work is a small fraction of the N comparisons it is built on (see `measurement.md`). Memory is linear in the changes. Cancellation is observed between comparisons.
