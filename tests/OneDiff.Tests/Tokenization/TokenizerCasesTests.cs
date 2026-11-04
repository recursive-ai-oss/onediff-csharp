using System.Text;
using OneDiff.Tokenization;

namespace OneDiff.Tests;

public class TokenizerCasesTests
{
    private static string[] Pieces(string text, TokenKind kind)
    {
        TokenRange[] ranges = Tokenizer.Tokenize(text, kind);
        Assert.Equal(text, string.Concat(ranges.Select(r => text.Substring(r.Start, r.Length))));
        return ranges.Select(r => text.Substring(r.Start, r.Length)).ToArray();
    }

    [Fact]
    public void LinesHandleMixedTerminatorsAndMissingFinalNewline()
    {
        Assert.Equal(["a\r\n", "b\n", "c\r", "\r\n", "d"], Pieces("a\r\nb\nc\r\r\nd", TokenKind.Line));
        Assert.Equal(["\r", "\r"], Pieces("\r\r", TokenKind.Line));
        Assert.Equal(["x\r"], Pieces("x\r", TokenKind.Line));
        Assert.Equal(["no newline"], Pieces("no newline", TokenKind.Line));
    }

    [Fact]
    public void GraphemesKeepEmojiSequencesFlagsAndMarksTogether()
    {
        string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467"; // man ZWJ woman ZWJ girl
        string wave = "\U0001F44B\U0001F3FD";                         // waving hand + skin tone
        string flag = "\U0001F1EB\U0001F1F7";                         // regional indicators F R
        string heart = "\u2764\uFE0F";                                // heart + variation selector
        string keycap = "1\uFE0F\u20E3";
        Assert.Equal(["a", family, wave, flag, heart, keycap, "b"], Pieces("a" + family + wave + flag + heart + keycap + "b", TokenKind.Grapheme));
        Assert.Equal(["\U0001F1EB\U0001F1F7", "\U0001F1E9\U0001F1EA"], Pieces("\U0001F1EB\U0001F1F7\U0001F1E9\U0001F1EA", TokenKind.Grapheme));
        Assert.Equal(["e\u0301\u0323", "n"], Pieces("e\u0301\u0323n", TokenKind.Grapheme));
        Assert.Equal(["\r\n", "a"], Pieces("\r\na", TokenKind.Grapheme));
        // Hangul jamo L+V+T compose one syllable block.
        Assert.Equal(["\u1112\u1161\u11AB", "\uD55C"], Pieces("\u1112\u1161\u11AB\uD55C", TokenKind.Grapheme));
        // Devanagari: the framework's cluster rules (UAX #29 before the Indic conjunct rule GB9c) end a cluster after a virama, so a conjunct
        // splits into consonant+virama and the next consonant with its vowel sign. Word tokens keep the whole conjunct (see WordsSplitScriptsSensibly).
        Assert.Equal(["\u0915\u094D", "\u0937\u093F", "x"], Pieces("\u0915\u094D\u0937\u093Fx", TokenKind.Grapheme));
    }

    [Fact]
    public void CharactersAreScalars()
    {
        Assert.Equal(["e", "\u0301", "\U0001F468", "\u200D", "\U0001F469"], Pieces("e\u0301\U0001F468\u200D\U0001F469", TokenKind.Character));
    }

    [Fact]
    public void WordsSplitScriptsSensibly()
    {
        Assert.Equal(["Hello", ",", " ", "world", "!"], Pieces("Hello, world!", TokenKind.Word));
        Assert.Equal(["don't", " ", "stop", "'"], Pieces("don't stop'", TokenKind.Word));
        Assert.Equal(["3.14", " ", "and", " ", "1,000", ".", " ", "x", "."], Pieces("3.14 and 1,000. x.", TokenKind.Word));
        // CJK ideographs and Hiragana one by one; Katakana and Hangul as runs.
        Assert.Equal(["\u4F60", "\u597D", "\u4E16", "\u754C"], Pieces("\u4F60\u597D\u4E16\u754C", TokenKind.Word));
        Assert.Equal(["\u3053", "\u3093", "\u30AB\u30BF\u30AB\u30CA", " ", "\uD55C\uAD6D\uC5B4"], Pieces("\u3053\u3093\u30AB\u30BF\u30AB\u30CA \uD55C\uAD6D\uC5B4", TokenKind.Word));
        // Latin next to Han splits; digits stay with letters.
        Assert.Equal(["abc", "\u4E2D", "d1"], Pieces("abc\u4E2Dd1", TokenKind.Word));
        // Thai (no spaces) and Arabic with marks stay as runs.
        Assert.Equal(["\u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35", " ", "\u0E04\u0E23\u0E31\u0E1A"], Pieces("\u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35 \u0E04\u0E23\u0E31\u0E1A", TokenKind.Word));
        Assert.Equal(["\u0645\u064E\u0631\u0652\u062D\u064E\u0628\u064B\u0627", "\u060C", " ", "\u0639\u0627\u0644\u0645"], Pieces("\u0645\u064E\u0631\u0652\u062D\u064E\u0628\u064B\u0627\u060C \u0639\u0627\u0644\u0645", TokenKind.Word));
        // Devanagari conjunct stays in its word; combining accents stay on their letter.
        Assert.Equal(["\u0915\u094D\u0937\u093F\u092F", " ", "cafe\u0301"], Pieces("\u0915\u094D\u0937\u093F\u092F cafe\u0301", TokenKind.Word));
    }

