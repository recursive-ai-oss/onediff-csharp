using OneDiff.Patching;

namespace OneDiff.Tests.Patches;

public class UnifiedPatchTests
{
    internal static string Make(string oldText, string newText, int context = 3, string oldName = "a", string newName = "b")
    {
        List<string> o = PatchLines.Split(oldText);
        List<string> n = PatchLines.Split(newText);
        EditScript script = ReferenceDiff.Compute(o, n);
        return UnifiedFilePatch.Create(o, n, script, oldName, newName, context).ToString();
    }

    [Fact]
    public void MakesTheDocumentedShape()
    {
        string o = "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12\n13\n14\n15\n16\n17\n18\n19\n20\n";
        string n = "1\n2\n3\nfour\n5\n6\n7\n8\n9\n10\n11\n12\n13\n14\n15\n16\n17\n18\n19\n20\n21\n";
        string expected =
            "--- a\n+++ b\n" +
            "@@ -1,7 +1,7 @@\n 1\n 2\n 3\n-4\n+four\n 5\n 6\n 7\n" +
            "@@ -18,3 +18,4 @@\n 18\n 19\n 20\n+21\n";
        Assert.Equal(expected, Make(o, n));
    }

    [Fact]
    public void NearbyChangesShareAHunk()
    {
        string o = "a\nb\nc\nd\ne\nf\ng\nh\n";
        string n = "A\nb\nc\nd\ne\nf\ng\nH\n";
        // Six unchanged lines between the changes: exactly twice the context, so one hunk.
        Assert.Equal("--- a\n+++ b\n@@ -1,8 +1,8 @@\n-a\n+A\n b\n c\n d\n e\n f\n g\n-h\n+H\n", Make(o, n, 3));
        // With one fewer context line they split.
        Assert.Contains("@@ -6,3 +6,3 @@", Make(o, n, 2));
    }

    [Fact]
    public void ZeroLengthSidesNameTheLineBefore()
    {
        Assert.Equal("--- a\n+++ b\n@@ -0,0 +1,2 @@\n+x\n+y\n", Make("", "x\ny\n"));
        Assert.Equal("--- a\n+++ b\n@@ -1,2 +0,0 @@\n-x\n-y\n", Make("x\ny\n", ""));
        Assert.Equal("--- a\n+++ b\n@@ -2,0 +3 @@\n+z\n", Make("x\ny\n", "x\ny\nz\n", 0));
        Assert.Equal("--- a\n+++ b\n@@ -2 +1,0 @@\n-y\n", Make("x\ny\n", "x\n", 0));
    }

    [Fact]
    public void IdenticalTextsGiveNothing()
    {
        Assert.Equal(string.Empty, Make("a\nb\n", "a\nb\n"));
        Assert.Equal(string.Empty, Make("", ""));
    }

    [Fact]
    public void MarksAMissingFinalNewline()
    {
        string patch = Make("a\nb", "a\nb\n");
        Assert.Equal("--- a\n+++ b\n@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+b\n", patch);
        Assert.Equal("a\nb\n", Diff.ApplyUnified("a\nb", patch).Text);
        string both = Make("a\nb", "a\nc");
        Assert.Equal("--- a\n+++ b\n@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+c\n\\ No newline at end of file\n", both);
        Assert.Equal("a\nc", Diff.ApplyUnified("a\nb", both).Text);
    }

    [Fact]
    public void KeepsCarriageReturnsByteExact()
    {
        string o = "a\r\nb\r\nc\r\n";
        string n = "a\r\nB\r\nc\r\n";
        string patch = Make(o, n);
        Assert.Contains("-b\r\n", patch);
        Assert.Equal(n, Diff.ApplyUnified(o, patch).Text);
        // A lone carriage return is content, not a line end.
        Assert.Single(PatchLines.Split("a\rb\n"));
    }

    [Fact]
    public void ParseFormatRoundTripsAndTimestampsAreOnlyWhatWeGive()
    {
        string patch = Make("a\nb\nc\n", "a\nx\nc\n", 1, "old.txt", "new.txt");
        UnifiedPatch parsed = UnifiedPatch.Parse(patch);
        Assert.Equal(patch, parsed.ToString());
        Assert.Equal("old.txt", parsed.Files[0].OldName);
        Assert.Null(parsed.Files[0].OldTimestamp);

        var stamped = UnifiedPatch.Parse("--- a.txt\t2020-01-02 03:04:05.000000000 +0000\n+++ b.txt\t2020-01-02 03:04:06.000000000 +0000\n@@ -1 +1 @@ fn()\n-a\n+b\n");
        Assert.Equal("a.txt", stamped.Files[0].OldName);
        Assert.Equal("2020-01-02 03:04:05.000000000 +0000", stamped.Files[0].OldTimestamp);
        Assert.Equal("fn()", stamped.Files[0].Hunks[0].SectionHeading);
        Assert.Equal(1, stamped.Files[0].Hunks[0].OldCount);
    }

