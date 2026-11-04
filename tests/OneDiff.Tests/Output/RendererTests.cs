using System.Text.RegularExpressions;
using OneDiff.Output;
using OneDiff.Tokenization;

namespace OneDiff.Tests.Output;

public sealed partial class RendererTests
{
    private const string Esc = "\u001b";

    private static TerminalRenderer Plain(int context = 3) => new() { Color = false, ContextLines = context };

    [Fact]
    public void PlainTerminalLineDiff()
    {
        TextDiff d = Diff.Lines("a\nb\nc\n", "a\nB\nc\n");
        Assert.Equal("@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n", Plain(1).Render(d));
        Assert.Equal("@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n", Diff.ToTerminal(d, color: false));
    }

    [Fact]
    public void ColouredTerminalLineDiff()
    {
        TextDiff d = Diff.Lines("a\nb\nc\n", "a\nB\nc\n");
        string expected =
            Esc + "[36m@@ -1,3 +1,3 @@" + Esc + "[0m\n" +
            " a\n" +
            Esc + "[31m-b" + Esc + "[0m\n" +
            Esc + "[32m+B" + Esc + "[0m\n" +
            " c\n";
        Assert.Equal(expected, new TerminalRenderer { ContextLines = 1 }.Render(d));
    }

    [Fact]
    public void WordLevelHighlightsOnlyTheChangedWords()
    {
        TextDiff d = Diff.Lines("the quick brown fox\n", "the quick red fox\n");
        string expected =
            Esc + "[36m@@ -1 +1 @@" + Esc + "[0m\n" +
            Esc + "[31m-the quick " + Esc + "[7mbrown" + Esc + "[27m fox" + Esc + "[0m\n" +
            Esc + "[32m+the quick " + Esc + "[7mred" + Esc + "[27m fox" + Esc + "[0m\n";
        Assert.Equal(expected, Diff.ToTerminal(d));

        string off = new TerminalRenderer { WordLevel = false }.Render(d);
        Assert.DoesNotContain(Esc + "[7m", off, StringComparison.Ordinal);
    }

    [Fact]
    public void WordLevelAcrossSeveralLines()
    {
        TextDiff d = Diff.Lines("aaa bbb\nccc ddd\n", "aaa XXX\nYYY ddd\n");
        string s = Diff.ToTerminal(d);
        Assert.Equal(4, Regex.Count(s, Regex.Escape(Esc + "[7m")));
        Assert.Contains(Esc + "[31m-aaa " + Esc + "[7mbbb" + Esc + "[27m" + Esc + "[0m\n", s, StringComparison.Ordinal);
    }

    [Fact]
    public void RewrittenLinesAreNotHighlighted()
    {
        string s = Diff.ToTerminal(Diff.Lines("alpha beta\n", "gamma delta\n"));
        Assert.DoesNotContain(Esc + "[7m", s, StringComparison.Ordinal);
    }

    [Fact]
    public void CrLfLinesShowWithoutTheCarriageReturn()
    {
        TextDiff d = Diff.Lines("a\r\nb\r\n", "a\r\nc\r\n");
        Assert.Equal("@@ -1,2 +1,2 @@\n a\n-b\n+c\n", Plain().Render(d));
        Assert.Equal("```diff\n@@ -1,2 +1,2 @@\n a\n-b\n+c\n```\n", new MarkdownRenderer().RenderLines(d));
    }

