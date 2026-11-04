# Alignment

What `Stages.Align` does, in the order it does it, so that a different implementation (or a person correcting one) can follow the same rules. The result is an `Alignment` of `Match`, `Move`, `Split` and `Merge` links (see `compare-model.md`); verification checks it afterwards, so these rules decide how a change *reads*, never whether the comparison is complete.

Everything is deterministic: the same inputs and settings give the same links. Ties are broken by node number, never by hash order or time.

## 0. Preparation

For each node: its compared text (normalization folds whose category is not hidden are undone), its words (letters and digits runs of the word tokenizer, compared exactly), the *shingles* of its words (hashes of neighbouring word pairs; the single words too when the text has three words or fewer), and a *subtree id*. Two nodes have the same subtree id exactly when their kind, compared text, content hash (or property name) and child subtree ids are equal. Block attributes and formatting are not part of the id, so a formatting-only change is still an exact match and shows as a format or attribute change later. Pinned links from the seed alignment are kept first and their nodes are left out of everything below.

## 1. Exact sequence

The text core (the settings' algorithm; histogram when the product of the two lengths is over the cost cap) runs over the subtree id plus depth of every unpinned node in document order. Equal runs become `Exact` matches. What lies between two equal runs is a *stretch*: the old and new nodes in the same stretch are each other's natural partners later. Because the sequence is diffed, a block repeated several times lines up with its own repetitions in order and never crosses.

## 2. Exact relocation (when `DetectMoves`)

Subtrees that are still wholly unmatched and equal on both sides are paired wherever they sit, largest subtree first, and among equal subtrees in document order. A moved clause is one link; a moved table is one link for the table plus matches for what is inside it. Subtrees without text or images are never relocated.

## 3. Text blocks

Eligible blocks are unmatched nodes with at least one word.

1. **Candidates.** An inverted index of shingles (a shingle in more than 24 blocks is ignored) is built for each side. Each block votes for the blocks of the same kind it shares shingles with; the six best (most shared shingles, then nearest position, then lowest number) become candidates, from both directions. Short stretches (at most 100 old-by-new pairs) are compared in full. The work is bounded by the posting cap, so it is near-linear.
2. **Similarity.** The Dice coefficient of the two word multisets; for texts of at most three words also the character-pair Dice, whichever is larger. Pairs under 0.30 are dropped.
3. **Splits and merges** (when `DetectSplitsAndMerges`). A block is a *part* of a longer block (at least four words) when it has at least three words, at least two shingles, and at least 60% of its shingles are in the longer one. Parts of one block that are neighbouring siblings form runs; every window of two to eight parts is scored by the Dice coefficient of the block against the union of the parts. The best window is proposed when it reaches `SplitMergeThreshold` and beats the best single candidate of the block by more than 0.05. Proposals are accepted best first, skipping any whose nodes are taken.
4. **Judging.** A candidate under 0.95 is ambiguous and goes to the scorer, in batches of 1,024 pairs (`IPairScorer.ScoreBatch`); the rest keep the similarity. The default scorer, `RulesPairScorer`, is the similarity adjusted by structure (+0.02 for each of paragraph style and list level that is equal on both sides, −0.05 for each that differs). A custom scorer replaces that judgement and is never asked about pairs the exact stages or the 0.95 rule settled.
5. **Admission.** A pair in the same stretch needs at least `MatchThreshold`; any other pair is a move candidate and needs `DetectMoves` and either at least `MoveThreshold`, or at least `MatchThreshold` with a *unique partner*: the pair is the best candidate of both blocks and beats every other candidate of either block (local ones included) by at least 0.15. An edited clause that moved has one such partner; two near-identical clauses have two, so neither is guessed.
6. **Choice.** Each admitted pair is weighed: judged similarity, +0.10 if both blocks are in the same stretch, +0.05 if the previous or next sibling of one is matched to the previous or next sibling of the other, +0.02 × (1 − distance between their relative positions in the documents). The global matcher then picks the set of pairs in which no block repeats and the total weight is largest: connected groups of candidates are solved exactly with the Hungarian method up to a size bound, and greedily (heaviest first) beyond it. Chosen pairs in the same stretch have origin `Similar`, the others `Relocated`.

## 4. Containers

A container is an unmatched node with no text of its own that groups others (table, row, cell, section, note, header, footer, comment, text box, content control).

1. **By content.** For every matched pair of text blocks or images, each pair of their ancestors of equal kind gets a vote. A pair of containers scores twice its votes over the sum of their content counts (capped at 1); pairs under 0.25 are dropped; the rest are weighed (+0.05 when their parents are paired, +0.02 × nearness) and chosen by the same global matcher. So a table whose rows were reordered and edited is still the same table, and a row with a new column cell is still the same row.
2. **By position.** Walking the paired containers from the roots down: among the children of a pair, the matched children that keep their order are anchors; between two anchors, if the old side and the new side hold the same number (at least one) of unmatched containers of a kind, they are paired in order (score 0.5, origin `Similar`). This pairs empty cells, rows whose text was all rewritten, and the only section or table of a rewritten document.

## 5. The cost cap

`CompareSettings.CostCap` bounds the work of the search for edited blocks: comparisons of two blocks, windows of splits and merges, and the cubic work of exact assignments, with postings visited counted at one sixteenth. When the bound is passed, step 3 stops before it creates any link, a warning diagnostic `align.cap` is added, and the result keeps the exact matches, relocations and the containers: complete, but edited blocks show as deleted and inserted. The exact sequence (step 1) is not capped; it uses Myers while the product of the lengths is within the cap and histogram beyond it (an `align.large` information diagnostic).

## 6. Normalization

The links are then normalized (see `compare-model.md`): which one-to-one links are `Move` is decided by the parent and sibling-order rules, not by the steps above.

## Editing

`Pin`, `Force` and `Unpin` act on the result. `Stages.Align(..., seed:)` keeps the pinned links of the seed, does not look at its other links, and aligns everything else afresh.
