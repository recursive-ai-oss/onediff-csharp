using OneDiff.Algorithms;
using OneDiff.Cleanup;
using OneDiff.Merge;
using OneDiff.Output;
using OneDiff.Patching;
using OneDiff.Tokenization;

namespace OneDiff.Tests;

/// <summary>The reading views, settings overloads, merge views and nullable results of the text core's public API.</summary>
public sealed class ApiShapeTests
{
    [Fact]
    public void WorkIsCountedOnSmallDiffsToo()
    {
        string a = string.Join('\n', Enumerable.Range(0, 40).Select(i => "line " + i)) + "\n";
        string b = a.Replace("line 7\n", "seven\n", StringComparison.Ordinal).Replace("line 30\n", "thirty\nextra\n", StringComparison.Ordinal);
        foreach (DiffAlgorithm algorithm in new[] { DiffAlgorithm.Myers, DiffAlgorithm.Patience, DiffAlgorithm.Histogram })
        {
            DiffDiagnostic d = Diff.Lines(a, b, new DiffSettings { Algorithm = algorithm }).Diagnostic;
            Assert.False(d.CostCapReached);
            Assert.True(d.Work >= 7, algorithm + " reported work " + d.Work);
        }

        Assert.Equal(3, Diff.Lines("x\ny\nz\n", "x\ny\nz\n").Diagnostic.Work);
        Assert.Equal(0, Diff.Lines(string.Empty, string.Empty).Diagnostic.Work);
    }

    [Fact]
    public void ComparerAndCleanupOneLinersTakeTheWorkCap()
    {
        string a = Shuffled(300, 7), b = Shuffled(300, 13);
        var tight = new DiffSettings { MaxWork = 10 };
        foreach (TextDiff d in new[]
        {
            Diff.WordsUsing(a, b, StringComparer.OrdinalIgnoreCase, tight),
            Diff.LinesUsing(a.Replace(' ', '\n'), b.Replace(' ', '\n'), StringComparer.OrdinalIgnoreCase, tight),
            Diff.TextUsing(a, b, StringComparer.OrdinalIgnoreCase, TokenKind.Word, tight),
            Diff.Semantic(a, b, TokenKind.Word, tight),
            Diff.Efficient(a, b, 4, TokenKind.Word, tight),
        })
        {
            Assert.True(d.Diagnostic.CostCapReached);
            Assert.Equal(a.Length, d.OldText.Length);
        }

        Assert.False(Diff.WordsUsing(a, b, StringComparer.OrdinalIgnoreCase, new DiffSettings()).Diagnostic.CostCapReached);
        Assert.Throws<ArgumentOutOfRangeException>(() => Diff.WordsUsing("a", "b", StringComparer.Ordinal, new DiffSettings { MaxWork = -1 }));
    }

    // ---- Reading views -------------------------------------------------------------------------------------------

    [Fact]
    public void LineDiffReadsAsLinesWithNumbers()
    {
        TextDiff d = Diff.Lines("a\nb\nc\n", "a\nB\nc\nd\n");
        Assert.Equal(
        [
            new DiffRun(EditKind.Equal, 1, 1, "a"),
            new DiffRun(EditKind.Delete, 2, null, "b"),
            new DiffRun(EditKind.Insert, null, 2, "B"),
            new DiffRun(EditKind.Equal, 3, 3, "c"),
            new DiffRun(EditKind.Insert, null, 4, "d"),
        ], d.Lines);
    }

    [Fact]
    public void LinesHandleCrLfAndMissingFinalNewline()
    {
        TextDiff d = Diff.Lines("a\r\nb", "a\r\nc");
        Assert.Equal(
        [
            new DiffRun(EditKind.Equal, 1, 1, "a"),
            new DiffRun(EditKind.Delete, 2, null, "b"),
            new DiffRun(EditKind.Insert, null, 2, "c"),
        ], d.Lines);
        Assert.Empty(Diff.Lines(string.Empty, string.Empty).Lines);
        Assert.Equal([new DiffRun(EditKind.Delete, 1, null, string.Empty)], Diff.Lines("\n", string.Empty).Lines);
    }

