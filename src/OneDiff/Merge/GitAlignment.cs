namespace OneDiff.Merge;

/// <summary>
/// The base-to-side alignment the merge uses. It is Myers' O(ND) search (Myers 1986 §4b, forward and reverse searches meeting at
/// a middle snake) with the shortcuts git's own diff takes, so that the same stretches are reported as changed. The result is
/// not always the shortest script. Each shortcut was found by comparing with the output of git 2.51.2 (see docs/merge.md).
/// </summary>
internal static class GitAlignment
{
    public static List<Hunk> Hunks(int[] first, int[] second, CancellationToken cancellationToken)
    {
        bool[] firstChanged = new bool[first.Length];
        bool[] secondChanged = new bool[second.Length];
        Mark(first, second, firstChanged, secondChanged, cancellationToken);
        Diff3Core.Compact(firstChanged, first, secondChanged);
        Diff3Core.Compact(secondChanged, second, firstChanged);

        return Diff3Core.ToHunks(firstChanged, secondChanged);
    }

    private static void Mark(int[] a, int[] b, bool[] aChanged, bool[] bChanged, CancellationToken cancellationToken)
    {
        int prefix = 0;
        int most = Math.Min(a.Length, b.Length);
        while (prefix < most && a[prefix] == b[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < most - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }

        int aEnd = a.Length - suffix;
        int bEnd = b.Length - suffix;

        // Items that occur on only one side cannot match anything: they are changed, and the search only sees the rest.
        var countInA = new Dictionary<int, int>();
        var countInB = new Dictionary<int, int>();
        for (int i = 0; i < a.Length; i++)
        {
            countInA[a[i]] = countInA.GetValueOrDefault(a[i]) + 1;
        }

        for (int j = 0; j < b.Length; j++)
        {
            countInB[b[j]] = countInB.GetValueOrDefault(b[j]) + 1;
        }

        int limitA = MatchLimit(a.Length);
        int limitB = MatchLimit(b.Length);
        List<int> aKept = Survivors(a, prefix, aEnd, countInB, limitA);
        List<int> bKept = Survivors(b, prefix, bEnd, countInA, limitB);
        for (int i = prefix; i < aEnd; i++)
        {
            aChanged[i] = true;
        }

        for (int j = prefix; j < bEnd; j++)
        {
            bChanged[j] = true;
        }

        foreach (int i in aKept)
        {
            aChanged[i] = false;
        }

        foreach (int j in bKept)
        {
            bChanged[j] = false;
        }

        int[] x = new int[aKept.Count];
        int[] y = new int[bKept.Count];
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = a[aKept[i]];
        }

        for (int j = 0; j < y.Length; j++)
        {
            y[j] = b[bKept[j]];
        }

        bool[] xChanged = new bool[x.Length];
        bool[] yChanged = new bool[y.Length];
        var search = new Search(x, y, xChanged, yChanged, cancellationToken);
        search.Compare(0, x.Length, 0, y.Length);
        for (int i = 0; i < x.Length; i++)
        {
            aChanged[aKept[i]] = xChanged[i];
        }

        for (int j = 0; j < y.Length; j++)
        {
            bChanged[bKept[j]] = yChanged[j];
        }
    }

    // Observed in output of git 2.51.2: an item stops matching when the other side holds at least this many copies of it. The
    // limit grows with the size of the side the item is on, 4 for 8 to 15 items, 8 for 16 to 63, 16 for 64 to 255, and so on
    // (the integer square root rounded to a power of two); there is no limit under 8 items.
    private static int MatchLimit(int size)
    {
        if (size < 8)
        {
            return int.MaxValue;
        }

        int limit = 1;
        for (int n = size; n > 0; n >>= 2)
        {
            limit <<= 1;
        }

        return limit;
    }

    // Observed in output of git 2.51.2: which items the search gets to see. An item with no copy on the other side never matches.
    // An item with at least `limit` copies there (a many-copies item) is dropped as well when it sits in a long stretch of
    // items that cannot match. Looking left and right from it, over items without a copy (counted as D) and over other
    // many-copies items (counted in P, which starts at 2 for the two sides), until a plain item ends the look: it stays when
    // either side has no D, or when D is at most three times P. These looks are static: whether another many-copies item is
    // itself kept does not change them.
    private static List<int> Survivors(int[] items, int from, int to, Dictionary<int, int> countOnOtherSide, int limit)
    {
        const int Plain = 0;
        const int NoCopy = 1;
        const int ManyCopies = 2;
        int[] kind = new int[to - from];
        for (int i = from; i < to; i++)
        {
            int copies = countOnOtherSide.GetValueOrDefault(items[i]);
            kind[i - from] = copies == 0 ? NoCopy : copies >= limit ? ManyCopies : Plain;
        }

        var kept = new List<int>();
        for (int i = 0; i < kind.Length; i++)
        {
            bool keep = kind[i] == Plain;
            if (kind[i] == ManyCopies)
            {
                int noCopyLeft = 0;
                int many = 2;
                for (int j = i - 1; j >= 0 && kind[j] != Plain; j--)
                {
                    if (kind[j] == NoCopy)
                    {
                        noCopyLeft++;
                    }
                    else
                    {
                        many++;
                    }
                }

                int noCopyRight = 0;
                for (int j = i + 1; j < kind.Length && kind[j] != Plain; j++)
                {
                    if (kind[j] == NoCopy)
                    {
                        noCopyRight++;
                    }
                    else
                    {
                        many++;
                    }
                }

                keep = noCopyLeft == 0 || noCopyRight == 0 || noCopyLeft + noCopyRight <= 3 * many;
            }

            if (keep)
            {
                kept.Add(from + i);
            }
        }

        return kept;
    }

