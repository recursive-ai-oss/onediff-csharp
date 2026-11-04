using OneDiff.Tokenization;

namespace OneDiff.Tests;

public class TrimEquivalenceTests
{
    public static TheoryData<DiffAlgorithm, TokenKind> Cases()
    {
        var data = new TheoryData<DiffAlgorithm, TokenKind>();
        foreach (DiffAlgorithm a in new[] { DiffAlgorithm.Myers, DiffAlgorithm.Patience, DiffAlgorithm.Histogram })
        {
            foreach (TokenKind k in new[] { TokenKind.Line, TokenKind.Word, TokenKind.Character, TokenKind.Grapheme })
            {
                data.Add(a, k);
            }
        }

        return data;
    }

    // Trimming the shared head and tail before interning must not change the script: compare with Diff.Sequences, which sees every token.
    [Theory]
    [MemberData(nameof(Cases))]
    public void TrimmedTextDiffEqualsTheUntrimmedScript(DiffAlgorithm algorithm, TokenKind kind)
    {
        string[] pieces = ["alpha", " ", "beta", "\n", "gamma", "\r\n", ".", "e\u0301", "\U0001F600", "x", "xx", "ab", "\u4F60"];
        var random = new Random(31);
        for (int round = 0; round < 500; round++)
        {
            string head = string.Concat(Enumerable.Range(0, random.Next(0, 12)).Select(_ => pieces[random.Next(pieces.Length)]));
            string tail = string.Concat(Enumerable.Range(0, random.Next(0, 12)).Select(_ => pieces[random.Next(pieces.Length)]));
            string midOld = string.Concat(Enumerable.Range(0, random.Next(0, 8)).Select(_ => pieces[random.Next(pieces.Length)]));
            string midNew = string.Concat(Enumerable.Range(0, random.Next(0, 8)).Select(_ => pieces[random.Next(pieces.Length)]));
            string o = head + midOld + tail, n = head + midNew + tail;

            TextDiff d = Diff.Text(o, n, kind, algorithm);
            string[] os = Tokenizer.Tokenize(o, kind).Select(r => o.Substring(r.Start, r.Length)).ToArray();
            string[] ns = Tokenizer.Tokenize(n, kind).Select(r => n.Substring(r.Start, r.Length)).ToArray();
            // Characters use code points as ids (as Diff.Text does); the algorithms' tie-breaking can depend on how sparse the ids are, so the
            // reference must use the same ids. Other kinds compare strings through dense ids.
            EditScript expected = kind == TokenKind.Character ? ByCodePoints(o, n, algorithm) : Diff.Sequences<string>(os, ns, algorithm);
            Assert.True(expected.Edits.ToArray().SequenceEqual(d.Script.Edits.ToArray()), $"{kind} {algorithm}: {Escape(o)} => {Escape(n)}\nexpected {string.Join(" ", expected.Edits.ToArray().Select(e => e.Kind.ToString()[0] + "" + (e.Kind == EditKind.Insert ? e.NewLength : e.OldLength)))}\nactual   {string.Join(" ", d.Script.Edits.ToArray().Select(e => e.Kind.ToString()[0] + "" + (e.Kind == EditKind.Insert ? e.NewLength : e.OldLength)))}");
            Assert.True(d.Script.Matches<string>(os, ns));
            Assert.Equal(os.Length, d.OldTokens.Length);
        }
    }

    [Fact]
    public void CharacterTrimHandlesSurrogatesOverlapsAndIdentity()
    {
        Assert.True(Diff.Characters("same", "same").Script.IsIdentical);
        Assert.Equal(3, Diff.Characters("", "abc").Script.InsertedCount);
        // The shared head and tail may overlap in a repeated run: "aaa" vs "aaaa" is one insertion.
        TextDiff rep = Diff.Characters("aaa", "aaaa");
        Assert.Equal(1, rep.Script.EditDistance);
        // A pair is never cut: the shared high surrogate must not be split from its low one.
        TextDiff emoji = Diff.Characters("a\U0001F600b", "a\U0001F601b");
        Assert.Equal(2, emoji.Script.EditDistance);
        Assert.Equal("\U0001F600", emoji.OldSpan(emoji.Script.Single(e => e.Kind == EditKind.Delete)).ToString());
    }

    private static EditScript ByCodePoints(string o, string n, DiffAlgorithm algorithm)
    {
        int[] a = o.EnumerateRunes().Select(r => r.Value).ToArray();
        int[] b = n.EnumerateRunes().Select(r => r.Value).ToArray();
        return algorithm switch
        {
            DiffAlgorithm.Myers => OneDiff.Algorithms.MyersDiff.Compute(a, b),
            DiffAlgorithm.Patience => OneDiff.Algorithms.PatienceDiff.Compute(a, b),
            _ => OneDiff.Algorithms.HistogramDiff.Compute(a, b),
        };
    }

    private static string Escape(string s) => string.Concat(s.Select(c => c < 0x20 || c > 0x7E ? $"\\u{(int)c:X4}" : c.ToString()));
}
