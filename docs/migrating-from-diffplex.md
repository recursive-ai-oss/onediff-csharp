# Migrating from DiffPlex

This page maps the calls in DiffPlex's README, checked against the public members of DiffPlex 1.9.0, to the `RecursiveAI.OneDiff` calls that do the same job. It is a mapping only: OneDiff does not copy DiffPlex's types or shapes, and there is no compatibility layer. Where the README does not say exactly what a DiffPlex option does, the row says "closest" and you should check the result on your own data. Rows marked *not provided* have no equivalent in the text core.

Everything below is in namespace `OneDiff` (and `OneDiff.Tokenization`, `OneDiff.Merge` where noted). All calls are static and take plain strings.

## The result: `DiffResult` → `TextDiff` and `EditScript`

`Diff.Lines`, `Diff.Words`, `Diff.Characters`, `Diff.Graphemes` and `Diff.Text` return a `TextDiff`: both strings, their tokens and an `EditScript`. The script is a list of `Edit` runs (`Equal`, `Delete`, `Insert`) whose indices count tokens of the old and new text. `TextDiff.OldSpan(edit)` and `NewSpan(edit)` give a run's text back. Replacements are a `Delete` followed by an `Insert`.

## `Differ` (`IDiffer`)

| DiffPlex | OneDiff | Notes |
|---|---|---|
| `CreateLineDiffs(old, new, ignoreWhitespace: false)` | `Diff.Lines(old, new)` | Line tokens keep their line terminator, so `a\r\n` and `a\n` are different lines. |
| `CreateLineDiffs(old, new, ignoreWhitespace: true)` | `Diff.LinesUsing(old, new, TokenComparer.IgnoreWhiteSpaceChanges)` or `TokenComparer.IgnoreAllWhiteSpace` | Closest. `IgnoreWhiteSpaceChanges` treats runs of white space as equal (and ignores a different line ending); `IgnoreAllWhiteSpace` drops white space altogether. |
| `CreateCharacterDiffs(old, new, ignoreWhitespace)` | `Diff.Characters(old, new)`; with a comparer, `Diff.TextUsing(old, new, comparer, TokenKind.Character)` | Characters are Unicode scalar values, so surrogate pairs stay whole. `Diff.Graphemes` compares user-perceived characters. |
| `CreateWordDiffs(old, new, ignoreWhitespace, separators)` | `Diff.Words(old, new)`; with a comparer, `Diff.WordsUsing(old, new, comparer)` | Word boundaries are fixed rules (see `TokenKind.Word`), not a separator list, so token boundaries differ from DiffPlex's. For your own separators, cut the text yourself and use `Diff.Sequences` (below). |
| `CreateCustomDiffs(old, new, ignoreWhiteSpace, chunker)` | `Diff.Sequences<string>(oldChunks, newChunks, comparer: …)` | You split the text; the script indexes your chunks. |
| `CreateDiffs(old, new, ignoreWhiteSpace, ignoreCase, chunker)` | `Diff.TextUsing(old, new, TokenComparer.Create(ignoreCase, whiteSpace), kind)` | `TokenComparer.IgnoreCase`, `.IgnoreWhiteSpaceChanges`, `.IgnoreAllWhiteSpace` and `.Create(ignoreCase, WhiteSpaceHandling)` cover the combinations. The edit script still indexes the original tokens. |

Which algorithm runs is a parameter: `Diff.Lines(old, new, DiffAlgorithm.Patience)` (`Myers`, `Patience`, `Histogram`). To cap the work on untrusted text: `Diff.Lines(old, new, new DiffSettings { MaxWork = 100_000 }).Diagnostic.CostCapReached`. DiffPlex's `Differ` has no algorithm choice or cap to map these to.

### Chunkers (`IChunker`)

| DiffPlex | OneDiff |
|---|---|
| `LineChunker`, `LineEndingsPreservingChunker` | `TokenKind.Line` (line tokens include their terminator) |
| `CharacterChunker` | `TokenKind.Character` (or `TokenKind.Grapheme`) |
| `WordChunker` | `TokenKind.Word` |
| `DelimiterChunker`, `CustomFunctionChunker` | split yourself, then `Diff.Sequences<string>` |

`Tokenizer.Tokenize(text, kind)` returns the token ranges if you want to look at them.

## `InlineDiffBuilder` (the README's console sample)

The README builds an inline model with `InlineDiffBuilder.Diff(before, after)` and prints each line of `diff.Lines` with a `+`, `-` or blank prefix according to `line.Type`. `TextDiff.Lines` is the same view: one entry per line with its kind, text (without the line break) and line number in each text.

| DiffPlex | OneDiff |
|---|---|
| `DiffPaneModel.Lines` | `TextDiff.Lines` |
| `DiffPiece.Type` (`Unchanged`, `Deleted`, `Inserted`) | `DiffPiece.Kind` (`EditKind.Equal`, `Delete`, `Insert`) |
| `DiffPiece.Text` | `DiffPiece.Text` |
| `DiffPiece.Position` | `DiffPiece.OldLine` / `DiffPiece.NewLine` (both, one or the other, from 1; null on the side that lacks the line) |
| `DiffPaneModel.HasDifferences` | `!diff.Script.IsIdentical` |

```csharp
var sb = new StringBuilder();
foreach (DiffPiece line in Diff.Lines(before, after).Lines)
{
    string prefix = line.Kind switch { EditKind.Insert => "+ ", EditKind.Delete => "- ", _ => "  " };
    sb.Append(prefix).Append(line.Text).Append('\n');
}
```