    [Fact]
    public void ParsesMultipleFilesAndSkipsGarbageAndGitHeaders()
    {
        string text =
            "From: someone\n\nSome words.\n" +
            "diff --git a/x b/x\nindex 111..222 100644\n--- a/x\n+++ b/x\n@@ -1 +1 @@\n-1\n+2\n" +
            "diff --git a/y b/y\nnew file mode 100644\nindex 000..333\n--- /dev/null\n+++ b/y\n@@ -0,0 +1 @@\n+hi\n";
        UnifiedPatch p = UnifiedPatch.Parse(text);
        Assert.Equal(2, p.Files.Count);
        Assert.Equal(["diff --git a/x b/x", "index 111..222 100644"], p.Files[0].ExtendedHeader);
        Assert.Equal("/dev/null", p.Files[1].OldName);
        Assert.Equal("hi\n", p.Files[1].Apply(string.Empty).Text);
    }

    [Fact]
    public void AcceptsAnEmptyContextLine()
    {
        UnifiedPatch p = UnifiedPatch.Parse("--- a\n+++ b\n@@ -1,3 +1,3 @@\n a\n\n-c\n+d\n");
        Assert.Equal("a\n\nd\n", p.Files[0].Apply("a\n\nc\n").Text);
    }

    [Fact]
    public void ReverseUndoes()
    {
        string o = "a\nb\nc\nd\n";
        string n = "a\nB\nc\nd\ne\n";
        UnifiedFilePatch p = UnifiedPatch.Parse(Make(o, n)).Files[0];
        Assert.Equal(o, p.Apply(n, new UnifiedApplyOptions { Reverse = true }).Text);
        Assert.Equal(o, p.Reverse().Apply(n).Text);
    }

    [Fact]
    public void RandomPairsRoundTrip()
    {
        var random = new Random(7);
        for (int round = 0; round < 300; round++)
        {
            string o = RandomText(random);
            string n = Mutate(random, o);
            int context = random.Next(0, 5);
            string patch = Make(o, n, context);
            UnifiedApplyResult r = Diff.ApplyUnified(o, patch);
            Assert.True(r.Success);
            Assert.Equal(n, r.Text);
            if (patch.Length > 0)
            {
                Assert.Equal(patch, UnifiedPatch.Parse(patch).ToString());
                Assert.Equal(o, UnifiedPatch.Parse(patch).Files[0].Apply(n, new UnifiedApplyOptions { Reverse = true }).Text);
            }
        }
    }

    [Fact]
    public void AppliesAtAnOffsetWhenLinesWereAddedAbove()
    {
        string o = Lines(1, 40);
        string n = o.Replace("20\n", "twenty\n", StringComparison.Ordinal);
        string patch = Make(o, n);
        string drifted = "new1\nnew2\nnew3\n" + o;
        UnifiedApplyResult r = Diff.ApplyUnified(drifted, patch);
        Assert.True(r.Success);
        Assert.Equal(HunkOutcome.AppliedWithOffset, r.Hunks[0].Outcome);
        Assert.Equal(3, r.Hunks[0].Offset);
        Assert.Equal("new1\nnew2\nnew3\n" + n, r.Text);
    }

    [Fact]
    public void AppliesWithFuzzWhenContextChanged()
    {
        string o = Lines(1, 30);
        string n = o.Replace("15\n", "fifteen\n", StringComparison.Ordinal);
        string patch = Make(o, n);
        // Edit the context line two lines before the change and the one two lines after.
        string drifted = o.Replace("\n13\n", "\nthirteen\n", StringComparison.Ordinal).Replace("\n17\n", "\nseventeen\n", StringComparison.Ordinal);
        UnifiedApplyResult r = Diff.ApplyUnified(drifted, patch);
        Assert.True(r.Success);
        Assert.Equal(HunkOutcome.AppliedWithFuzz, r.Hunks[0].Outcome);
        Assert.True(r.Hunks[0].Fuzz is 1 or 2);
        Assert.Equal(drifted.Replace("\n15\n", "\nfifteen\n", StringComparison.Ordinal), r.Text);

        var strict = UnifiedPatch.Parse(patch).Files[0].Apply(drifted, new UnifiedApplyOptions { MaxFuzz = 0 });
        Assert.False(strict.Success);
        Assert.Equal(drifted, strict.Text);
    }

    [Fact]
    public void RejectsAndReportsWithoutThrowing()
    {
        string o = Lines(1, 30);
        string n = o.Replace("15\n", "fifteen\n", StringComparison.Ordinal).Replace("28\n", "twenty-eight\n", StringComparison.Ordinal);
        string patch = Make(o, n, 1);
        string changed = o.Replace("15\n", "something else\n", StringComparison.Ordinal);
        UnifiedApplyResult r = Diff.ApplyUnified(changed, patch);
        Assert.False(r.Success);
        Assert.Equal(HunkOutcome.Rejected, r.Hunks[0].Outcome);
        Assert.Equal(HunkRejectReason.ContextMismatch, r.Hunks[0].Reason);
        Assert.Equal(HunkOutcome.Applied, r.Hunks[1].Outcome);
        Assert.NotNull(r.Rejected);
        Assert.Single(r.Rejected.Hunks);
        Assert.Equal(changed.Replace("28\n", "twenty-eight\n", StringComparison.Ordinal), r.Text);
    }

