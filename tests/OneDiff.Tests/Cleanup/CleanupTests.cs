using OneDiff.Cleanup;
using OneDiff.Tokenization;

namespace OneDiff.Tests;

public class CleanupTests
{
    // A minimal script by dynamic programming (small inputs only), so cleanup sees realistic, fragmented scripts whatever the
    // algorithms in the library do at this point.
    internal static TextDiff Minimal(string oldText, string newText, TokenKind kind)
    {
        TokenRange[] o = Tokenizer.Tokenize(oldText, kind);
        TokenRange[] n = Tokenizer.Tokenize(newText, kind);
        string[] os = o.Select(r => oldText.Substring(r.Start, r.Length)).ToArray();
        string[] ns = n.Select(r => newText.Substring(r.Start, r.Length)).ToArray();
        int[,] lcs = new int[os.Length + 1, ns.Length + 1];
        for (int i = os.Length - 1; i >= 0; i--)
        {
            for (int j = ns.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = os[i] == ns[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var b = new EditScriptBuilder();
        int a = 0, c = 0;
        while (a < os.Length || c < ns.Length)
        {
            if (a < os.Length && c < ns.Length && os[a] == ns[c]) { b.Equal(1); a++; c++; }
            else if (a < os.Length && (c == ns.Length || lcs[a + 1, c] >= lcs[a, c + 1])) { b.Delete(1); a++; }
            else { b.Insert(1); c++; }
        }

        return new TextDiff(oldText, newText, kind, o, n, b.ToEditScript());
    }

    private static string[] Strings(string text, ReadOnlySpan<TokenRange> ranges)
    {
        var result = new string[ranges.Length];
        for (int i = 0; i < ranges.Length; i++)
        {
            result[i] = text.Substring(ranges[i].Start, ranges[i].Length);
        }

        return result;
    }

    internal static void AssertValid(TextDiff d)
    {
        string[] o = Strings(d.OldText, d.OldTokens);
        string[] n = Strings(d.NewText, d.NewTokens);
        Assert.True(EditScript.TryValidate(d.Script.Edits, o.Length, n.Length, out string? error), error);
        Assert.True(d.Script.Matches<string>(o, n));
        Assert.Equal(d.NewText, string.Concat(d.Script.Apply<string>(o, n)));
    }

    private static string Render(TextDiff d)
    {
        var sb = new System.Text.StringBuilder();
        foreach (Edit e in d.Script)
        {
            sb.Append(e.Kind switch { EditKind.Equal => "=", EditKind.Delete => "-", _ => "+" });
            sb.Append('[').Append(e.Kind == EditKind.Insert ? d.NewSpan(e).ToString() : d.OldSpan(e).ToString()).Append(']');
        }

        return sb.ToString();
    }

    [Fact]
    public void SemanticMergesTrivialEqualitiesIntoOneReplacement()
    {
        TextDiff raw = Minimal("the quick brown fox", "the slow red dog", TokenKind.Word);
        TextDiff clean = DiffCleanup.Semantic(raw);
        AssertValid(clean);
        Assert.Equal("=[the ]-[quick brown fox]+[slow red dog]", Render(clean));
    }

    [Fact]
    public void SemanticKeepsLargeEqualities()
    {
        TextDiff raw = Minimal("alpha is a rather long shared phrase omega", "beta is a rather long shared phrase gamma", TokenKind.Word);
        Assert.Equal(Render(raw), Render(DiffCleanup.Semantic(raw)));
    }

    [Fact]
    public void SemanticFindsOverlaps()
    {
        // Delete "abcxxx", insert "xxxdef": the shared "xxx" stays an equality.
        TextDiff raw = Minimal("1 abcxxx 2", "1 xxxdef 2", TokenKind.Character);
        TextDiff clean = DiffCleanup.Semantic(raw);
        AssertValid(clean);
        Assert.Contains("=[xxx]", Render(clean));
        Assert.True(clean.Script.EditDistance <= raw.Script.EditDistance + 4);
    }

    [Fact]
    public void EfficiencyDropsSmallEqualitiesByCost()
    {
        // "xaybz" -> "XaYbZ"-style: single-character equalities between four-run regions go at cost 4, stay at cost 1.
        TextDiff raw = Minimal("ab12cd34ef", "AB12CD34EF", TokenKind.Character);
        AssertValid(raw);
        TextDiff cost4 = DiffCleanup.Efficiency(raw, 4);
        AssertValid(cost4);
        Assert.Equal("-[ab12cd34ef]+[AB12CD34EF]", Render(cost4));
        Assert.Equal(Render(raw), Render(DiffCleanup.Efficiency(raw, 1)));
        Assert.Same(raw, DiffCleanup.Efficiency(raw, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiffCleanup.Efficiency(raw, -1));
    }

    [Fact]
    public void LosslessShiftPicksWordBoundaries()
    {
        // "the cat" -> "the cat sat on the cat": the inserted text can be drawn several ways; the readable one starts at a word.
        TextDiff raw = Minimal("one two", "one two two", TokenKind.Character);
        TextDiff shifted = DiffCleanup.SemanticLosslessShift(raw);
        AssertValid(shifted);
        Assert.Equal(raw.Script.EditDistance, shifted.Script.EditDistance);
        Assert.Equal("=[one two]+[ two]", Render(shifted));

        TextDiff raw2 = Minimal("a\n\nb\n", "a\n\nb\n\nb\n", TokenKind.Character);
        TextDiff shifted2 = DiffCleanup.SemanticLosslessShift(raw2);
        AssertValid(shifted2);
        Assert.Equal(raw2.Script.EditDistance, shifted2.Script.EditDistance);
    }

    [Fact]
    public void ShiftJoinsAnEditThatCanSlideIntoTheNextChange()
    {
        // old Q b R, new b b R: delete Q, keep b, insert b. The insertion can slide left across the kept b and touch the deletion.
        var b = new EditScriptBuilder();
        b.Delete(1); b.Equal(1); b.Insert(1); b.Equal(1);
        TokenRange[] o = Tokenizer.Tokenize("QbR", TokenKind.Character), n = Tokenizer.Tokenize("bbR", TokenKind.Character);
        var raw = new TextDiff("QbR", "bbR", TokenKind.Character, o, n, b.ToEditScript());
        TextDiff joined = DiffCleanup.SemanticLosslessShift(raw);
        AssertValid(joined);
        Assert.Equal("-[Q]+[b]=[bR]", Render(joined));
        Assert.Equal(raw.Script.EditDistance, joined.Script.EditDistance);
        Assert.Equal("-[Q]+[b]=[bR]", Render(DiffCleanup.Efficiency(raw, 1)));
    }

    [Fact]
    public void ShiftDoesNotCutInsideAWordOrACombiningSequence()
    {
        TextDiff raw = Minimal("cat", "caat", TokenKind.Character);
        Assert.Equal(1, DiffCleanup.SemanticLosslessShift(raw).Script.InsertedCount);

        TextDiff withMarks = Minimal("xe\u0301", "xe\u0301e\u0301", TokenKind.Character);
        TextDiff shifted = DiffCleanup.SemanticLosslessShift(withMarks);
        AssertValid(shifted);
        foreach (Edit e in shifted.Script.Where(e => e.Kind == EditKind.Insert))
        {
            Assert.NotEqual('\u0301', shifted.NewSpan(e)[0]);
        }
    }

    [Theory]
    [InlineData(TokenKind.Character)]
    [InlineData(TokenKind.Word)]
    [InlineData(TokenKind.Line)]
    [InlineData(TokenKind.Grapheme)]
    public void RandomEditsStayValidAfterEveryCleanup(TokenKind kind)
    {
        var random = new Random(99);
        string[] pieces = ["alpha", " ", "beta", "\n", "gamma", "  ", "\r\n", ".", "e\u0301", "\u4F60", "\U0001F600", "x", "xx", "\n\n"];
        for (int round = 0; round < 400; round++)
        {
            string baseText = string.Concat(Enumerable.Range(0, random.Next(0, 25)).Select(_ => pieces[random.Next(pieces.Length)]));
            var edited = new System.Text.StringBuilder(baseText);
            for (int k = random.Next(0, 5); k > 0 && edited.Length > 0; k--)
            {
                int at = random.Next(edited.Length);
                if (random.Next(2) == 0)
                {
                    edited.Remove(at, Math.Min(random.Next(1, 6), edited.Length - at));
                }
                else
                {
                    edited.Insert(at, pieces[random.Next(pieces.Length)]);
                }
            }

            TextDiff raw = Minimal(baseText, edited.ToString(), kind);
            AssertValid(raw);
            AssertValid(DiffCleanup.Semantic(raw));
            AssertValid(DiffCleanup.Efficiency(raw, 1 + random.Next(8)));
            TextDiff shifted = DiffCleanup.SemanticLosslessShift(raw);
            AssertValid(shifted);
            Assert.Equal(raw.Script.EditDistance, shifted.Script.EditDistance);
        }
    }

    [Fact]
    public void CleanupHonoursTheComparerTheScriptWasBuiltWith()
    {
        TextDiff d = Diff.TextUsing("Foo foo foo", "foo foo foo foo", TokenComparer.IgnoreCase, TokenKind.Word);
        AssertValidUnder(DiffCleanup.SemanticLosslessShift(d), TokenComparer.IgnoreCase);
        AssertValidUnder(DiffCleanup.Semantic(d), TokenComparer.IgnoreCase);
    }

    private static void AssertValidUnder(TextDiff d, IEqualityComparer<string> comparer)
    {
        Assert.True(d.Script.Matches<string>(Strings(d.OldText, d.OldTokens), Strings(d.NewText, d.NewTokens), comparer));
    }

    [Fact]
    public void StaticsCleanUp()
    {
        TextDiff d = Diff.Semantic("the quick brown fox", "the quick brown fox jumps");
        AssertValid(d);
        Assert.Equal(d.NewText, string.Concat(d.Script.Apply<string>(Strings(d.OldText, d.OldTokens), Strings(d.NewText, d.NewTokens))));
        AssertValid(Diff.Efficient("abcdef", "abXdef"));
        Assert.Throws<ArgumentNullException>(() => DiffCleanup.Semantic(null!));
    }
}
