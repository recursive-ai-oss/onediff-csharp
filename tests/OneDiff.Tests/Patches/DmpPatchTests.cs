using System.Text;
using OneDiff.Patching;

namespace OneDiff.Tests.Patches;

public class DmpPatchTests
{
    private static readonly DmpPatcher Patcher = new();

    [Fact]
    public void RoundTripsText()
    {
        string a = "The quick brown fox jumps over the lazy dog.";
        string b = "That quick brown fox jumped over a lazy dog.";
        IReadOnlyList<DmpPatch> patches = Patcher.Make(a, b);
        string text = DmpPatcher.ToText(patches);
        Assert.StartsWith("@@ -1,", text);
        DmpApplyResult r = Patcher.Apply(DmpPatcher.FromText(text), a);
        Assert.Equal(b, r.Text);
        Assert.True(r.Success);
        Assert.Equal(text, DmpPatcher.ToText(DmpPatcher.FromText(text)));
    }

    [Fact]
    public void HeaderCoordinatesFollowTheWikiRules()
    {
        // A length of one drops the count, a length of zero keeps the start as is.
        string t = DmpPatcher.ToText(Patcher.Make("abcdefghij", "abcdefghijk"));
        Assert.Equal("@@ -3,8 +3,9 @@\n cdefghij\n+k\n", t);
        string p = DmpPatcher.ToText(new DmpPatcher { PatchMargin = 0 }.Make("abc", "abXc"));
        Assert.Equal("@@ -2,0 +3 @@\n+X\n", p);
        string q = DmpPatcher.ToText(new DmpPatcher { PatchMargin = 0 }.Make("abc", "ac"));
        Assert.Equal("@@ -2 +1,0 @@\n-b\n", q);
    }

    [Fact]
    public void EncodesLikeAUriButKeepsSpaces()
    {
        var sb = new StringBuilder();
        DmpText.Encode(sb, "a b\n%\u00e9\U0001F600-_.!~*'();/?:@&=+$,#");
        Assert.Equal("a b%0A%25%C3%A9%F0%9F%98%80-_.!~*'();/?:@&=+$,#", sb.ToString());
        Assert.True(DmpText.TryDecode(sb.ToString(), out string back));
        Assert.Equal("a b\n%\u00e9\U0001F600-_.!~*'();/?:@&=+$,#", back);
    }

    [Fact]
    public void CodePointOffsetsCountPairsOnce()
    {
        string emoji = string.Concat(Enumerable.Repeat("\U0001F600", 700)); // 1400 UTF-16 units, 700 code points
        string a = emoji + " the quick brown fox jumps over the lazy dog and keeps running far away";
        string b = emoji + " the quick brown fox leaps over the lazy dog and keeps running far away";
        var cp = new DmpPatcher { OffsetUnit = DmpOffsetUnit.CodePoint };
        IReadOnlyList<DmpPatch> units = Patcher.Make(a, ReferenceDmp.Diffs(a, b));
        IReadOnlyList<DmpPatch> points = cp.Make(a, ReferenceDmp.Diffs(a, b));
        Assert.Equal(units[0].Start2 - 700, points[0].Start2);
        // Both read the same text; the code point patch applies even where the unit offset would be 700 characters off.
        Assert.Equal(b, cp.Apply(DmpPatcher.FromText(DmpPatcher.ToText(points)), a).Text);
        Assert.Equal(b, Patcher.Apply(units, a).Text);
        DmpApplyResult wrongUnit = Patcher.Apply(points, a);
        Assert.True(!wrongUnit.Success || wrongUnit.Text != b);
        // Random round trips in both units.
        var random = new Random(3);
        for (int i = 0; i < 100; i++)
        {
            string x = RandomString(random, 300);
            string y = MutateString(random, x);
            Assert.Equal(y, cp.Apply(cp.Make(x, y), x).Text);
        }
    }