    [Fact]
    public void WordsKeepEmojiSequencesWholeAndSplitWhiteSpace()
    {
        string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        Assert.Equal(["a", family, "\U0001F1EB\U0001F1F7", "b"], Pieces("a" + family + "\U0001F1EB\U0001F1F7" + "b", TokenKind.Word));
        Assert.Equal(["a", " \t\r\n ", "b", "\u200B", "c"], Pieces("a \t\r\n b\u200Bc", TokenKind.Word));
        Assert.Equal(["\uD83D", "x"], Pieces("\uD83Dx", TokenKind.Word)); // lone surrogate
    }

    [Theory]
    [InlineData(TokenKind.Line)]
    [InlineData(TokenKind.Word)]
    [InlineData(TokenKind.Character)]
    [InlineData(TokenKind.Grapheme)]
    public void RandomMixedScriptTextPartitions(TokenKind kind)
    {
        string[] atoms =
        [
            "a", "Z", "7", "_", " ", "\t", "\n", "\r", "\r\n", "'", ".", ",", "\u2019", "-", "\u00E9", "e\u0301", "\u0301", "\u200D", "\u200C", "\u200B",
            "\u4F60", "\u3053", "\u30AB", "\uD55C", "\u1112", "\u1161", "\u11AB", "\u0915", "\u094D", "\u0937", "\u0E2A", "\u0E31", "\u0645", "\u064E",
            "\U0001F468", "\U0001F3FD", "\U0001F1EB", "\U0001F1F7", "\uFE0F", "\u20E3", "\uD83D", "\uDE00", "\u0600", "\uFEFF", "\u0000", "\u00AD", "\u2028",
        ];
        var random = new Random(20261104);
        for (int round = 0; round < 3000; round++)
        {
            var sb = new StringBuilder();
            int n = random.Next(0, 30);
            for (int i = 0; i < n; i++)
            {
                sb.Append(atoms[random.Next(atoms.Length)]);
            }

            string text = sb.ToString();
            int pos = 0;
            foreach (TokenRange t in Tokenizer.Tokenize(text, kind))
            {
                Assert.Equal(pos, t.Start);
                Assert.True(t.Length > 0, $"empty token in {Escape(text)}");
                pos = t.End;
            }

            Assert.Equal(text.Length, pos);
        }
    }

    [Fact]
    public void GraphemeFastPathAgreesWithTheFrameworkRules()
    {
        // The ASCII shortcut must produce the same cut as StringInfo alone on any mix of ASCII and the characters that could join.
        string[] atoms = ["a", "b", " ", "\r", "\n", "\u0301", "\u200D", "\uFE0F", "\u00E9", "\u0903", "\u0600", "\U0001F600", "\u1100", "\u1161"];
        var random = new Random(5);
        for (int round = 0; round < 2000; round++)
        {
            string text = string.Concat(Enumerable.Range(0, random.Next(0, 20)).Select(_ => atoms[random.Next(atoms.Length)]));
            var expected = new List<int>();
            for (int i = 0; i < text.Length; i += System.Globalization.StringInfo.GetNextTextElementLength(text.AsSpan(i)))
            {
                expected.Add(i);
            }

            Assert.Equal(expected, Tokenizer.Tokenize(text, TokenKind.Grapheme).Select(r => r.Start).ToList());
        }
    }

    private static string Escape(string s) => string.Concat(s.Select(c => c < 0x20 || c > 0x7E ? $"\\u{(int)c:X4}" : c.ToString()));
}
