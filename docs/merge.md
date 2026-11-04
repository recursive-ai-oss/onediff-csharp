# Three-way merge

`ThreeWayMerge` (and `Diff.Merge`) merge a common ancestor (base) with two descendants (ours, theirs) over any sequence; the text form works on lines. The structure follows Khanna, Kuber and Pierce, "A Formal Investigation of Diff3" (FSTTCS 2007); the conflict layouts follow git's documentation for `git merge-file` and `git merge` (conflict styles). Where those leave behaviour open, it was settled by running `git merge-file` 2.51.2 on crafted inputs and the 2,599 triples of the merge corpus, and is marked *observed* below.

## Lines

A line ends at `\n` and keeps its ending. A lone `\r` is not a line break. Two lines are equal only if their endings are too (`a` and `a\n` differ, `a\r\n` and `a\n` differ). The last line may have no ending.

## Aligning

Each side is aligned with the base by the chosen algorithm (Myers by default). The alignment is turned into hunks: maximal runs of changed items, each a range of the base and the range of the side that replaces it. Observed: a run of changed items that could sit in several places is placed by sliding it up as far as it goes (joining runs it meets) and then down as far as it goes, settling at the last place where the other sequence also has a run (so a deletion and an insertion that belong together read as one replacement), or at the lowest place if there is none.

## Walking the hunks

Both hunk lists are walked together in base order.

- A hunk that has no counterpart touching it is **one-sided**: ours-only or theirs-only; the merge takes that side.
- An ours hunk and a theirs hunk with the same base range and the same new items are one **false conflict** (`BothSame`): taken once.
- Anything else is a **conflict**. Observed: hunks that merely touch (one ends where the other starts, or both insert at one point) conflict. A conflict covers every hunk of either side that overlaps or touches it, repeatedly, so its base range is the union and its ours and theirs ranges are what each side holds there.
- Observed: a conflict whose two sides end up equal after different splits into hunks is still a conflict in the diff3 and zdiff3 styles. In the merge style, narrowing (below) finds nothing left to mark and it disappears.

## Conflict styles

- **merge.** Each conflict is narrowed by aligning its ours side with its theirs side: shared items stay out and each differing stretch is a conflict of its own. Then neighbouring conflicts are joined (observed): two conflicts with only shared, unchanged items between them become one when the gap is at most three items, or when no item of the gap holds an ASCII letter or digit. A gap that holds a one-sided change does not qualify; a false conflict settled earlier counts as shared text. Layout: ours section, `=======`, theirs section.
- **diff3.** No narrowing and no joining. Layout: ours, `|||||||` with the base, `=======`, theirs.
- **zdiff3.** diff3, but items that begin both sides identically move out before the conflict and items that end both sides identically move out after it (observed: the start is taken first, the end from what is left). The base section stays whole. A conflict can end up with empty ours and theirs sections.

Markers are `<<<<<<<`, `|||||||`, `=======`, `>>>>>>>` (marker size adjustable) followed by a space and the label when there is one. A section whose last line has no ending gets one inside a conflict, so the next marker starts a line.

## Line endings in markers

Observed: marker lines (and the ending added to an unterminated last line) use CR LF exactly when, for the conflict: the base line before it (or the first base line when it starts the file) ends in CR LF, and so does the corresponding line of ours and of theirs where there is one. An empty base never asks for CR LF. For a conflict that was narrowed or joined, the base line is the one before where the whole conflict began.

## Resolutions

`Ours`, `Theirs` and `Union` settle conflicts after narrowing, joining and trimming (so `Union` on a narrowed conflict gives each narrowed piece's two sides, not the whole conflict twice). `Union` writes ours first and adds a line ending after an unterminated last line of ours. The conflict count is then zero.

## Structured result

`Regions` tile the base, ours and theirs: each region holds the three ranges. Narrowed or trimmed conflicts share the conflict's base range: the first conflicting piece carries all of it, and shared items around the pieces are `BothSame` regions with an empty base range.

## Known difference from git

The remaining differences on the merge corpus come from which of several equally short alignments the diff picks. git's picks differ from ours on some inputs, e.g. base `M V M` against ours `V M M M`: git matches the `V`, our Myers matches the first `M`. They show up as different conflict extents, not as different rules.
