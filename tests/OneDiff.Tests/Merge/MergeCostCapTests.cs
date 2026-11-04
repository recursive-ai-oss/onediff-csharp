using System.Diagnostics;
using System.Text;
using OneDiff.Merge;

namespace OneDiff.Tests.Merge;

/// <summary>The cost cap of a three-way merge: a capped merge is coarser, never wrong.</summary>
public class MergeCostCapTests
{
    private static string Join(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    [Fact]
    public void DefaultCapIsMyersCap()
    {
        Assert.Equal(1L << 31, MergeSettings.Default.MaxWork);
        Assert.Throws<ArgumentOutOfRangeException>(() => Diff.Merge("a\n", "b\n", "c\n", new MergeSettings { MaxWork = -1 }));
    }

    [Fact]
    public void SmallMergeStaysWithinTheCap()
    {
        TextMerge m = Diff.Merge("a\nb\nc\n", "a\nX\nc\n", "a\nb\nc\nd\n");
        Assert.False(m.Diagnostic.CostCapReached);
        Assert.Equal("a\nX\nc\nd\n", m.Text);
    }

    // Ours holds the base's lines in reverse, so the only common subsequence is one line long and the edit graph is as big as it gets
    // (N·D with D about 2N). Theirs changes a single line.
    [Theory]
    [InlineData(ConflictStyle.Merge)]
    [InlineData(ConflictStyle.Diff3)]
    [InlineData(ConflictStyle.ZDiff3)]
    public void PathologicalTripleFinishesQuicklyUnderASmallCap(ConflictStyle style)
    {
        const int N = 20000;
        string[] baseLines = Enumerable.Range(0, N).Select(i => "line " + i).ToArray();
        string[] oursLines = baseLines.Reverse().ToArray();
        string[] theirsLines = (string[])baseLines.Clone();
        theirsLines[N / 2] = "changed";
        string b = Join(baseLines), o = Join(oursLines), t = Join(theirsLines);

        var settings = new MergeSettings { Style = style, MaxWork = 200_000 };
        var clock = Stopwatch.StartNew();
        TextMerge m = Diff.Merge(b, o, t, settings);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.True(m.Diagnostic.CostCapReached);
        Assert.InRange(m.Diagnostic.Work, 200_001, 400_000);
        AssertRegionsAreSound(m);

        // Coarser, not lost: ours rewrote everything and theirs changed a line inside it, so the whole file is one conflict.
        Assert.True(m.HasConflicts);
        Assert.Contains("changed", m.Text);
    }

    // Long runs of one repeated line with scattered edits: the slide of changed runs and the many-copies rule see their worst input.
    [Fact]
    public void RepeatedLinesDoNotRunAway()
    {
        const int N = 60000;
        var random = new Random(9);
        string[] baseLines = Enumerable.Repeat("a", N).ToArray();
        var ours = new List<string>(baseLines);
        var theirs = new List<string>(baseLines);
        for (int k = 0; k < 3000; k++)
        {
            ours.Insert(random.Next(ours.Count), random.Next(2) == 0 ? "b" : "a");
            theirs.RemoveAt(random.Next(theirs.Count));
        }

        var clock = Stopwatch.StartNew();
        TextMerge m = Diff.Merge(Join(baseLines), Join(ours), Join(theirs));
        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
        AssertRegionsAreSound(m);
    }

    [Fact]
    public void ZeroCapGivesWholeFileHunks()
    {
        TextMerge m = Diff.Merge("a\nb\nc\nd\n", "d\nc\nb\na\n", "a\nb\nX\nd\n", new MergeSettings { MaxWork = 0 });
        Assert.True(m.Diagnostic.CostCapReached);
        AssertRegionsAreSound(m);
        Assert.True(m.HasConflicts);
    }

    [Fact]
    public void CappedMergesStayValid()
    {
        var random = new Random(1717);
        string[] alphabet = ["a", "b", "c", "d", "e", "f"];
        int capped = 0;
        for (int round = 0; round < 3000; round++)
        {
            string[] baseLines = RandomLines(random, alphabet, random.Next(0, 30));
            string[] oursLines = Mutate(random, alphabet, baseLines);
            string[] theirsLines = Mutate(random, alphabet, baseLines);
            string b = Join(baseLines), o = Join(oursLines), t = Join(theirsLines);
            long cap = random.Next(0, 4) switch { 0 => 0, 1 => 5, 2 => 40, _ => 300 };
            foreach (ConflictStyle style in new[] { ConflictStyle.Merge, ConflictStyle.Diff3, ConflictStyle.ZDiff3 })
            {
                TextMerge m = Diff.Merge(b, o, t, new MergeSettings { Style = style, MaxWork = cap });
                capped += m.Diagnostic.CostCapReached ? 1 : 0;
                AssertRegionsAreSound(m);
                if (!m.HasConflicts)
                {
                    Assert.Equal(Resolve(m), m.Text);
                }

                // Settling every conflict with a side takes that side everywhere the other side did not change.
                TextMerge viaOurs = Diff.Merge(b, o, t, new MergeSettings { Style = style, MaxWork = cap, Resolution = ConflictResolution.Ours });
                Assert.Equal(Resolve(viaOurs, ConflictResolution.Ours), viaOurs.Text);
                TextMerge viaTheirs = Diff.Merge(b, o, t, new MergeSettings { Style = style, MaxWork = cap, Resolution = ConflictResolution.Theirs });
                Assert.Equal(Resolve(viaTheirs, ConflictResolution.Theirs), viaTheirs.Text);
            }

            // One side unchanged: the merge is the other side, whatever the cap.
            TextMerge same = Diff.Merge(b, b, t, new MergeSettings { MaxWork = cap });
            Assert.False(same.HasConflicts);
            Assert.Equal(t, same.Text);
            TextMerge other = Diff.Merge(b, o, b, new MergeSettings { MaxWork = cap });
            Assert.False(other.HasConflicts);
            Assert.Equal(o, other.Text);
        }

        Assert.True(capped > 1000, $"only {capped} capped merges; the test is not exercising the cap");
    }

    private static string[] RandomLines(Random random, string[] alphabet, int count)
        => Enumerable.Range(0, count).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();

    private static string[] Mutate(Random random, string[] alphabet, string[] lines)
    {
        var list = lines.ToList();
        int edits = random.Next(0, 5);
        for (int e = 0; e < edits; e++)
        {
            int kind = random.Next(3);
            if (kind == 0 || list.Count == 0)
            {
                list.Insert(random.Next(list.Count + 1), alphabet[random.Next(alphabet.Length)]);
            }
            else if (kind == 1)
            {
                list.RemoveAt(random.Next(list.Count));
            }
            else
            {
                list[random.Next(list.Count)] = alphabet[random.Next(alphabet.Length)];
            }
        }

        return list.ToArray();
    }

    // The regions tile each input, and what each kind claims about the three ranges holds: nothing a side changed is dropped.
    private static void AssertRegionsAreSound(TextMerge m)
    {
        int b = 0, o = 0, t = 0;
        foreach (MergeRegion r in m.Regions)
        {
            Assert.Equal(b, r.Base.Start);
            Assert.Equal(o, r.Ours.Start);
            Assert.Equal(t, r.Theirs.Start);
            b = r.Base.End;
            o = r.Ours.End;
            t = r.Theirs.End;
            string bs = m.Slice(MergeSide.Base, r.Base).ToString();
            string os = m.Slice(MergeSide.Ours, r.Ours).ToString();
            string ts = m.Slice(MergeSide.Theirs, r.Theirs).ToString();
            switch (r.Kind)
            {
                case MergeRegionKind.Unchanged:
                    Assert.Equal(bs, os);
                    Assert.Equal(bs, ts);
                    break;
                case MergeRegionKind.OursOnly:
                    Assert.Equal(bs, ts);
                    break;
                case MergeRegionKind.TheirsOnly:
                    Assert.Equal(bs, os);
                    break;
                case MergeRegionKind.BothSame:
                    Assert.Equal(os, ts);
                    break;
            }
        }

        Assert.Equal(m.LineCount(MergeSide.Base), b);
        Assert.Equal(m.LineCount(MergeSide.Ours), o);
        Assert.Equal(m.LineCount(MergeSide.Theirs), t);
    }

    // What the regions say the merge should be, with conflicts settled the way the resolution asks (none: there must be no conflict).
    private static string Resolve(TextMerge m, ConflictResolution resolution = ConflictResolution.None)
    {
        var sb = new StringBuilder();
        foreach (MergeRegion r in m.Regions)
        {
            switch (r.Kind)
            {
                case MergeRegionKind.Unchanged:
                    sb.Append(m.Slice(MergeSide.Base, r.Base));
                    break;
                case MergeRegionKind.OursOnly:
                case MergeRegionKind.BothSame:
                    sb.Append(m.Slice(MergeSide.Ours, r.Ours));
                    break;
                case MergeRegionKind.TheirsOnly:
                    sb.Append(m.Slice(MergeSide.Theirs, r.Theirs));
                    break;
                default:
                    Assert.NotEqual(ConflictResolution.None, resolution);
                    if (resolution != ConflictResolution.Theirs)
                    {
                        sb.Append(m.Slice(MergeSide.Ours, r.Ours));
                    }

                    if (resolution != ConflictResolution.Ours)
                    {
                        sb.Append(m.Slice(MergeSide.Theirs, r.Theirs));
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
