using System.Text;
using OneDiff.Merge;
using OneDiff.Output;
using OneDiff.Tokenization;

namespace OneDiff.Tests;

/// <summary>Runs the code shown in docs/migrating-from-diffplex.md so the page stays true.</summary>
public sealed class MigrationPageTests
{
    // Stands in for System.Console so the snippets read as they do on the page and the test can check what they print.
    private static class Console
    {
        [ThreadStatic]
        public static List<string>? lines;

        public static List<string> Lines { get => lines!; set => lines = value; }

        public static void WriteLine(string text) => lines!.Add(text);
    }

    [Fact]
    public void InlineLoop()
    {
        const string before = "Line 1\nOld line 2\nLine 3\n", after = "Line 1\nNew line 2\nLine 3\nLine 4\n";

        // --- begin snippet inline
        var sb = new StringBuilder();
        foreach (DiffRun line in Diff.Lines(before, after).Lines)
        {
            string prefix = line.Kind switch { EditKind.Insert => "+ ", EditKind.Delete => "- ", _ => "  " };
            sb.Append(prefix).Append(line.Text).Append('\n');
        }

        // --- end snippet inline
        Assert.Equal("  Line 1\n- Old line 2\n+ New line 2\n  Line 3\n+ Line 4\n", sb.ToString());
        DocSnippet.AssertInPage("docs/migrating-from-diffplex.md", "inline");
    }

    [Fact]
    public void SideBySideLoop()
    {
        const string before = "Line 1\nOld line 2\nLine 3\n", after = "Line 1\nNew line 2\nLine 3\nLine 4\n";
        Console.Lines = [];

        // --- begin snippet sidebyside
        foreach (SideBySideRow row in Diff.SideBySide(before, after).Rows)
        {
            string left = row.Left?.Text ?? "", right = row.Right?.Text ?? "";
            Console.WriteLine($"{row.Kind,-9} | {left,-30} | {right}");
        }

        // --- end snippet sidebyside
        List<string> rows = Console.Lines;
        Assert.Equal(4, rows.Count);
        Assert.StartsWith("Modified  | Old line 2", rows[1], StringComparison.Ordinal);
        Assert.EndsWith("| New line 2", rows[1], StringComparison.Ordinal);
        Assert.StartsWith("Inserted  |  ", rows[3], StringComparison.Ordinal);
        DocSnippet.AssertInPage("docs/migrating-from-diffplex.md", "sidebyside");
    }

    [Fact]
    public void ConflictLoop()
    {
        string baseText = "a\nb\nc\n", oldText = "a\nx\nc\n", newText = "a\ny\nc\n";
        Console.Lines = [];

        // --- begin snippet conflicts
        TextMerge merge = Diff.Merge(baseText, ours: oldText, theirs: newText);
        if (merge.HasConflicts)
            foreach (MergeConflict c in merge.Conflicts)
                Console.WriteLine($"conflict at ours line {c.OursLine}: {c.OursText} / {c.TheirsText}");
        else Console.WriteLine(merge.Text);
        // --- end snippet conflicts
        Assert.Equal(["conflict at ours line 2: x\n / y\n"], Console.Lines);
        DocSnippet.AssertInPage("docs/migrating-from-diffplex.md", "conflicts");
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
