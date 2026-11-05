# Three-way merge

`ThreeWayMerge` (and `Diff.Merge`) merge a common ancestor (base) with two descendants (ours, theirs) over any sequence; the text form works on lines. The structure follows Khanna, Kuber and Pierce, "A Formal Investigation of Diff3" (FSTTCS 2007); the conflict layouts follow git's documentation for `git merge-file` and `git merge` (conflict styles). Where those leave behaviour open, it was settled by running `git merge-file` 2.51.2 on crafted inputs and the 2,599 triples of the merge corpus, and is marked *observed* below.

## Lines

A line ends at `\n` and keeps its ending. A lone `\r` is not a line break. Two lines are equal only if their endings are too (`a` and `a\n` differ, `a\r\n` and `a\n` differ). The last line may have no ending.

## Aligning

Each side is aligned with the base, and the alignment is turned into hunks: maximal runs of changed items, each a range of the base and the range of the side that replaces it. With the default algorithm (Myers) the merge uses its own alignment, not the shortest-script one of `Diff`, because several equally short scripts exist and a merge is only as good as the extents of its hunks. Patience and histogram use the library's own alignments. The rules below were each found by comparing with the output of `git diff` and `git merge-file` 2.51.2 on crafted inputs (every pair over three lines up to length 4, 20,000 random pairs, 60,000 longer pairs with a frequent line) and then confirmed on the 2,599 merge triples. All are *observed in output of git 2.51.2*.

1. **Trimming.** The common start and end of the two sequences are matched first and take no part in anything below.
2. **Items that cannot match.** An item with no copy in the other sequence is changed. The counts are over the whole other sequence, before trimming.
3. **Items with too many copies.** An item is *frequent* when the other sequence holds at least `limit(N)` copies of it, N being the length of the sequence the item is in. There is no limit under 8 items; the limit is 4 for N from 8 to 15, 8 for 16 to 63, 16 for 64 to 255, 32 for 256 to 1023, 64 from 1024 (a power of two near the square root of N).
4. **Which frequent items stay.** Inside the trimmed range, look left and right from a frequent item over items that have no copy (counted as D, left and right) and over other frequent items (counted in P, which starts at 2), stopping at the first ordinary item. The item stays in the search when either D is zero, or when D left plus D right is at most three times P; otherwise it is changed. The looks are static: another frequent item being dropped does not change them.
5. **The search.** Myers 1986 §4b on what is left (forward and reverse searches meeting at a middle snake, recursing on both halves), except that the forward diagonals of each step are visited from k = d down to k = -d; the reverse ones go up. For base `aab` against `ba` this matches the `b`, where visiting in the paper's order matches an `a`.
6. **Sliding.** A run of changed items that could sit in several places is placed by sliding it up as far as it goes (joining runs it meets) and then down as far as it goes, noting each place where the other sequence has a run lined up with it; if sliding down joins the next run, the joined run starts again (up, then down with fresh memory); it settles at the last lined-up place, or at the lowest place if there is none. The first sequence is settled against the other one as searched; the second against the first as settled.

The result is not always the shortest script. Items skipped by rules 2 to 4 never match, so the hunks can be longer than the minimum.

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

## Agreement with git

On the 2,599 real merge triples of the corpus, the text produced equals the output of `git merge-file` 2.51.2 byte for byte in the merge, diff3 and zdiff3 styles, with git choosing its own alignment. The base-to-side alignments themselves still differ from `git diff` on a few corpus pairs (26 of 5,198); none of them changes a merge. Very large inputs may differ from git where git gives up searching for the shortest script, which is not modelled here; the cost cap below is OneDiff's own.

## Cost cap

The alignments of one merge share a budget, `MergeSettings.MaxWork` (2^31 by default, the default of the diff algorithms; zero or more). Work is counted as in the Myers search: one unit per diagonal visited, per item compared along a diagonal, and per item slid. Items that cannot match and the frequent-item rule (steps 2 to 4 above) are linear and not counted.

When the budget runs out:

- the search stops, and every stretch it had not solved is reported as changed on both sides (the same coarsening Myers uses: unsearched parts become delete and insert);
- sliding (step 6) stops where it is; each slide step keeps the alignment valid;
- the merge carries on with the hunks it has. Hunks that are longer than needed only make conflicts larger, or turn a clean merge into a conflict. A change of either side is never dropped: it is applied or inside a conflict, and the regions still tile all three inputs;
- `TextMerge.Diagnostic` and `ItemMerge.Diagnostic` have `CostCapReached` set and `Work` holds the work spent, a little over the cap at most (the cap is checked once per search round, so the overshoot is bounded by the size of the inputs).

A merge that stays within the budget is byte-for-byte what it would be without a cap. With the default cap this never happens on the merge corpus. The patience and histogram alignments receive what is left of the budget and report the same way.

## Conflicts and merged items

`TextMerge.Conflicts` lists the conflict regions in order as `MergeConflict`s: index, region, the 1-based line each version starts on, and the three versions' lines (line endings included). It lists the places the sides disagreed whether or not `MergeSettings.Resolution` settled them in `Text`, so with a resolution `ConflictCount` is zero while `Conflicts` still shows what was settled. `Diff.Merge<T>` returns an `ItemMerge<T>`: `Items` is the merged sequence with conflicts settled as `Resolution` says (null when the resolution is `None` and there are conflicts, because there is no single sequence then), and `Resolve(how)` settles them another way (Ours, Theirs, or Union with ours first).