    [Fact]
    public void RecognizesAnAlreadyAppliedPatch()
    {
        string o = Lines(1, 30);
        string n = o.Replace("15\n", "fifteen\n", StringComparison.Ordinal);
        UnifiedApplyResult r = Diff.ApplyUnified(n, Make(o, n));
        Assert.False(r.Success);
        Assert.Equal(HunkRejectReason.AlreadyApplied, r.Hunks[0].Reason);
    }

    [Fact]
    public void HunksAtTheFileEdgesStayAnchored()
    {
        // The hunk touches line 1, so it has no leading context; it must not match in the middle of a file that gained lines on top.
        string o = "a\nb\nc\nd\ne\n";
        string n = "A\nb\nc\nd\ne\n";
        string patch = Make(o, n);
        UnifiedApplyResult r = Diff.ApplyUnified("x\n" + o, patch);
        Assert.False(r.Success);
    }

    public static TheoryData<string> Malformed => new()
    {
        "--- a\n",
        "--- a\n+++ b\n@@ -1,2 +1,2 @@\n a\n",
        "--- a\n+++ b\n@@ -1,2 +1,2 @@\n a\n-b\n",
        "--- a\n+++ b\n@@ -1 +1 @@\n*x\n",
        "--- a\n+++ b\n@@ -1 +1 @@\n-x\n+y\n+z\n@@ nonsense\n",
        "--- a\n+++ b\n@@ -x +1 @@\n-1\n+2\n",
        "--- a\n+++ b\n@@ -99999999999999999999,1 +1 @@\n-1\n+2\n",
        "--- a\n+++ b\n@@ -0,1 +1 @@\n-1\n+2\n",
        "--- a\n+++ b\n@@ -1,2 +1\n-1\n",
        "--- a\n+++ b\n@@ -1,1000000000 +1,1000000000 @@\n x\n",
        "not a patch at all\n",
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void MalformedInputIsRejectedCleanly(string text)
    {
        Assert.False(UnifiedPatch.TryParse(text, out UnifiedPatch? p, out PatchParseError error));
        Assert.Null(p);
        Assert.True(error.Line >= 1);
        Assert.Throws<PatchFormatException>(() => UnifiedPatch.Parse(text));
    }

    [Fact]
    public void EveryPrefixOfAPatchIsAcceptedOrRejectedCleanly()
    {
        string patch = Make(Lines(1, 30), Lines(1, 30).Replace("15\n", "x\ny\n", StringComparison.Ordinal), 2);
        for (int i = 0; i < patch.Length; i++)
        {
            _ = UnifiedPatch.TryParse(patch[..i], out _, out _);
        }
    }

    [Fact]
    public void WhitespaceOnlyIsAPatchWithNoFiles()
    {
        Assert.Empty(UnifiedPatch.Parse(string.Empty).Files);
        Assert.Empty(UnifiedPatch.Parse("\n \n").Files);
    }

    [Fact]
    public void QuotedNamesAreUnquoted()
    {
        UnifiedPatch p = UnifiedPatch.Parse("--- \"a/caf\\303\\251 x\"\n+++ \"b/t\\tab\"\n@@ -1 +1 @@\n-1\n+2\n");
        Assert.Equal("a/caf\u00e9 x", p.Files[0].OldName);
        Assert.Equal("b/t\tab", p.Files[0].NewName);
    }

    private static string Lines(int from, int to) => string.Concat(Enumerable.Range(from, to - from + 1).Select(i => i + "\n"));

    internal static string RandomText(Random random)
    {
        int count = random.Next(0, 40);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.Append((char)('a' + random.Next(6)));
            if (random.Next(10) == 0)
            {
                sb.Append('\r');
            }

            if (i < count - 1 || random.Next(4) != 0)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    internal static string Mutate(Random random, string text)
    {
        List<string> lines = PatchLines.Split(text);
        int edits = random.Next(0, 6);
        for (int e = 0; e < edits; e++)
        {
            int at = lines.Count == 0 ? 0 : random.Next(lines.Count + 1);
            switch (random.Next(3))
            {
                case 0 when at < lines.Count:
                    lines.RemoveAt(at);
                    break;
                case 1:
                    lines.Insert(at, ((char)('a' + random.Next(6))) + "\n");
                    break;
                default:
                    if (at < lines.Count)
                    {
                        lines[at] = ((char)('a' + random.Next(6))) + (lines[at].EndsWith('\n') ? "\n" : string.Empty);
                    }

                    break;
            }
        }

        return string.Concat(lines);
    }
}