    [Fact]
    public void LoneSurrogatesSurviveTheTextForm()
    {
        string a = "x\uD800y";
        string b = "x\uDC00y!";
        string t = DmpPatcher.ToText(Patcher.Make(a, b));
        Assert.Equal(b, Patcher.Apply(DmpPatcher.FromText(t), a).Text);
    }

    [Fact]
    public void NeverSplitsASurrogatePair()
    {
        string a = "\U0001F600\U0001F601\U0001F602";
        string b = "\U0001F600\U0001F4A9\U0001F602";
        foreach (DmpPatch p in Patcher.Make(a, b))
        {
            foreach (DmpDiff d in p.Diffs)
            {
                Assert.False(d.Text.Length > 0 && (char.IsLowSurrogate(d.Text[0]) || char.IsHighSurrogate(d.Text[^1])));
            }
        }
    }

    [Fact]
    public void RandomPairsRoundTrip()
    {
        var random = new Random(11);
        for (int round = 0; round < 300; round++)
        {
            string a = RandomString(random, 300);
            string b = MutateString(random, a);
            IReadOnlyList<DmpPatch> patches = Patcher.Make(a, b);
            string text = DmpPatcher.ToText(patches);
            DmpApplyResult r = Patcher.Apply(DmpPatcher.FromText(text), a);
            Assert.True(r.Success, $"round {round}");
            Assert.Equal(b, r.Text);
            Assert.Equal(text, DmpPatcher.ToText(DmpPatcher.FromText(text)));
        }
    }

    [Fact]
    public void LongTextsWithLongDeletionsRoundTrip()
    {
        var random = new Random(5);
        string a = RandomString(random, 3000) + RandomString(random, 3000) + new string('q', 1500);
        string b = a.Remove(500, 900).Insert(100, "inserted text here") + "tail";
        DmpApplyResult r = Patcher.Apply(Patcher.Make(a, b), a);
        Assert.True(r.Success);
        Assert.Equal(b, r.Text);
    }

    [Fact]
    public void AppliesToDriftedText()
    {
        string a = "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs. How vexingly quick daft zebras jump!";
        string b = "The quick brown fox leaps over the lazy dog. Pack my box with five dozen liquor jugs. How vexingly quick daft zebras leap!";
        IReadOnlyList<DmpPatch> patches = Patcher.Make(a, ReferenceDmp.Diffs(a, b));
        string drifted = "Preface text added at the front. " + a.Replace("lazy", "sleepy", StringComparison.Ordinal);
        DmpApplyResult r = Patcher.Apply(patches, drifted);
        Assert.True(r.Success);
        Assert.Equal("Preface text added at the front. " + b.Replace("lazy", "sleepy", StringComparison.Ordinal), r.Text);
    }

    [Fact]
    public void ReportsPatchesThatCannotBePlaced()
    {
        string a = "alpha beta gamma delta epsilon zeta eta theta";
        string b = "alpha beta GAMMA delta epsilon zeta eta THETA";
        IReadOnlyList<DmpPatch> patches = Patcher.Make(a, b);
        DmpApplyResult r = Patcher.Apply(patches, "completely unrelated words that share nothing useful");
        Assert.False(r.Success);
        Assert.All(r.Patches, p => Assert.Equal(DmpPatchOutcome.NotFound, p.Outcome));
        Assert.Equal("completely unrelated words that share nothing useful", r.Text);
    }

    [Fact]
    public void LongDeletionOverChangedTextIsRefused()
    {
        string a = new string('x', 20) + new string('a', 200) + new string('y', 20);
        string b = new string('x', 20) + new string('y', 20);
        var strict = new DmpPatcher { PatchDeleteThreshold = 0.1 };
        string changed = new string('x', 20) + string.Concat(Enumerable.Repeat("ab", 100)) + new string('y', 20);
        DmpApplyResult r = strict.Apply(strict.Make(a, b), changed);
        Assert.False(r.Success);
        Assert.Equal(DmpPatchOutcome.TooDifferent, r.Patches[0].Outcome);
        Assert.Equal(changed, r.Text);
    }