    private sealed class Search(int[] a, int[] b, bool[] aChanged, bool[] bChanged, CancellationToken cancellationToken)
    {
        private int[] forward = new int[16];
        private int[] reverse = new int[16];

        // Compares a[aLow..aHigh) with b[bLow..bHigh).
        public void Compare(int aLow, int aHigh, int bLow, int bHigh)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (aLow < aHigh && bLow < bHigh && a[aLow] == b[bLow])
            {
                aLow++;
                bLow++;
            }

            while (aLow < aHigh && bLow < bHigh && a[aHigh - 1] == b[bHigh - 1])
            {
                aHigh--;
                bHigh--;
            }

            if (aLow == aHigh)
            {
                Array.Fill(bChanged, true, bLow, bHigh - bLow);
                return;
            }

            if (bLow == bHigh)
            {
                Array.Fill(aChanged, true, aLow, aHigh - aLow);
                return;
            }

            (int x, int y) = MiddleSnake(aLow, aHigh, bLow, bHigh);
            if ((x == aLow && y == bLow) || (x == aHigh && y == bHigh))
            {
                // The split would not make the problem smaller.
                Array.Fill(aChanged, true, aLow, aHigh - aLow);
                Array.Fill(bChanged, true, bLow, bHigh - bLow);
                return;
            }

            Compare(aLow, x, bLow, y);
            Compare(x, aHigh, y, bHigh);
        }

        // Myers 1986 §4b: grow forward paths from the start and reverse paths from the end, one edit at a time, until they overlap;
        // the overlap is where the problem splits.
        private (int X, int Y) MiddleSnake(int aLow, int aHigh, int bLow, int bHigh)
        {
            int n = aHigh - aLow;
            int m = bHigh - bLow;
            int delta = n - m;
            bool odd = (delta & 1) != 0;
            int max = (n + m + 1) / 2 + 1;
            int offset = max + 1;
            if (forward.Length < 2 * offset + 2)
            {
                forward = new int[2 * offset + 2];
                reverse = new int[2 * offset + 2];
            }

            forward[offset + 1] = 0;
            reverse[offset + 1] = 0;
            for (int d = 0; d <= max; d++)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    if (pass == 0)
                    {
                        for (int step = 0; step <= d; step++)
                        {
                            // Observed in output of git 2.51.2: forward diagonals are visited from k = d down to k = -d.
                            int k = d - 2 * step;
                            bool up = forward[offset + k - 1] < forward[offset + k + 1];
                            int x = k == -d || (k != d && up) ? forward[offset + k + 1] : forward[offset + k - 1] + 1;
                            int y = x - k;
                            while (x < n && y < m && a[aLow + x] == b[bLow + y])
                            {
                                x++;
                                y++;
                            }

                            forward[offset + k] = x;
                            if (odd && k >= delta - (d - 1) && k <= delta + (d - 1) && x + reverse[offset + delta - k] >= n)
                            {
                                return (aLow + x, bLow + y);
                            }
                        }
                    }
                    else
                    {
                        for (int step = 0; step <= d; step++)
                        {
                            int k = -d + 2 * step;
                            bool up = reverse[offset + k - 1] < reverse[offset + k + 1];
                            int x = k == -d || (k != d && up) ? reverse[offset + k + 1] : reverse[offset + k - 1] + 1;
                            int y = x - k;
                            while (x < n && y < m && a[aHigh - 1 - x] == b[bHigh - 1 - y])
                            {
                                x++;
                                y++;
                            }

                            reverse[offset + k] = x;
                            int fk = delta - k;
                            if (!odd && fk >= -d && fk <= d && forward[offset + fk] + x >= n)
                            {
                                return (aHigh - x, bHigh - y);
                            }
                        }
                    }
                }
            }

            throw new InvalidOperationException("the searches did not meet");
        }
    }
}