    [Fact]
    public void WordDiffPiecesAreRunsAndLinesAreCutAtBreaks()
    {
        TextDiff d = Diff.Words("one two\nthree four\n", "one 2\nthree four\nfive\n");
        IReadOnlyList<DiffRun> pieces = d.Pieces;
        Assert.All(pieces, p => Assert.False(string.IsNullOrEmpty(p.Text)));
        // Old pieces concatenate to the old text, new pieces to the new text.
        Assert.Equal(d.OldText, string.Concat(pieces.Where(p => p.Kind != EditKind.Insert).Select(p => p.Text)));
        Assert.Equal(d.NewText, string.Concat(pieces.Where(p => p.Kind != EditKind.Delete).Select(p => p.Text)));
        Assert.Contains(pieces, p => p.Kind == EditKind.Delete && p.Text == "two" && p.OldLine == 1);
        Assert.Contains(pieces, p => p.Kind == EditKind.Insert && p.Text == "2" && p.NewLine == 1);

        IReadOnlyList<DiffRun> lines = d.Lines;
        Assert.All(lines, l => Assert.DoesNotContain('\n', l.Text));
        Assert.Contains(lines, l => l.Kind == EditKind.Insert && l.Text == "five" && l.NewLine == 3);
        Assert.Contains(lines, l => l.Kind == EditKind.Equal && l.Text == "three four" && l.OldLine == 2 && l.NewLine == 2);
    }

    [Theory]
    [InlineData(TokenKind.Line)]
    [InlineData(TokenKind.Word)]
    [InlineData(TokenKind.Character)]
    [InlineData(TokenKind.Grapheme)]
    public void LinesRebuildBothTextsForEveryKind(TokenKind kind)
    {
        string a = "x y\r\nz 😀 é\n\nlast", b = "x w\r\nz 😀 e\nmore\n\nlast!";
        TextDiff d = Diff.Text(a, b, kind);
        static string Join(IEnumerable<string> parts) => string.Concat(parts);

        // Cutting at line breaks loses only the breaks: the non-empty text outside them is the same.
        string Strip(string s) => s.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
        Assert.Equal(Strip(a), Join(d.Lines.Where(l => l.Kind != EditKind.Insert).Select(l => l.Text)));
        Assert.Equal(Strip(b), Join(d.Lines.Where(l => l.Kind != EditKind.Delete).Select(l => l.Text)));
        Assert.Equal(a, Join(d.Pieces.Where(p => p.Kind != EditKind.Insert).Select(p => p.Text)));
    }

    [Fact]
    public void SideBySidePairsChangedLinesAndMarksWords()
    {
        SideBySideDiff sbs = Diff.SideBySide("keep\npay in thirty days\ngone\n", "keep\npay in forty days\nnew1\nnew2\n");
        IReadOnlyList<SideBySideRow> rows = sbs.Rows;
        Assert.Equal([SideBySideKind.Unchanged, SideBySideKind.Modified, SideBySideKind.Modified, SideBySideKind.Inserted], rows.Select(r => r.Kind));

        SideBySideRow changed = rows[1];
        Assert.Equal((2, "pay in thirty days"), (changed.Left!.Line, changed.Left.Text));
        Assert.Equal((2, "pay in forty days"), (changed.Right!.Line, changed.Right.Text));
        Assert.Equal("thirty", Assert.Single(changed.Left.Parts, p => p.Kind == EditKind.Delete).Text);
        Assert.Equal("forty", Assert.Single(changed.Right.Parts, p => p.Kind == EditKind.Insert).Text);
        Assert.Equal(changed.Left.Text, string.Concat(changed.Left.Parts.Select(p => p.Text)));
        Assert.Equal(changed.Right.Text, string.Concat(changed.Right.Parts.Select(p => p.Text)));

        Assert.Equal("new2", rows[3].Right!.Text);
        Assert.Null(rows[3].Left);
        Assert.Equal(4, rows[3].Right!.Line);
    }

    [Fact]
    public void SideBySideCoversEveryLineOnce()
    {
        string a = "1\n2\n3\n4\n5\n", b = "1\nx\n3\n5\n6\n";
        SideBySideDiff sbs = Diff.SideBySide(a, b);
        Assert.Equal([1, 2, 3, 4, 5], sbs.Rows.Where(r => r.Left is not null).Select(r => r.Left!.Line));
        Assert.Equal([1, 2, 3, 4, 5], sbs.Rows.Where(r => r.Right is not null).Select(r => r.Right!.Line));
        Assert.Throws<ArgumentException>(() => SideBySideDiff.From(Diff.Words(a, b)));
        Assert.Empty(Diff.SideBySide(string.Empty, string.Empty).Rows);
        Assert.Equal(SideBySideKind.Deleted, Assert.Single(Diff.SideBySide("a\n", string.Empty).Rows).Kind);
    }