    [Fact]
    public void MatchBehavesAsDocumented()
    {
        var m = new DmpPatcher();
        static int? At(DmpPatcher p, string text, string pattern, int loc) => p.TryMatch(text, pattern, loc, out int at) ? at : null;
        Assert.Equal(0, At(m, "abcdef", "abcdef", 1000));
        Assert.Null(At(m, string.Empty, "abcdef", 1));
        Assert.Equal(3, At(m, "abcdef", string.Empty, 3));
        Assert.Equal(3, At(m, "abcdef", "de", 3));
        Assert.Equal(4, At(m, "abcdefghijk", "efxhi", 5));
        Assert.Null(At(new DmpPatcher { MatchThreshold = 0.0 }, "abcdefghijk", "efxyhi", 5));
        Assert.Null(At(m, "a", new string('a', 33), 0));
        // Distance: the same pattern twice, the near one wins.
        Assert.Equal(0, At(new DmpPatcher { MatchDistance = 10 }, "abc" + new string('-', 50) + "abc", "abc", 5));
    }

    [Theory]
    [InlineData("@@ -1 +1 @@\nx\n")]
    [InlineData(" no header\n")]
    [InlineData("@@ nonsense\n")]
    [InlineData("@@ -1,2 +1,2 @@\n+%zz\n")]
    [InlineData("@@ -1,2 +1,2 @@\n+%C3\n")]
    [InlineData("@@ -1,2 +1,2 @@\n+%FF\n")]
    [InlineData("@@ -99999999999999,2 +1,2 @@\n+a\n")]
    [InlineData("@@ -0,2 +1,2 @@\n+a\n")]
    [InlineData("@@ -1,2 +1,2\n+a\n")]
    public void MalformedTextIsRejectedCleanly(string text)
    {
        Assert.False(DmpPatcher.TryFromText(text, out IReadOnlyList<DmpPatch>? p, out PatchParseError e));
        Assert.Null(p);
        Assert.True(e.Line >= 1);
        Assert.Throws<PatchFormatException>(() => DmpPatcher.FromText(text));
    }

    [Fact]
    public void EmptyTextHasNoPatchesAndAppliesAsNothing()
    {
        Assert.Empty(DmpPatcher.FromText(string.Empty));
        Assert.Equal("abc", Patcher.Apply([], "abc").Text);
        Assert.Equal(string.Empty, DmpPatcher.ToText(Patcher.Make("same", "same")));
    }

    [Fact]
    public void StaticsRoundTrip()
    {
        string t = Diff.PatchText("hello world", "hello brave new world");
        Assert.Equal("hello brave new world", Diff.ApplyPatchText("hello world", t).Text);
        Assert.Equal("a\nc\n", Diff.ApplyUnified("a\nb\n", Diff.Unified("a\nb\n", "a\nc\n")).Text);
    }

    internal static string RandomString(Random random, int max)
    {
        int n = random.Next(0, max);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            int r = random.Next(40);
            sb.Append(r switch
            {
                < 30 => (char)('a' + (r % 8)),
                < 33 => ' ',
                < 35 => '\n',
                < 37 => '%',
                _ => (char)('\u00e0' + r),
            });
            if (r == 39)
            {
                sb.Append("\U0001F600");
            }
        }

        return sb.ToString();
    }

    internal static string MutateString(Random random, string s)
    {
        var sb = new StringBuilder(s);
        for (int e = random.Next(0, 6); e > 0; e--)
        {
            int at = random.Next(sb.Length + 1);
            switch (random.Next(3))
            {
                case 0 when at < sb.Length:
                    sb.Remove(at, Math.Min(random.Next(1, 40), sb.Length - at));
                    break;
                case 1:
                    sb.Insert(at, RandomString(random, 30));
                    break;
                default:
                    if (at < sb.Length)
                    {
                        sb[at] = 'Z';
                    }

                    break;
            }
        }

        // Keep pairs whole: a cut inside a pair would make the mutated text hold lone surrogates, which is a different test.
        string result = sb.ToString();
        return result.Replace('\uD83D', 'q').Replace('\uDE00', 'r');
    }
}
