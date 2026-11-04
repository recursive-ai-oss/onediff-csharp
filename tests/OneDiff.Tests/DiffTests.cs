using OneDiff.Tokenization;

namespace OneDiff.Tests;

public class DiffTests
{
    public static TheoryData<DiffAlgorithm> Algorithms => new() { DiffAlgorithm.Myers, DiffAlgorithm.Patience, DiffAlgorithm.Histogram };

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void RandomSequencesRoundTrip(DiffAlgorithm algorithm)
    {
        var random = new Random(42);
        for (int round = 0; round < 300; round++)
        {
            int[] a = Enumerable.Range(0, random.Next(0, 30)).Select(_ => random.Next(4)).ToArray();
            int[] b = Enumerable.Range(0, random.Next(0, 30)).Select(_ => random.Next(4)).ToArray();

            EditScript s = Diff.Sequences<int>(a, b, algorithm);

            Assert.True(EditScript.TryValidate(s.Edits, a.Length, b.Length, out string? error), error);
            Assert.True(s.Matches<int>(a, b));
            Assert.Equal(b, s.Apply<int>(a, b));
            Assert.Equal(a, s.Unapply<int>(a, b));
        }
    }

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void EdgeCases(DiffAlgorithm algorithm)
    {
        Assert.True(Diff.Sequences<string>([], [], algorithm).IsIdentical);
        EditScript insertOnly = Diff.Sequences<string>([], ["a"], algorithm);
        Assert.Equal([Edit.Insert(0, 0, 1)], insertOnly.Edits.ToArray());
        EditScript deleteOnly = Diff.Sequences<string>(["a"], [], algorithm);
        Assert.Equal([Edit.Delete(0, 1, 0)], deleteOnly.Edits.ToArray());
        Assert.True(Diff.Sequences<string>(["a", "b"], ["a", "b"], algorithm).IsIdentical);
    }

    [Fact]
    public void ComparerIsHonoured()
    {
        EditScript s = Diff.Sequences<string>(["A", "b"], ["a", "B"], comparer: StringComparer.OrdinalIgnoreCase);
        Assert.True(s.IsIdentical);
    }

    [Fact]
    public void TextDiffMapsEditsBackToText()
    {
        TextDiff d = Diff.Lines("one\ntwo\nthree\n", "one\n2\nthree\nfour\n");
        Assert.Equal(TokenKind.Line, d.Kind);

        string oldRebuilt = "", newRebuilt = "";
        foreach (Edit e in d.Script)
        {
            if (e.Kind != EditKind.Insert)
            {
                oldRebuilt += d.OldSpan(e).ToString();
            }

            if (e.Kind != EditKind.Delete)
            {
                newRebuilt += d.NewSpan(e).ToString();
            }
        }

        Assert.Equal(d.OldText, oldRebuilt);
        Assert.Equal(d.NewText, newRebuilt);
    }

    [Theory]
    [InlineData(TokenKind.Line)]
    [InlineData(TokenKind.Word)]
    [InlineData(TokenKind.Character)]
    [InlineData(TokenKind.Grapheme)]
    public void TextDiffReplaysForEveryKind(TokenKind kind)
    {
        const string a = "The cafe\u0301 sold \U0001F600 tea.\r\nOpen daily.\n";
        const string b = "The cafe\u0301 sold \U0001F600 coffee.\r\nOpen daily!\n";
        TextDiff d = Diff.Text(a, b, kind);
        Assert.False(d.Script.IsIdentical);
        Assert.Equal(d.OldTokens.Length, d.Script.OldLength);
        Assert.Equal(d.NewTokens.Length, d.Script.NewLength);
        Assert.True(Diff.Text(a, a, kind).Script.IsIdentical);
    }

    [Fact]
    public void Guards()
    {
        Assert.Throws<ArgumentNullException>(() => Diff.Text(null!, ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => Diff.Sequences<int>([1], [2], (DiffAlgorithm)9));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Diff.Sequences<int>([1], [2], DiffAlgorithm.Myers, null, cts.Token));
    }
}