    // Lines whose order differs between two multipliers, so the diff has many common lines and needs real search.
    private static string Shuffled(int count, int multiplier) => string.Concat(Enumerable.Range(0, count).Select(i => $"line {i * multiplier % 101}\n"));

    // ---- Settings ------------------------------------------------------------------------------------------------

    [Fact]
    public void CappedLineDiffIsOneCallAndReportsTheCap()
    {
        string a = Shuffled(3000, 7), b = Shuffled(3000, 13);

        TextDiff capped = Diff.Lines(a, b, new DiffSettings { MaxWork = 1000 });
        Assert.True(capped.Diagnostic.CostCapReached);
        Assert.Equal((3000, 3000), (capped.Script.OldLength, capped.Script.NewLength)); // still a complete script

        TextDiff free = Diff.Lines(a, b);
        Assert.False(free.Diagnostic.CostCapReached);
        Assert.True(free.Diagnostic.Work > 0 || free.Script.EditDistance > 0);
    }

    [Theory]
    [InlineData(DiffAlgorithm.Myers)]
    [InlineData(DiffAlgorithm.Patience)]
    [InlineData(DiffAlgorithm.Histogram)]
    public void SettingsPickTheAlgorithmAndAgreeWithTheOverloads(DiffAlgorithm algorithm)
    {
        string a = "a\nb\nc\nd\ne\nf\n", b = "a\nc\nb\nd\nf\ne\n";
        var settings = new DiffSettings { Algorithm = algorithm };
        Assert.Equal(Diff.Lines(a, b, algorithm).Script.ToArray(), Diff.Lines(a, b, settings).Script.ToArray());
        Assert.Equal(Diff.Words(a, b, algorithm).Script.ToArray(), Diff.Words(a, b, settings).Script.ToArray());
        Assert.Equal(Diff.Characters(a, b, algorithm).Script.ToArray(), Diff.Characters(a, b, settings).Script.ToArray());
        Assert.Equal(Diff.Graphemes(a, b, algorithm).Script.ToArray(), Diff.Graphemes(a, b, settings).Script.ToArray());
        Assert.Equal(Diff.Unified(a, b, algorithm: algorithm), Diff.Unified(a, b, settings));
    }

    [Fact]
    public void CappedDiffsStillRoundTrip()
    {
        string a = Shuffled(400, 7), b = Shuffled(400, 13);
        var tight = new DiffSettings { MaxWork = 50 };
        string patch = Diff.Unified(a, b, tight);
        Assert.Equal(b, Diff.ApplyUnified(a, patch).Text);
        int[] x = Enumerable.Range(0, 300).Select(i => i * 7 % 101).ToArray(), y = Enumerable.Range(0, 300).Select(i => i * 13 % 101).ToArray();
        EditScript s = Diff.Sequences<int>(x, y, tight, out DiffDiagnostic diagnostic);
        Assert.Equal(y, s.Apply<int>(x, y));
        Assert.True(diagnostic.CostCapReached);
    }

    [Fact]
    public void SettingsRejectBadValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Diff.Lines("a", "b", new DiffSettings { MaxWork = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Diff.Lines("a", "b", new DiffSettings { Algorithm = (DiffAlgorithm)9 }));
        Assert.Throws<ArgumentNullException>(() => Diff.Lines("a", "b", (DiffSettings)null!));
        Assert.Equal(default, Diff.Lines("a", "b").Diagnostic with { Work = 0 });
    }

    [Fact]
    public void CleanupKeepsTheDiagnostic()
    {
        TextDiff d = Diff.Words(Shuffled(300, 7), Shuffled(300, 13), new DiffSettings { MaxWork = 10 });
        Assert.True(d.Diagnostic.CostCapReached);
        Assert.Equal(d.Diagnostic, DiffCleanup.Semantic(d).Diagnostic);
    }

