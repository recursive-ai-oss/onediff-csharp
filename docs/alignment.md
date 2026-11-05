# Alignment

What `Stages.Align` does, in the order it does it, so that a different implementation (or a person correcting one) can follow the same rules. The result is an `Alignment` of `Match`, `Move`, `Split` and `Merge` links (see `compare-model.md`); verification checks it afterwards, so these rules decide how a change *reads*, never whether the comparison is complete.

Everything is deterministic: the same inputs and settings give the same links. Ties are broken by node number, never by hash order or time.

## 0. Preparation

For each node: its compared text (normalization folds whose category is not hidden are undone), its words (letters and digits runs of the word tokenizer, compared exactly), the *shingles* of its words (hashes of neighbouring word pairs; the single words too when the text has three words or fewer), and a *subtree id*. Two nodes have the same subtree id exactly when their kind, compared text, content hash (or property name) and child subtree ids are equal. Block attributes and formatting are not part of the id, so a formatting-only change is still an exact match and shows as a format or attribute change later. Pinned links from the seed alignment are kept first and their nodes are left out of everything below.

## 1. Exact sequence

The text core (the settings' algorithm; histogram when the product of the two lengths is over the cost cap) runs over the subtree id plus depth of every unpinned node in document order. Equal runs become `Exact` matches. What lies between two matches is a *stretch*: the old and new nodes in the same stretch are each other's natural partners later. Because the sequence is diffed, a block repeated several times lines up with its own repetitions in order and never crosses.

A sequence can have several equally long readings when blocks repeat, and the text core takes one of them without looking at where the blocks sit. Three rules, applied in this order each time the sequence is run, choose among them by structure:

1. **Windows without a stable run.** Between two runs of at least eight matches in step (which nothing could change), the stretch is searched again (a table of at most 1,500 × 1,500 nodes and 2^20 cells) for the longest match, and among the longest the one with the least *drift*: the sum over matches of how far the position of the pair, counted from the window's start, differs between the sides. Of clauses that differ only in a figure, the one in the same place is matched, not the one that would have to jump over its neighbour.
2. **Copies.** When a run of unmatched nodes (or its last or first whole sibling subtrees) is an exact copy of the run next to it, the copies form a chain and the one left unmatched is the one that keeps all the others nearest to their place on the other side (the sum of index differences; ties go to the later copy, because a copy is made after its original). Copies move as whole sibling subtrees, never cut.
3. **Container context.** A *repeated* pair is a match of a block whose subtree id occurs more than once on a side, or whose subtree holds at most five words (a table value, a short label). It contradicts its context when, walking up from both blocks through containers of equal kind, a container is already matched to another node, or is the *provisional partner* of another. Provisional partners are containers (not matched exactly) where the blocks that are not repeated vote: each exactly matched, non-repeated text block votes for every pair of its unmatched ancestors, and two containers are partners when each is the other's single best vote. A pair that contradicts its context is banned: the subtrees on both sides get ids nothing else has, and the sequence is run again (at most four times). So a value in one row is not paired with the equal value of a row it does not belong to, and a deleted row does not lend its equal cell to the row above.

Stretches are numbered by the matches before them, from the final sequence.

## 2. Exact relocation (when `DetectMoves`)

Subtrees that are still wholly unmatched and equal on both sides are paired wherever they sit, largest subtree first, and among equal subtrees in document order. A moved clause is one link; a moved table is one link for the table plus matches for what is inside it. Subtrees without text or images are never relocated. Two refinements keep equal text from being taken for a move by chance:

- Among equal candidates, one that contradicts its container context (step 1, rule 3) is passed over, and one whose parent is the partner of this one's parent is preferred. The first 64 free candidates are looked at.
- A single text block is not relocated when both it and its partner each have a close edited partner (similarity at least `MatchThreshold`) among the free blocks of the stretch they sit in: clauses that differ only in a figure, each figure shifted by one place, are edits. The pair is left to step 3, where it competes with those edits.

## 3. Text blocks

Eligible blocks are unmatched nodes with at least one word.

1. **Candidates.** An inverted index of shingles (a shingle in more than 24 blocks is ignored) is built for each side. Each block votes for the blocks of the same kind it shares shingles with; the six best (most shared shingles, then nearest position, then lowest number) become candidates, from both directions. Short stretches (at most 100 old-by-new pairs) are compared in full. The work is bounded by the posting cap, so it is near-linear.
2. **Similarity.** The Dice coefficient of the two word multisets; for texts of at most three words also the character-pair Dice, whichever is larger. Pairs under 0.30 are dropped.
3. **Splits and merges** (when `DetectSplitsAndMerges`). A block is a *part* of a longer block (at least four words) when it has at least three words, at least two shingles, and at least 60% of its shingles are in the longer one. Parts of one block that are neighbouring siblings form runs; every window of two to eight parts is scored by the Dice coefficient of the block against the union of the parts. The best window is proposed when it reaches `SplitMergeThreshold` and beats the best single candidate of the block by more than 0.05. A block is not counted as a part of a whole when the whole's previous or next sibling is a better home for it: the sibling is the block's best other candidate, at least `MatchThreshold` similar, and holds a larger share of the block's words than the whole does (or an equal share and a higher similarity). Two neighbouring paragraphs that were both edited, and share template sentences, are then not read as a split. Proposals are accepted best first, skipping any whose nodes are taken.
4. **Judging.** A candidate under 0.95 is ambiguous and goes to the scorer, in batches of 1,024 pairs (`IPairScorer.ScoreBatch`); the rest keep the similarity. The default scorer, `RulesPairScorer`, is the similarity adjusted by structure (+0.02 for each of paragraph style and list level that is equal on both sides, −0.05 for each that differs). A custom scorer replaces that judgement and is never asked about pairs the exact stages or the 0.95 rule settled.
5. **Admission.** A pair in the same stretch needs at least `MatchThreshold`; any other pair is a move candidate and needs `DetectMoves` and either at least `MoveThreshold`, or at least `MatchThreshold` with a *unique partner*: the pair is the best candidate of both blocks and beats every other candidate of either block (local ones included) by at least 0.15. An edited clause that moved has one such partner; two near-identical clauses have two, so neither is guessed.
6. **Choice.** Each admitted pair is weighed: judged similarity, +0.10 if both blocks are in the same stretch, +0.05 if the previous or next sibling of one is matched to the previous or next sibling of the other, +0.02 × (1 − distance between their relative positions in the documents). The global matcher then picks the set of pairs in which no block repeats and the total weight is largest: connected groups of candidates are solved exactly with the Hungarian method up to a size bound, and greedily (heaviest first) beyond it. Chosen pairs in the same stretch have origin `Similar`, the others `Relocated`. A pair that contradicts its container context (step 1, rule 3) and is a repeated pair is left out.

## 4. Containers

A container is an unmatched node with no text of its own that groups others (table, row, cell, section, note, header, footer, comment, text box, content control).

1. **By content.** For every matched pair of text blocks or images, each pair of their ancestors of equal kind gets a vote. A pair of containers scores twice its votes over the sum of their content counts (capped at 1); pairs under 0.25 are dropped; the rest are weighed (+0.05 when their parents are paired, +0.02 × nearness) and chosen by the same global matcher. So a table whose rows were reordered and edited is still the same table, and a row with a new column cell is still the same row.
2. **By position.** Walking the paired containers from the roots down: among the children of a pair, the matched children that keep their order are anchors; between two anchors, if the old side and the new side hold the same number (at least one) of unmatched containers of a kind, they are paired in order (score 0.5, origin `Similar`). This pairs empty cells, rows whose text was all rewritten, and the only section or table of a rewritten document.
3. **Text in a paired cell.** In a pair of table cells, between two matched siblings, a single unmatched text block of a kind on each side is paired (score 0.5, origin `Similar`): a cell whose text was rewritten past recognition is one replace, not a delete beside an insert.

## 5. The cost cap

`CompareSettings.CostCap` bounds the work of the search for edited blocks: comparisons of two blocks, windows of splits and merges, and the cubic work of exact assignments, with postings visited counted at one sixteenth. When the bound is passed, step 3 stops before it creates any link, a warning diagnostic `align.cap` is added, and the result keeps the exact matches, relocations and the containers: complete, but edited blocks show as deleted and inserted. The exact sequence (step 1) is not capped; it uses Myers while the product of the lengths is within the cap and histogram beyond it (an `align.large` information diagnostic).

## 6. Normalization

The links are then normalized (see `compare-model.md`): which one-to-one links are `Move` is decided by the parent and sibling-order rules, not by the steps above.

## Editing

`Pin`, `Force` and `Unpin` act on the result. `Stages.Align(..., seed:)` keeps the pinned links of the seed, does not look at its other links, and aligns everything else afresh.
