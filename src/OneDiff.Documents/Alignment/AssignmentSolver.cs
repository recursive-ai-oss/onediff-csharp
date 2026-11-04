namespace OneDiff.Documents.Pipeline;

/// <summary>A possible pairing of an old and a new block with the weight of choosing it.</summary>
internal readonly record struct WeightedPair(int Old, int New, long Weight);

/// <summary>
/// The global matcher: chooses, from weighted candidate pairs, the set in which no block is used twice and the total weight is
/// largest. The pairs are split into connected groups first (blocks that share no candidate cannot compete). A small group is
/// solved exactly with the Hungarian method (Kuhn 1955, in the shortest-augmenting-path form of Jonker and Volgenant); a group
/// too big for that is solved greedily, heaviest pair first. Ties break by block number, so the result is deterministic.
/// </summary>
internal static class AssignmentSolver
{
    /// <summary>A group whose matrix would be larger than this many cells is solved greedily.</summary>
    private const long MaxExactCells = 160_000;

    /// <summary>A group whose cubic work would exceed this is solved greedily.</summary>
    private const long MaxExactWork = 12_000_000;

    /// <param name="pairs">Candidates; every weight positive. The order does not matter.</param>
    /// <param name="oldCount">Number of old block ids.</param>
    /// <param name="newCount">Number of new block ids.</param>
    /// <param name="work">Receives the operations spent, for the cost cap.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The chosen pairs, ascending by new then old.</returns>
    public static List<(int Old, int New)> Solve(List<WeightedPair> pairs, int oldCount, int newCount, ref long work, CancellationToken ct)
    {
        var chosen = new List<(int Old, int New)>();
        if (pairs.Count == 0)
        {
            return chosen;
        }

        // Groups of blocks connected by candidate pairs. Old ids are 0..oldCount-1, new ids follow.
        var parent = new int[oldCount + newCount];
        for (int i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        foreach (WeightedPair p in pairs)
        {
            Union(parent, p.Old, oldCount + p.New);
        }

        var ordered = new WeightedPair[pairs.Count];
        var rootOf = new int[pairs.Count];
        var index = new int[pairs.Count];
        for (int i = 0; i < pairs.Count; i++)
        {
            rootOf[i] = Find(parent, pairs[i].Old);
            index[i] = i;
        }

        Array.Sort(index, (a, b) =>
        {
            int c = rootOf[a].CompareTo(rootOf[b]);
            if (c != 0)
            {
                return c;
            }

            c = pairs[a].New.CompareTo(pairs[b].New);
            return c != 0 ? c : pairs[a].Old.CompareTo(pairs[b].Old);
        });
        for (int i = 0; i < index.Length; i++)
        {
            ordered[i] = pairs[index[i]];
        }

        int s = 0;
        while (s < ordered.Length)
        {
            ct.ThrowIfCancellationRequested();
            int root = Find(parent, ordered[s].Old);
            int e = s;
            while (e < ordered.Length && Find(parent, ordered[e].Old) == root)
            {
                e++;
            }

            SolveGroup(ordered.AsSpan(s, e - s), chosen, ref work);
            s = e;
        }

        chosen.Sort(static (a, b) =>
        {
            int c = a.New.CompareTo(b.New);
            return c != 0 ? c : a.Old.CompareTo(b.Old);
        });
        return chosen;
    }

    private static void SolveGroup(ReadOnlySpan<WeightedPair> group, List<(int Old, int New)> chosen, ref long work)
    {
        var olds = new SortedSet<int>();
        var news = new SortedSet<int>();
        foreach (WeightedPair p in group)
        {
            olds.Add(p.Old);
            news.Add(p.New);
        }

        if (group.Length == 1)
        {
            chosen.Add((group[0].Old, group[0].New));
            work++;
            return;
        }

        long rows = Math.Min(olds.Count, news.Count), cols = Math.Max(olds.Count, news.Count);
        if (rows * cols <= MaxExactCells && rows * rows * cols <= MaxExactWork)
        {
            work += rows * rows * cols;
            Hungarian(group, olds, news, chosen);
            return;
        }

        // Too big to solve exactly: take pairs heaviest first, then lowest new and old numbers.
        work += group.Length;
        WeightedPair[] byWeight = group.ToArray();
        Array.Sort(byWeight, static (a, b) =>
        {
            int c = b.Weight.CompareTo(a.Weight);
            if (c != 0)
            {
                return c;
            }

            c = a.New.CompareTo(b.New);
            return c != 0 ? c : a.Old.CompareTo(b.Old);
        });
        var usedOld = new HashSet<int>();
        var usedNew = new HashSet<int>();
        foreach (WeightedPair p in byWeight)
        {
            if (!usedOld.Contains(p.Old) && !usedNew.Contains(p.New))
            {
                usedOld.Add(p.Old);
                usedNew.Add(p.New);
                chosen.Add((p.Old, p.New));
            }
        }
    }

    /// <summary>Maximum-weight assignment on a dense matrix whose missing pairs weigh zero; zero-weight picks are dropped.</summary>
    private static void Hungarian(ReadOnlySpan<WeightedPair> group, SortedSet<int> olds, SortedSet<int> news, List<(int Old, int New)> chosen)
    {
        bool transpose = olds.Count > news.Count;
        int[] rowIds = transpose ? [.. news] : [.. olds];
        int[] colIds = transpose ? [.. olds] : [.. news];
        int n = rowIds.Length, m = colIds.Length;
        var rowIndex = new Dictionary<int, int>(n);
        var colIndex = new Dictionary<int, int>(m);
        for (int i = 0; i < n; i++)
        {
            rowIndex[rowIds[i]] = i + 1;
        }

        for (int j = 0; j < m; j++)
        {
            colIndex[colIds[j]] = j + 1;
        }

        var weight = new long[(n + 1) * (m + 1)];
        foreach (WeightedPair p in group)
        {
            int r = rowIndex[transpose ? p.New : p.Old], c = colIndex[transpose ? p.Old : p.New];
            weight[r * (m + 1) + c] = p.Weight;
        }

        // Minimize the negated weights. u, v are the potentials; p[j] is the row assigned to column j; way[] reconstructs paths.
        var u = new long[n + 1];
        var v = new long[m + 1];
        var assigned = new int[m + 1];
        var way = new int[m + 1];
        for (int i = 1; i <= n; i++)
        {
            assigned[0] = i;
            int j0 = 0;
            var minv = new long[m + 1];
            Array.Fill(minv, long.MaxValue);
            var used = new bool[m + 1];
            do
            {
                used[j0] = true;
                int i0 = assigned[j0], j1 = 0;
                long delta = long.MaxValue;
                for (int j = 1; j <= m; j++)
                {
                    if (used[j])
                    {
                        continue;
                    }

                    long cur = -weight[i0 * (m + 1) + j] - u[i0] - v[j];
                    if (cur < minv[j])
                    {
                        minv[j] = cur;
                        way[j] = j0;
                    }

                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }

                for (int j = 0; j <= m; j++)
                {
                    if (used[j])
                    {
                        u[assigned[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }

                j0 = j1;
            }
            while (assigned[j0] != 0);
            do
            {
                int j1 = way[j0];
                assigned[j0] = assigned[j1];
                j0 = j1;
            }
            while (j0 != 0);
        }

        for (int j = 1; j <= m; j++)
        {
            int i = assigned[j];
            if (i != 0 && weight[i * (m + 1) + j] > 0)
            {
                int a = rowIds[i - 1], b = colIds[j - 1];
                chosen.Add(transpose ? (b, a) : (a, b));
            }
        }
    }

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }

        return x;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra != rb)
        {
            // The smaller root wins, so the grouping does not depend on the order of the pairs.
            if (ra < rb)
            {
                parent[rb] = ra;
            }
            else
            {
                parent[ra] = rb;
            }
        }
    }
}