    [Fact]
    public void ApplyOptionsReachTheOneLiners()
    {
        // Context drifted by three lines of difference: the default fuzz of 2 places the hunk, none does not.
        string oldText = "h1\nh2\nctx1\nctx2\nold\nctx3\nctx4\nt1\n";
        string newText = "h1\nh2\nctx1\nctx2\nnew\nctx3\nctx4\nt1\n";
        string patch = Diff.Unified(oldText, newText);
        string drifted = "h1\nh2\nCTXa\nctx2\nold\nctx3\nCTXb\nt1\n";

        Assert.True(Diff.ApplyUnified(drifted, patch).Success);
        UnifiedApplyResult strict = Diff.ApplyUnified(drifted, patch, new UnifiedApplyOptions { MaxFuzz = 0 });
        Assert.False(strict.Success);
        Assert.Null(Assert.Single(strict.Hunks).Line);
        Assert.Equal(drifted, strict.Text);

        UnifiedApplyResult reversed = Diff.ApplyUnified(newText, patch, new UnifiedApplyOptions { Reverse = true });
        Assert.Equal(oldText, reversed.Text);
    }

    [Fact]
    public void PatchTextTakesAPatcherAndAToken()
    {
        string oldText = "The quick brown fox jumps over the lazy dog.";
        string newText = "The quick red fox jumps over the lazy cat.";
        string patchText = Diff.PatchText(oldText, newText, new DmpPatcher { PatchMargin = 8 });
        Assert.Equal(newText, Diff.ApplyPatchText(oldText, patchText).Text);

        string drifted = "A short intro. The quick brown fox jumps over the lazy dog. And more text.";
        Assert.True(Diff.ApplyPatchText(drifted, patchText).Success);
        DmpApplyResult strict = Diff.ApplyPatchText("Quite another sentence about a fox.", patchText, new DmpPatcher { MatchThreshold = 0.0 });
        Assert.False(strict.Success);
        Assert.All(strict.Patches.Where(p => !p.Applied), p => Assert.Null(p.Location));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Diff.ApplyPatchText(oldText, patchText, null, cts.Token));
        Assert.Throws<OperationCanceledException>(() => Diff.PatchText(oldText, newText, null, cts.Token));
    }

    // ---- Merge views ---------------------------------------------------------------------------------------------

    [Fact]
    public void MergeListsItsConflictsWithThreeVersions()
    {
        const string baseText = "a\nb\nc\nd\ne\n";
        TextMerge merge = Diff.Merge(baseText, "a\nX\nc\nd\ne1\n", "a\nY\nc\nd\ne\n");
        Assert.Equal(1, merge.ConflictCount);
        MergeConflict c = Assert.Single(merge.Conflicts);
        Assert.Equal(0, c.Index);
        Assert.Equal((2, 2, 2), (c.BaseLine, c.OursLine, c.TheirsLine));
        Assert.Equal(("b\n", "X\n", "Y\n"), (c.BaseText, c.OursText, c.TheirsText));
        Assert.Equal(MergeRegionKind.Conflict, c.Region.Kind);

        Assert.Empty(Diff.Merge(baseText, "A\nb\nc\nd\ne\n", "a\nb\nc\nd\nE\n").Conflicts);
        // Settled conflicts are still listed: they are where the sides disagreed.
        TextMerge settled = Diff.Merge(baseText, "a\nX\nc\nd\ne\n", "a\nY\nc\nd\ne\n", new MergeSettings { Resolution = ConflictResolution.Theirs });
        Assert.Equal(0, settled.ConflictCount);
        Assert.Single(settled.Conflicts);
    }

    [Fact]
    public void ItemMergeHoldsTheMergedItems()
    {
        string[] b = ["a", "b", "c", "d"];
        ItemMerge<string> clean = Diff.Merge<string>(b, ["a", "B", "c", "d"], ["a", "b", "c", "D", "e"]);
        Assert.Equal(["a", "B", "c", "D", "e"], clean.Items);

        ItemMerge<string> conflict = Diff.Merge<string>(b, ["a", "X", "c", "d"], ["a", "Y", "c", "d"]);
        Assert.True(conflict.HasConflicts);
        Assert.Null(conflict.Items);
        Assert.Equal(["a", "X", "c", "d"], conflict.Resolve(ConflictResolution.Ours));
        Assert.Equal(["a", "Y", "c", "d"], conflict.Resolve(ConflictResolution.Theirs));
        Assert.Equal(["a", "X", "Y", "c", "d"], conflict.Resolve(ConflictResolution.Union));
        Assert.Null(conflict.Resolve(ConflictResolution.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => conflict.Resolve((ConflictResolution)7));

        ItemMerge<string> resolved = Diff.Merge<string>(b, ["a", "X", "c", "d"], ["a", "Y", "c", "d"], new MergeSettings { Resolution = ConflictResolution.Theirs });
        Assert.Equal(["a", "Y", "c", "d"], resolved.Items);
        ItemMerge asBase = resolved;
        Assert.Single(asBase.Regions, r => r.Kind == MergeRegionKind.Conflict);
    }

    // ---- Nullable results ----------------------------------------------------------------------------------------

    [Fact]
    public void RejectedHunksAndUnplacedPatchesHaveNoPlace()
    {
        UnifiedApplyResult r = Diff.ApplyUnified("totally\nother\n", "--- a\n+++ b\n@@ -1,2 +1,2 @@\n x\n-y\n+z\n");
        HunkResult h = Assert.Single(r.Hunks);
        Assert.Equal(HunkOutcome.Rejected, h.Outcome);
        Assert.Null(h.Line);

        UnifiedApplyResult ok = Diff.ApplyUnified("p\nq\n", "--- a\n+++ b\n@@ -1,2 +1,2 @@\n p\n-q\n+r\n");
        Assert.Equal(0, Assert.Single(ok.Hunks).Line);
    }

    [Fact]
    public void TryMatchReportsMissWithoutSentinelsOrThrowing()
    {
        var m = new DmpPatcher();
        Assert.True(m.TryMatch("abcdef", "de", 3, out int at));
        Assert.Equal(3, at);
        Assert.False(m.TryMatch(string.Empty, "abcdef", 1, out at));
        Assert.False(new DmpPatcher { MatchThreshold = 0.0 }.TryMatch("abcdefghijk", "efxyhi", 5, out _));
        // A pattern longer than MatchMaxBits cannot be matched; that is a miss, not an exception.
        Assert.False(m.TryMatch("a", new string('a', 33), 0, out _));
        Assert.True(m.TryMatch("abc", string.Empty, 1, out at));
        Assert.Equal(1, at);
    }

    // ---- Docs ----------------------------------------------------------------------------------------------------

    [Fact]
    [Trait("Host", "Native")] // reads documentation files from the source tree
    public void PackageReadmeSnippetCompilesAndWorks()
    {
        string oldText = "a\nb\nc\n", newText = "a\nB\nc\n", baseText = oldText, ours = newText, theirs = "a\nb\nc\nd\n";

        // --- begin snippet package
        string patch = Diff.Unified(oldText, newText);
        string text = Diff.ApplyUnified(oldText, patch).Text;
        TextMerge m = Diff.Merge(baseText, ours, theirs);
        TextDiff d = Diff.Lines(oldText, newText, new DiffSettings { MaxWork = 100_000 });
        SideBySideDiff rows = Diff.SideBySide(oldText, newText);
        // --- end snippet package

        Assert.Equal(newText, text);
        Assert.False(m.HasConflicts);
        Assert.False(d.Diagnostic.CostCapReached);
        Assert.Equal(3, rows.Rows.Count);
        DocSnippet.AssertInPage("src/OneDiff/PACKAGE.md", "package");
    }

    [Fact]
    [Trait("Host", "Native")] // reads documentation files from the source tree
    public void RepositoryReadmeTextLinesCompile()
    {
        string oldText = "pay within thirty days", newText = "pay within forty days", baseText = "a\nb\nc\nd\n", ours = "A\nb\nc\nd\n", theirs = "a\nb\nc\nD\n";

        // --- begin snippet readme
        string marked = Diff.ToMarkdownInline(Diff.Words(oldText, newText));
        string patch = Diff.Unified(oldText, newText);
        TextMerge both = Diff.Merge(baseText, ours, theirs);
        // --- end snippet readme

        Assert.Contains("<ins>forty</ins>", marked, StringComparison.Ordinal);
        Assert.NotEmpty(patch);
        Assert.False(both.HasConflicts);
        DocSnippet.AssertInPage("README.md", "readme");
    }
}
