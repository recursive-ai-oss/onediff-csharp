using OneDiff.Algorithms;

namespace OneDiff.Tests;

public class AlgorithmTests
{
    public static TheoryData<DiffAlgorithm> All => new() { DiffAlgorithm.Myers, DiffAlgorithm.Patience, DiffAlgorithm.Histogram };

    private static EditScript Run(DiffAlgorithm algorithm, int[] a, int[] b) => algorithm switch
    {
        DiffAlgorithm.Myers => MyersDiff.Compute(a, b),
        DiffAlgorithm.Patience => PatienceDiff.Compute(a, b),
        _ => HistogramDiff.Compute(a, b),
    };

    /// <summary>Longest common subsequence length by the textbook O(NM) table: the reference for minimality.</summary>
    private static int Lcs(int[] a, int[] b)
    {
        int[,] t = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                t[i, j] = a[i - 1] == b[j - 1] ? t[i - 1, j - 1] + 1 : Math.Max(t[i - 1, j], t[i, j - 1]);
            }
        }

        return t[a.Length, b.Length];
    }

    private static void AssertValid(EditScript s, int[] a, int[] b, string what)
    {
        Assert.True(EditScript.TryValidate(s.Edits, a.Length, b.Length, out string? error), what + ": " + error);
        Assert.True(s.Matches<int>(a, b), what + ": equal runs differ");
        Assert.Equal(b, s.Apply<int>(a, b));
    }

    private static IEnumerable<int[]> AllSequences(int alphabet, int maxLength)
    {
        yield return [];
        for (int length = 1; length <= maxLength; length++)
        {
            int total = (int)Math.Pow(alphabet, length);
            for (int code = 0; code < total; code++)
            {
                int[] s = new int[length];
                int c = code;
                for (int i = 0; i < length; i++)
                {
                    s[i] = c % alphabet;
                    c /= alphabet;
                }

                yield return s;
            }
        }
    }

    [Fact]
    public void MyersIsMinimalOnEverySmallPair()
    {
        foreach ((int alphabet, int length) in new[] { (2, 7), (3, 5), (4, 4) })
        {
            int[][] seqs = AllSequences(alphabet, length).ToArray();
            foreach (int[] a in seqs)
            {
                foreach (int[] b in seqs)
                {
                    EditScript s = MyersDiff.Compute(a, b);
                    AssertValid(s, a, b, $"{string.Join(",", a)} -> {string.Join(",", b)}");
                    Assert.Equal(a.Length + b.Length - (2 * Lcs(a, b)), s.EditDistance);
                }
            }
        }
    }

    [Fact]
    public void MyersIsMinimalOnRandomPairs()
    {
        var random = new Random(7);
        for (int round = 0; round < 4000; round++)
        {
            int alphabet = 1 + random.Next(1, 25);
            int[] a = Enumerable.Range(0, random.Next(0, 80)).Select(_ => random.Next(alphabet)).ToArray();
            int[] b = random.Next(3) == 0
                ? Enumerable.Range(0, random.Next(0, 80)).Select(_ => random.Next(alphabet)).ToArray()
                : Mutate(a, random, alphabet);
            EditScript s = MyersDiff.Compute(a, b);
            AssertValid(s, a, b, "random " + round);
            Assert.Equal(a.Length + b.Length - (2 * Lcs(a, b)), s.EditDistance);
        }
    }

    private static int[] Mutate(int[] a, Random random, int alphabet)
    {
        var list = a.ToList();
        for (int e = random.Next(0, 6); e > 0; e--)
        {
            int at = random.Next(0, list.Count + 1);
            if (random.Next(2) == 0 && at < list.Count)
            {
                list.RemoveAt(at);
            }
            else
            {
                list.Insert(at, random.Next(alphabet));
            }
        }

        return list.ToArray();
    }

    [Theory]
    [MemberData(nameof(All))]
    public void EveryAlgorithmIsValidAndDeterministic(DiffAlgorithm algorithm)
    {
        var random = new Random(11);
        for (int round = 0; round < 3000; round++)
        {
            int alphabet = random.Next(1, 30);
            int[] a = Enumerable.Range(0, random.Next(0, 120)).Select(_ => random.Next(alphabet)).ToArray();
            int[] b = random.Next(4) == 0
                ? Enumerable.Range(0, random.Next(0, 120)).Select(_ => random.Next(alphabet)).ToArray()
                : Mutate(a, random, alphabet);
            EditScript s = Run(algorithm, a, b);
            AssertValid(s, a, b, algorithm + " " + round);
            Assert.Equal(s.Edits.ToArray(), Run(algorithm, a, b).Edits.ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(All))]
    public void SmallPairsAreValid(DiffAlgorithm algorithm)
    {
        int[][] seqs = AllSequences(3, 5).ToArray();
        foreach (int[] a in seqs)
        {
            foreach (int[] b in seqs)
            {
                AssertValid(Run(algorithm, a, b), a, b, $"{algorithm} {string.Join(",", a)} -> {string.Join(",", b)}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(All))]
    public void SparseAndNegativeIdsWork(DiffAlgorithm algorithm)
    {
        int[] a = [-5, 1_000_000_000, 7, 7, int.MinValue, 3];
        int[] b = [1_000_000_000, 7, int.MaxValue, 7, 3];
        AssertValid(Run(algorithm, a, b), a, b, "sparse");
    }

    [Theory]
    [MemberData(nameof(All))]
    public void EmptyIdenticalAndDisjoint(DiffAlgorithm algorithm)
    {
        Assert.Empty(Run(algorithm, [], []));
        Assert.Equal([Edit.Insert(0, 0, 3)], Run(algorithm, [], [1, 2, 3]).Edits.ToArray());
        Assert.Equal([Edit.Delete(0, 3, 0)], Run(algorithm, [1, 2, 3], []).Edits.ToArray());
        Assert.True(Run(algorithm, [1, 2, 3], [1, 2, 3]).IsIdentical);
        EditScript disjoint = Run(algorithm, [1, 2, 3], [4, 5]);
        Assert.Equal(5, disjoint.EditDistance);
    }

    [Theory]
    [MemberData(nameof(All))]
    public void RepeatedItemsWithOneChange(DiffAlgorithm algorithm)
    {
        int[] a = Enumerable.Repeat(1, 3000).ToArray();
        int[] b = (int[])a.Clone();
        b[1500] = 2;
        EditScript s = Run(algorithm, a, b);
        AssertValid(s, a, b, "repeated");
        Assert.Equal(2, s.EditDistance);
    }

    [Theory]
    [MemberData(nameof(All))]
    public void LargeSequencesWithScatteredEdits(DiffAlgorithm algorithm)
    {
        var random = new Random(3);
        int[] a = Enumerable.Range(0, 150_000).Select(i => i).ToArray();
        var list = a.ToList();
        for (int e = 0; e < 300; e++)
        {
            int at = random.Next(list.Count);
            if (e % 2 == 0)
            {
                list.RemoveAt(at);
            }
            else
            {
                list.Insert(at, 1_000_000 + e);
            }
        }

        int[] b = list.ToArray();
        EditScript s = Run(algorithm, a, b);
        AssertValid(s, a, b, "large");
        Assert.Equal(300, s.EditDistance);
    }

    [Fact]
    public void OneHugeSequenceOfCharacters()
    {
        var random = new Random(5);
        int[] a = Enumerable.Range(0, 1_000_000).Select(_ => random.Next(26)).ToArray();
        int[] b = (int[])a.Clone();
        for (int e = 0; e < 40; e++)
        {
            b[random.Next(b.Length)] = 26 + e;
        }

        EditScript s = MyersDiff.Compute(a, b);
        AssertValid(s, a, b, "huge");
        Assert.True(s.EditDistance <= 80);
    }

    [Theory]
    [MemberData(nameof(All))]
    public void CostCapGivesAValidCoarserScriptAndADiagnostic(DiffAlgorithm algorithm)
    {
        // Two sequences that share almost nothing in a scrambled order keep Myers busy: D is close to N + M.
        var random = new Random(9);
        int[] a = Enumerable.Range(0, 20_000).Select(_ => random.Next(3)).ToArray();
        int[] b = Enumerable.Range(0, 20_000).Select(_ => random.Next(3)).ToArray();

        DiffDiagnostic d1;
        EditScript capped = algorithm switch
        {
            DiffAlgorithm.Myers => MyersDiff.Compute(a, b, 100_000, out d1),
            DiffAlgorithm.Patience => PatienceDiff.Compute(a, b, 100_000, out d1),
            _ => HistogramDiff.Compute(a, b, 100_000, out d1),
        };
        Assert.True(d1.CostCapReached);
        AssertValid(capped, a, b, "capped");

        if (algorithm == DiffAlgorithm.Myers)
        {
            EditScript full = MyersDiff.Compute(a, b, long.MaxValue, out DiffDiagnostic d2);
            Assert.False(d2.CostCapReached);
            Assert.True(full.EditDistance <= capped.EditDistance);
            Assert.True(d2.Work > 100_000);
        }
    }

    [Fact]
    public void ZeroCapStillTrimsAndStaysValid()
    {
        int[] a = [0, 1, 2, 3, 4, 5, 6, 0];
        int[] b = [0, 2, 1, 4, 3, 6, 5, 0];
        EditScript s = MyersDiff.Compute(a, b, 0, out DiffDiagnostic d);
        AssertValid(s, a, b, "zero cap");
        Assert.True(d.CostCapReached);
        Assert.Equal(12, s.EditDistance); // the shared 0s are kept; the six between them are replaced wholesale
        Assert.Throws<ArgumentOutOfRangeException>(() => MyersDiff.Compute(a, b, -1, out _));
    }

    [Fact]
    public void OrdinaryInputsDoNotReachTheDefaultCap()
    {
        var random = new Random(1);
        int[] a = Enumerable.Range(0, 5000).Select(_ => random.Next(50)).ToArray();
        int[] b = Mutate(a, random, 50);
        MyersDiff.Compute(a, b, long.MaxValue, out DiffDiagnostic d);
        Assert.False(d.CostCapReached);
    }

    [Theory]
    [MemberData(nameof(All))]
    public void CancellationIsHonoured(DiffAlgorithm algorithm)
    {
        var random = new Random(2);
        int[] a = Enumerable.Range(0, 5000).Select(_ => random.Next(3)).ToArray();
        int[] b = Enumerable.Range(0, 5000).Select(_ => random.Next(3)).ToArray();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => algorithm switch
        {
            DiffAlgorithm.Myers => MyersDiff.Compute(a, b, cts.Token),
            DiffAlgorithm.Patience => PatienceDiff.Compute(a, b, cts.Token),
            _ => HistogramDiff.Compute(a, b, cts.Token),
        });
    }

    [Fact]
    public void PatienceAnchorsOnUniqueLines()
    {
        // The braces repeat; the unique lines decide the alignment, so the moved function body is not interleaved.
        string[] oldLines = ["{", "a", "}", "{", "b", "}"];
        string[] newLines = ["{", "b", "}", "{", "a", "}"];
        EditScript s = Diff.Sequences<string>(oldLines, newLines, DiffAlgorithm.Patience);
        Assert.True(s.Matches<string>(oldLines, newLines));
    }

    [Fact]
    public void HistogramPrefersTheRareMatch()
    {
        // 1 occurs five times in the old side, 9 once: the anchor is 9 although 1 comes first.
        int[] a = [1, 1, 1, 1, 1, 9, 2];
        int[] b = [1, 9, 2, 1];
        EditScript s = HistogramDiff.Compute(a, b);
        AssertValid(s, a, b, "rare");
        Assert.Contains(s.Edits.ToArray(), e => e.Kind == EditKind.Equal && e.OldStart <= 5 && e.OldEnd > 5);
    }
}