`TextDiff.Pieces` gives the same runs without cutting them at line breaks (for a word diff, runs of words). `diff.Script`, `OldSpan` and `NewSpan` are still there when you want token indices. For a finished rendering instead of a loop: `Diff.ToTerminal(diff)` (ANSI colours, changed words highlighted, context lines and hunks), `Diff.ToMarkdown(diff)` (a fenced `diff` block) and `Diff.ToMarkdownInline(Diff.Words(a, b))` (`<del>`/`<ins>` inline).

## `SideBySideDiffBuilder` (`ISideBySideDiffBuilder`)

`Diff.SideBySide(old, new)` returns a `SideBySideDiff`: aligned `Rows`, each with a `Kind` (`Unchanged`, `Deleted`, `Inserted`, `Modified`) and a `Left` and `Right` cell. A cell has the line number, the text and `Parts`, which for a modified line are the word-level pieces (Equal and Delete on the left, Equal and Insert on the right), the way DiffPlex fills `SubPieces`.

| DiffPlex | OneDiff |
|---|---|
| `SideBySideDiffModel.OldText.Lines`, `.NewText.Lines` (padded with `Imaginary` pieces) | `SideBySideDiff.Rows`, with `Left` or `Right` null where DiffPlex pads |
| `DiffPiece.Type` `Modified` | `SideBySideRow.Kind == SideBySideKind.Modified` |
| `DiffPiece.SubPieces` | `SideBySideCell.Parts` |

```csharp
foreach (SideBySideRow row in Diff.SideBySide(before, after).Rows)
{
    string left = row.Left?.Text ?? "", right = row.Right?.Text ?? "";
    Console.WriteLine($"{row.Kind,-9} | {left,-30} | {right}");
}
```

With an already computed line diff (for example one made with a comparer): `Diff.ToSideBySide(diff)`.

## `UnidiffRenderer`

| DiffPlex 1.9.0 | OneDiff |
|---|---|
| `GenerateUnidiff(old, new, oldFileName, newFileName, ignoreWhitespace, ignoreCase, contextLines)` | `Diff.Unified(old, new, oldFileName, newFileName, contextLines)` |
| `Generate(old, new, oldFileName, newFileName, ignoreWhitespace, ignoreCase)` | `Diff.Unified(old, new, oldFileName, newFileName)` |
| `Generate(diffResult, oldFileName, newFileName)` | `Diff.Unified(old, new, …)` (the diff is computed inside; to keep a `TextDiff`, render it with `Diff.ToMarkdown` or `TerminalRenderer`) |
| options to ignore white space and case | not available on `Diff.Unified`; render a comparer-based diff with `TerminalRenderer` or `MarkdownRenderer` over `Diff.LinesUsing(…)` instead |

`Diff.Unified` has no timestamps in the header lines, and also takes `algorithm:` or, to cap the work, `DiffSettings`. Unified text parses back with `UnifiedPatch.Parse` and applies with `Diff.ApplyUnified`.

## `ThreeWayDiffer` (`IThreeWayDiffer`)

DiffPlex's `CreateMerge(baseText, oldText, newText, ignoreWhiteSpace, ignoreCase, chunker)` takes the two modified versions as "old text" and "new text"; they map to `ours` and `theirs`, in that order. Its result, `ThreeWayMergeResult`, has `IsSuccessful`, `MergedPieces`, `ConflictBlocks` and `DiffResult`.

| DiffPlex 1.9.0 | OneDiff |
|---|---|
| `CreateMerge(baseText, oldText, newText, …)` | `Diff.Merge(baseText, ours: oldText, theirs: newText)` returns a `TextMerge` |
| `result.IsSuccessful` | `!merge.HasConflicts` |
| `result.ConflictBlocks` | `merge.Conflicts`: each with the base, ours and theirs lines and line numbers; `merge.ConflictCount` for the count |
| `result.MergedPieces` | `merge.Text`, with conflicts marked in git's `merge`, `diff3` or `zdiff3` layout (`new MergeSettings { Style = … }`), or settled with `Resolution = ConflictResolution.Ours/Theirs/Union` |
| `CreateDiffs(…)` (three-way diff) | `merge.Regions`: the stretches of the merge, each with its base, ours and theirs line range |
| `ignoreWhiteSpace`, `ignoreCase`, `chunker` | `Diff.Merge<T>(baseItems, ours, theirs, settings, comparer)` over items you cut yourself (for example lines) with the comparer you want; `ItemMerge<T>.Items` holds the merged items |

```csharp
TextMerge merge = Diff.Merge(baseText, ours: oldText, theirs: newText);
if (merge.HasConflicts)
    foreach (MergeConflict c in merge.Conflicts)
        Console.WriteLine($"conflict at ours line {c.OursLine}: {c.OursText} / {c.TheirsText}");
else Console.WriteLine(merge.Text);
```

Changes on adjacent lines are a conflict, as in git; changes separated by an unchanged line merge cleanly.

## Beyond DiffPlex

The text core also has semantic and efficiency cleanup (`Diff.Semantic`, `Diff.Efficient`), diff-match-patch patch text (`Diff.PatchText`, `Diff.ApplyPatchText`) and unified patch apply with fuzz (`Diff.ApplyUnified`), and the `onediff diff | patch | merge` command line over the same calls. Document comparison lives in `RecursiveAI.OneDiff.Documents` and the adapter packages.

The snippets and mappings above that show behaviour are covered by tests in the repository (`MigrationPageTests`).
