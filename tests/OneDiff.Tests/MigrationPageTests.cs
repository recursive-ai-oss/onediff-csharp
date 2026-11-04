using System.Text;
using OneDiff.Merge;
using OneDiff.Tokenization;

namespace OneDiff.Tests;

/// <summary>Runs the code shown in docs/migrating-from-diffplex.md so the page stays true.</summary>
public sealed class MigrationPageTests
{
    [Fact]
    public void InlineLoop()
    {
        const string Before = "Line 1\nOld line 2\nLine 3\n", After = "Line 1\nNew line 2\nLine 3\nLine 4\n";

        // --- begin snippet
        var sb = new StringBuilder();
        TextDiff diff = Diff.Lines(Before, After);
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

        // --- end snippet
        Assert.Equal("  Line 1\n- Old line 2\n+ New line 2\n  Line 3\n+ Line 4\n", sb.ToString());
    }

    [Fact]
    public void IgnoreOptions()
    {
        Assert.True(Diff.LinesUsing("a  b\nc\n", "a b\nc\n", TokenComparer.IgnoreWhiteSpaceChanges).Script.IsIdentical);
        Assert.True(Diff.LinesUsing("a b\n", "ab\n", TokenComparer.IgnoreAllWhiteSpace).Script.IsIdentical);
        Assert.True(Diff.LinesUsing("Abc\n", "aBC\n", TokenComparer.IgnoreCase).Script.IsIdentical);
        Assert.True(Diff.LinesUsing("A b\n", "a  B\n", TokenComparer.Create(ignoreCase: true, WhiteSpaceHandling.IgnoreChanges)).Script.IsIdentical);

        // Line tokens keep their terminators, so a different line ending is a difference unless white space is ignored.
        Assert.False(Diff.Lines("a\r\nb\r\n", "a\nb\n").Script.IsIdentical);
        Assert.True(Diff.LinesUsing("a\r\nb\r\n", "a\nb\n", TokenComparer.IgnoreWhiteSpaceChanges).Script.IsIdentical);
    }

    [Fact]
    public void CustomChunks()
    {
        string[] oldChunks = "a,b,c".Split(','), newChunks = "a,x,c".Split(',');
        EditScript script = Diff.Sequences<string>(oldChunks, newChunks);
        Assert.Equal(2, script.EditDistance);
        Assert.Equal(["a", "x", "c"], script.Apply<string>(oldChunks, newChunks));
    }

    [Fact]
    public void UnifiedAndMerge()
    {
        Assert.Equal("--- before.txt\n+++ after.txt\n@@ -1,4 +1,4 @@\n Line 1\n-Old line 2\n+New line 2\n Line 3\n Line 4\n",
            Diff.Unified("Line 1\nOld line 2\nLine 3\nLine 4\n", "Line 1\nNew line 2\nLine 3\nLine 4\n", "before.txt", "after.txt", contextLines: 5));

        TextMerge merge = Diff.Merge("a\nb\nc\nd\ne\n", "a\nB\nc\nd\ne\n", "a\nb\nc\nd\nE\n");
        Assert.False(merge.HasConflicts);
        Assert.Equal("a\nB\nc\nd\nE\n", merge.Text);
        TextMerge conflict = Diff.Merge("a\nb\nc\n", "a\nx\nc\n", "a\ny\nc\n");
        Assert.True(conflict.HasConflicts);
        Assert.Equal(1, conflict.ConflictCount);
    }
}
