# Diff algorithms

Behaviour of `MyersDiff`, `PatienceDiff` and `HistogramDiff`. All three take two `ReadOnlySpan<int>` of item ids (equal ids are equal items) and return a canonical `EditScript`: equal runs, and between them a deletion followed by an insertion.

## Myers

Myers 1986, "An O(ND) Difference Algorithm and Its Variations": the greedy furthest-reaching search (section 3) with the linear-space middle-snake refinement (section 4b). The script has the fewest deleted plus inserted items possible (`D`), unless the work cap below is reached.

- A common prefix and suffix are removed first; they belong to some minimal script.
- Items that occur on one side only are set aside before the search (they cannot be in a longest common subsequence) and the search runs on what is left. Minimality is unaffected. This is skipped when the ids are negative or too sparse to count in an array.
- The search finds the middle snake of the (sub)problem, then solves the two halves, depth `O(log D)`. Memory is linear in the input.
- Tie-breaking: among minimal scripts, the one whose forward and reverse searches meet first. It depends only on the input, so repeated runs give the same script. Where several minimal scripts exist this one is not chosen for readability; cleanup does that.

## Work cap

Every search counts work in edit-graph steps: one per diagonal visited plus one per item matched while following a snake (Myers), and for the anchor-based algorithms one per item scanned plus one per item compared while growing a match.

- The default cap is 2^31 steps, far above what ordinary inputs need (a pair differing by D edits costs on the order of D² steps).
- `Compute(old, new, maxWork, out diagnostic)` takes an explicit cap. `long.MaxValue` means unbounded. `DiffDiagnostic.CostCapReached` reports whether it was hit and `Work` how much was spent.
- When the cap is reached, parts already solved stay as they are and every part not yet searched becomes one deletion followed by one insertion. The script is valid but, for Myers, may be longer than the minimum. Patience and histogram are not minimal anyway.
- Cancellation is checked once per edit count `d` in Myers and once per segment in the anchor-based algorithms.

## Patience

From the published write-ups of Bram Cohen's patience diff. In each pair of ranges (after trimming a common prefix and suffix):

1. Items that occur exactly once in each range are candidates, taken in old order.
2. Their longest subsequence that is increasing in both ranges (patience sorting over the new positions) becomes the anchors.
3. The ranges between anchors are processed the same way.
4. A range with no candidate is finished with Myers.

## Histogram

From the published descriptions of histogram diff. In each pair of ranges (after trimming):

1. Count how often each item occurs in the old range.
2. For each item of the new range that occurs in the old range at most 64 times, take each old occurrence as a starting point, grow the match in both directions while items are equal, and score it by its rarest item (lowest old-range count). A lower score wins; on equal scores the longer match; then the earlier one (earlier in the new range, then in the old).
3. After examining a starting point, scanning resumes after the furthest end of the matches grown from it.
4. The best match is the anchor; the ranges before and after it are processed the same way.
5. If the ranges share items but every shared item occurs more than 64 times in the old range, the range is finished with Myers. If they share nothing it is one deletion plus one insertion.

The limit of 64 is a fixed constant of this implementation.

## Lines and the final newline

Line tokens keep their terminator, so a last line with and without a newline are different items. Tokenizers decide where a line ends; the corpus comparison against GNU `diff` used `\n` only as the line end, as GNU does.
