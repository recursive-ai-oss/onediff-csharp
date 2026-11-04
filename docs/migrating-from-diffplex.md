# Migrating from DiffPlex

This page maps the calls in DiffPlex's README to the `RecursiveAI.OneDiff` calls that do the same job. It is a mapping only: OneDiff does not copy DiffPlex's types or shapes, and there is no compatibility layer. Where the README does not say exactly what a DiffPlex option does, the row says "closest" and you should check the result on your own data. Rows marked *not provided* have no equivalent in the text core.

Everything below is in namespace `OneDiff` (and `OneDiff.Tokenization`, `OneDiff.Merge` where noted). All calls are static and take plain strings.

## The result: `DiffResult` → `TextDiff` and `EditScript`

`Diff.Lines`, `Diff.Words`, `Diff.Characters`, `Diff.Graphemes` and `Diff.Text` return a `TextDiff`: both strings, their tokens and an `EditScript`. The script is a list of `Edit` runs (`Equal`, `Delete`, `Insert`) whose indices count tokens of the old and new text. `TextDiff.OldSpan(edit)` and `NewSpan(edit)` give a run's text back. Replacements are a `Delete` followed by an `Insert`.

## `Differ` (`IDiffer`)

| DiffPlex | OneDiff | Notes |
|---|---|---|
| `CreateLineDiffs(old, new, ignoreWhiteSpace: false)` | `Diff.Lines(old, new)` | Line tokens keep their line terminator, so `a\r\n` and `a\n` are different lines. |
| `CreateLineDiffs(old, new, ignoreWhiteSpace: true)` | `Diff.LinesUsing(old, new, TokenComparer.IgnoreWhiteSpaceChanges)` or `TokenComparer.IgnoreAllWhiteSpace` | Closest. `IgnoreWhiteSpaceChanges` treats runs of white space as equal (and ignores a different line ending); `IgnoreAllWhiteSpace` drops white space altogether. |
| `CreateCharacterDiffs(old, new, ignoreWhitespace)` | `Diff.Characters(old, new)`; with a comparer, `Diff.TextUsing(old, new, comparer, TokenKind.Character)` | Characters are Unicode scalar values, so surrogate pairs stay whole. `Diff.Graphemes` compares user-perceived characters. |
| `CreateWordDiffs(old, new, ignoreWhitespace, separators)` | `Diff.Words(old, new)`; with a comparer, `Diff.WordsUsing(old, new, comparer)` | Word boundaries are fixed rules (see `TokenKind.Word`), not a separator list, so token boundaries differ from DiffPlex's. For your own separators, cut the text yourself and use `Diff.Sequences` (below). |
| `CreateCustomDiffs(old, new, ignoreWhiteSpace, chunker)` | `Diff.Sequences<string>(oldChunks, newChunks, comparer: …)` | You split the text; the script indexes your chunks. |
| `CreateDiffs(old, new, ignoreWhiteSpace, ignoreCase, chunker)` | `Diff.TextUsing(old, new, TokenComparer.Create(ignoreCase, whiteSpace), kind)` | `TokenComparer.IgnoreCase`, `.IgnoreWhiteSpaceChanges`, `.IgnoreAllWhiteSpace` and `.Create(ignoreCase, WhiteSpaceHandling)` cover the combinations. The edit script still indexes the original tokens. |

Which algorithm runs is a parameter: `Diff.Lines(old, new, DiffAlgorithm.Patience)` (`Myers`, `Patience`, `Histogram`). There is no separate algorithm choice in the README's `Differ` to map it to.

### Chunkers (`IChunker`)

| DiffPlex | OneDiff |
|---|---|
| `LineChunker`, `LineEndingsPreservingChunker` | `TokenKind.Line` (line tokens include their terminator) |
| `CharacterChunker` | `TokenKind.Character` (or `TokenKind.Grapheme`) |
| `WordChunker` | `TokenKind.Word` |
| `DelimiterChunker`, `CustomFunctionChunker` | split yourself, then `Diff.Sequences<string>` |

`Tokenizer.Tokenize(text, kind)` returns the token ranges if you want to look at them.

## `InlineDiffBuilder` (the README's console sample)

The README builds an inline model with `InlineDiffBuilder.Diff(before, after)` and prints each line with a `+`, `-` or blank prefix. The same output from a `TextDiff`:

```csharp
var sb = new StringBuilder();
TextDiff diff = Diff.Lines(before, after);
foreach (Edit e in diff.Script)
{
    string prefix = e.Kind switch { EditKind.Insert => "+ ", EditKind.Delete => "- ", _ => "  " };
    bool fromNew = e.Kind == EditKind.Insert;
    string text = fromNew ? diff.NewText : diff.OldText;
    int start = fromNew ? e.NewStart : e.OldStart, count = fromNew ? e.NewLength : e.OldLength;
    for (int i = start; i < start + count; i++)
    {
        TokenRange line = fromNew ? diff.NewTokens[i] : diff.OldTokens[i];
        sb.Append(prefix).Append(text.AsSpan(line.Start, line.Length).TrimEnd("\r\n")).Append('\n');
    }
}
```

(`TokenRange` is in `OneDiff.Tokenization`.) For a finished rendering instead of a loop: `Diff.ToTerminal(diff)` (ANSI colours, changed words highlighted, context lines and hunks), `Diff.ToMarkdown(diff)` (a fenced `diff` block) and `Diff.ToMarkdownInline(Diff.Words(a, b))` (`<del>`/`<ins>` inline).

## `SideBySideDiffBuilder` (`ISideBySideDiffBuilder`)

*Not provided* as a model type. `Diff.Lines` gives the edit script; a side-by-side view is built from it: an `Equal` run appears on both sides, a `Delete` run on the left, an `Insert` run on the right, and a `Delete` followed by an `Insert` is a changed row pair. For word-level marks inside a changed row, diff the two lines again with `Diff.Words` and read the runs from its script.

## `UnidiffRenderer`

| DiffPlex | OneDiff |
|---|---|
| `UnidiffRenderer.GenerateUnidiff(old, new, oldFileName, newFileName)` | `Diff.Unified(old, new, oldFileName, newFileName)` |
| `new UnidiffRenderer(contextLines: 5).Generate(old, new, "before.txt", "after.txt")` | `Diff.Unified(old, new, "before.txt", "after.txt", contextLines: 5)` |
| options to ignore white space and case | not available on `Diff.Unified`; render a comparer-based diff with `TerminalRenderer` or `MarkdownRenderer` over `Diff.LinesUsing(…)` instead |

`Diff.Unified` has no timestamps in the header lines, and also takes `algorithm:`. Unified text parses back with `UnifiedPatch.Parse` and applies with `Diff.ApplyUnified`.

## `ThreeWayDiffer` (`IThreeWayDiffer`)

The README calls the two modified versions "old text" and "new text"; they map to `ours` and `theirs`, in the order below.

| DiffPlex | OneDiff |
|---|---|
| `CreateMerge(baseText, oldText, newText, …)` | `Diff.Merge(baseText, ours: oldText, theirs: newText)` returns a `TextMerge` |
| `mergeResult.HasConflicts` | `merge.HasConflicts` |
| `mergeResult.ConflictBlocks.Count` | `merge.ConflictCount` |
| `mergeResult.MergedText` | `merge.Text`, with conflicts marked in git's `merge`, `diff3` or `zdiff3` layout (`new MergeSettings { Style = … }`), or settled with `Resolution = ConflictResolution.Ours/Theirs/Union` |
| `CreateDiffs(…)` (three-way diff) | `merge.Regions`: the stretches of the merge, each with its base, ours and theirs line range |
| `ignoreWhiteSpace`, `ignoreCase`, `chunker` | `Diff.Merge<T>(baseItems, ours, theirs, settings, comparer)` over items you cut yourself (for example lines) with the comparer you want |

```csharp
TextMerge merge = Diff.Merge(baseText, ours: oldText, theirs: newText);
if (merge.HasConflicts) Console.WriteLine($"Found {merge.ConflictCount} conflicts");
else Console.WriteLine(merge.Text);
```

Changes on adjacent lines are a conflict, as in git; changes separated by an unchanged line merge cleanly.

## Beyond DiffPlex

The text core also has semantic and efficiency cleanup (`Diff.Semantic`, `Diff.Efficient`), diff-match-patch patch text (`Diff.PatchText`, `Diff.ApplyPatchText`) and unified patch apply with fuzz (`Diff.ApplyUnified`), and the `onediff diff | patch | merge` command line over the same calls. Document comparison lives in `RecursiveAI.OneDiff.Documents` and the adapter packages.

The snippets and mappings above that show behaviour are covered by tests in the repository (`MigrationPageTests`).