    [Fact]
    public void MissingFinalNewlineIsMarked()
    {
        TextDiff d = Diff.Lines("a\nb", "a\nb\n");
        Assert.Equal("@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+b\n", Plain().Render(d));
        Assert.Equal("```diff\n@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+b\n```\n", Diff.ToMarkdown(d));

        string coloured = Diff.ToTerminal(d);
        Assert.Contains(Esc + "[2m\\ No newline at end of file" + Esc + "[0m\n", coloured, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySides()
    {
        Assert.Equal("@@ -0,0 +1 @@\n+x\n", Plain().Render(Diff.Lines("", "x\n")));
        Assert.Equal("@@ -1 +0,0 @@\n-x\n", Plain().Render(Diff.Lines("x\n", "")));
        Assert.Equal(string.Empty, Plain().Render(Diff.Lines("", "")));
        Assert.Equal(string.Empty, Diff.ToTerminal(Diff.Lines("same\n", "same\n")));
        Assert.Equal(string.Empty, Diff.ToMarkdown(Diff.Lines("", "")));
        Assert.Equal(string.Empty, Diff.ToMarkdownInline(Diff.Words("", "")));
    }

    [Fact]
    public void WideUnicodePassesThrough()
    {
        TextDiff d = Diff.Lines("日本語 \U0001F600 cafe\u0301\n", "日本語 \U0001F601 cafe\u0301\n");
        Assert.Equal("@@ -1 +1 @@\n-日本語 \U0001F600 cafe\u0301\n+日本語 \U0001F601 cafe\u0301\n", Plain().Render(d));

        // Colour adds sequences only; stripping them gives the plain text back.
        string stripped = AnsiPattern().Replace(Diff.ToTerminal(d), string.Empty);
        Assert.Equal(Plain().Render(d), stripped);
    }

    [Fact]
    public void ControlCharactersCannotDriveTheTerminal()
    {
        TextDiff d = Diff.Lines("a\u001b[31mb\n", "a\u009bc\n");
        string plain = Plain().Render(d);
        Assert.Equal("@@ -1 +1 @@\n-a\u241b[31mb\n+a\uFFFDc\n", plain);
    }

    [Fact]
    public void ContextSplitsDistantChangesIntoHunks()
    {
        string[] oldLines = Enumerable.Range(1, 20).Select(i => "l" + i).ToArray();
        string[] newLines = (string[])oldLines.Clone();
        newLines[1] = "X2";
        newLines[17] = "X18";
        string o = string.Join('\n', oldLines) + "\n", n = string.Join('\n', newLines) + "\n";
        const string Expected =
            "@@ -1,3 +1,3 @@\n l1\n-l2\n+X2\n l3\n" +
            "@@ -17,3 +17,3 @@\n l17\n-l18\n+X18\n l19\n";
        Assert.Equal(Expected, Plain(1).Render(Scripted(o, n)));

        // With enough context the two changes share one hunk and no line repeats.
        string merged = Plain(9).Render(Scripted(o, n));
        Assert.StartsWith("@@ -1,20 +1,20 @@\n", merged, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(merged, "@@ -"));

        // No context: bare changes.
        Assert.Equal("@@ -2 +2 @@\n-l2\n+X2\n@@ -18 +18 @@\n-l18\n+X18\n", Plain(0).Render(Scripted(o, n)));
    }

    // The two single-line replacements at lines 2 and 18, spelled out so the hunking is tested apart from any algorithm.
    private static TextDiff Scripted(string o, string n)
    {
        TokenRange[] ot = Tokenizer.Tokenize(o, TokenKind.Line), nt = Tokenizer.Tokenize(n, TokenKind.Line);
        Edit[] edits =
        [
            Edit.Equal(0, 0, 1), Edit.Delete(1, 1, 1), Edit.Insert(2, 1, 1), Edit.Equal(2, 2, 15),
            Edit.Delete(17, 1, 17), Edit.Insert(18, 17, 1), Edit.Equal(18, 18, 2),
        ];
        return new TextDiff(o, n, TokenKind.Line, ot, nt, EditScript.Create(edits, ot.Length, nt.Length));
    }

    [Fact]
    public void FileHeaders()
    {
        TextDiff d = Diff.Lines("a\n", "b\n");
        Assert.Equal("--- old.txt\n+++ new.txt\n@@ -1 +1 @@\n-a\n+b\n", (Plain() with { OldName = "old.txt", NewName = "new.txt" }).Render(d));
        Assert.Equal("```diff\n--- old.txt\n+++ new.txt\n@@ -1 +1 @@\n-a\n+b\n```\n", new MarkdownRenderer { OldName = "old.txt", NewName = "new.txt" }.RenderLines(d));
    }

    [Fact]
    public void MarkdownFenceOutgrowsBackticksInContent()
    {
        TextDiff d = Diff.Lines("```\nx\n", "```\ny\n");
        string md = Diff.ToMarkdown(d);
        Assert.StartsWith("````diff\n", md, StringComparison.Ordinal);
        Assert.EndsWith("\n````\n", md, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownInline()
    {
        Assert.Equal("the quick <del>brown</del><ins>red</ins> fox", Diff.ToMarkdownInline(Diff.Words("the quick brown fox", "the quick red fox")));
        Assert.Equal("a<del>&lt;</del><ins>&amp;</ins>b", Diff.ToMarkdownInline(Diff.Characters("a<b", "a&b")));

        // A deletion that spans lines is wrapped per line, with the breaks outside the tags.
        string multi = Diff.ToMarkdownInline(Diff.Characters("x\r\nyy\r\nz", "xz"));
        Assert.Equal("x<del>yy</del>z", multi.Replace("\r\n", string.Empty, StringComparison.Ordinal).Replace("<del></del>", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain("<del>\r", multi, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputIsRepeatableAndWriterMatchesString()
    {
        TextDiff d = Diff.Lines("one\ntwo\nthree\nfour\n", "one\n2\nthree\nfour\nfive\n");
        var r = new TerminalRenderer();
        string first = r.Render(d);
        Assert.Equal(first, r.Render(Diff.Lines(d.OldText, d.NewText)));
        using var w = new StringWriter();
        r.Render(d, w);
        Assert.Equal(first, w.ToString());
        Assert.Equal(new MarkdownRenderer().RenderLines(d), new MarkdownRenderer().RenderLines(Diff.Lines(d.OldText, d.NewText)));
    }

    [Fact]
    public void Guards()
    {
        TextDiff words = Diff.Words("a b", "a c");
        Assert.Throws<ArgumentException>(() => Diff.ToTerminal(words));
        Assert.Throws<ArgumentException>(() => Diff.ToMarkdown(words));
        Assert.Throws<ArgumentNullException>(() => Diff.ToTerminal(null!));
        Assert.Throws<ArgumentNullException>(() => new TerminalRenderer().Render(Diff.Lines("a", "b"), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerminalRenderer { ContextLines = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownRenderer { ContextLines = -1 });
    }

    [GeneratedRegex("\u001b\\[[0-9;]*m")]
    private static partial Regex AnsiPattern();
}
