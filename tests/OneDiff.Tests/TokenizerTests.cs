using OneDiff.Tokenization;

namespace OneDiff.Tests;

public class TokenizerTests
{
    private static string[] Pieces(string text, TokenKind kind) =>
        Tokenizer.Tokenize(text, kind).Select(r => text.Substring(r.Start, r.Length)).ToArray();

    [Fact]
    public void LinesKeepTheirTerminators()
    {
        Assert.Equal(["a\n", "b\r\n", "c\r\n", "d"], Pieces("a\nb\r\nc\r\nd", TokenKind.Line));
        Assert.Equal(["a\n", "b\r\n", "c\r", "d"], Pieces("a\nb\r\nc\rd", TokenKind.Line));
        Assert.Equal(["a\n"], Pieces("a\n", TokenKind.Line));
        Assert.Equal(["\n", "\n"], Pieces("\n\n", TokenKind.Line));
    }

    [Fact]
    public void WordsSplitIntoWordSpaceAndSymbolRuns()
    {
        Assert.Equal(["foo_1", " \t", "+", "bar", "!", "!"], Pieces("foo_1 \t+bar!!", TokenKind.Word));
    }

    [Fact]
    public void CharactersKeepSurrogatePairsTogether()
    {
        Assert.Equal(["a", "\U0001F600", "b"], Pieces("a\U0001F600b", TokenKind.Character));
        Assert.Equal(["\uD83D", "x"], Pieces("\uD83Dx", TokenKind.Character));
    }

    [Fact]
    public void GraphemesKeepClustersTogether()
    {
        Assert.Equal(["e\u0301", "x"], Pieces("e\u0301x", TokenKind.Grapheme));
    }

    [Theory]
    [InlineData(TokenKind.Line)]
    [InlineData(TokenKind.Word)]
    [InlineData(TokenKind.Character)]
    [InlineData(TokenKind.Grapheme)]
    public void TokensPartitionTheText(TokenKind kind)
    {
        Assert.Empty(Tokenizer.Tokenize("", kind));

        var random = new Random(7);
        const string alphabet = "ab _\n\r\t.\u00e9\u0301\U0001F600\uD83D";
        for (int round = 0; round < 200; round++)
        {
            string text = new(Enumerable.Range(0, random.Next(0, 40)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            TokenRange[] tokens = Tokenizer.Tokenize(text, kind);
            int pos = 0;
            foreach (TokenRange t in tokens)
            {
                Assert.Equal(pos, t.Start);
                Assert.True(t.Length > 0);
                pos = t.End;
            }

            Assert.Equal(text.Length, pos);
        }
    }

    [Fact]
    public void AppendsToTheDestination()
    {
        var list = new List<TokenRange> { new(0, 0) };
        Tokenizer.Tokenize("ab", TokenKind.Character, list);
        Assert.Equal(3, list.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => Tokenizer.Tokenize("x", (TokenKind)99));
    }
}
