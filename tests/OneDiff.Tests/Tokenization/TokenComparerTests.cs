using OneDiff.Tokenization;

namespace OneDiff.Tests;

public class TokenComparerTests
{
    [Fact]
    public void IgnoreCaseAndWhiteSpaceRules()
    {
        Assert.True(TokenComparer.IgnoreCase.Equals("Hello", "hELLO"));
        Assert.False(TokenComparer.Ordinal.Equals("Hello", "hello"));
        Assert.True(TokenComparer.IgnoreWhiteSpaceChanges.Equals("  a \t b  \n", "a b"));
        Assert.False(TokenComparer.IgnoreWhiteSpaceChanges.Equals("a b", "ab"));
        Assert.True(TokenComparer.IgnoreAllWhiteSpace.Equals("a b", "ab"));
        Assert.True(TokenComparer.IgnoreAllWhiteSpace.Equals(" \n", "\t"));
        Assert.True(TokenComparer.Create(true, WhiteSpaceHandling.IgnoreChanges).Equals(" A  b", "a B "));
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenComparer.Create(false, (WhiteSpaceHandling)9));
    }

    [Fact]
    public void HashAgreesWithEquality()
    {
        TokenComparer[] comparers = [TokenComparer.IgnoreCase, TokenComparer.IgnoreWhiteSpaceChanges, TokenComparer.IgnoreAllWhiteSpace, TokenComparer.Create(true, WhiteSpaceHandling.IgnoreAll)];
        string[] samples = ["a b", "A  B", " ab ", "AB", "\ta\nb", "", "  ", "x", "X "];
        foreach (TokenComparer c in comparers)
        {
            foreach (string x in samples)
            {
                foreach (string y in samples)
                {
                    if (c.Equals(x, y))
                    {
                        Assert.Equal(c.GetHashCode(x), c.GetHashCode(y));
                    }
                }
            }
        }
    }

    [Fact]
    public void LinesIgnoringWhiteSpaceAndCaseDiffOnTheRealChange()
    {
        TextDiff d = Diff.LinesUsing("Alpha\n  beta  \ngamma\n", "alpha\nbeta\ndelta\n", TokenComparer.Create(true, WhiteSpaceHandling.IgnoreChanges));
        Assert.Equal(1, d.Script.DeletedCount);
        Assert.Equal(1, d.Script.InsertedCount);
        Assert.Equal("gamma\n", d.OldSpan(d.Script.Single(e => e.Kind == EditKind.Delete)).ToString());
        Assert.True(d.Script.Matches(d.OldTokens.ToArray().Select(r => d.OldText.Substring(r.Start, r.Length)).ToArray(),
            d.NewTokens.ToArray().Select(r => d.NewText.Substring(r.Start, r.Length)).ToArray(), TokenComparer.Create(true, WhiteSpaceHandling.IgnoreChanges)));
    }

    [Fact]
    public void AnyStringComparerWorksWithoutSpanLookup()
    {
        TextDiff d = Diff.WordsUsing("A b", "a B", StringComparer.OrdinalIgnoreCase);
        Assert.True(d.Script.IsIdentical);
    }
}
