using System.Buffers;

namespace OneDiff.Algorithms;

/// <summary>
/// Myers' O(ND) algorithm in linear space (Myers 1986, "An O(ND) Difference Algorithm and Its Variations", sections 3 and 4b).
/// The script has the fewest inserted plus deleted items possible, unless the work cap is reached (see
/// <see cref="Compute(ReadOnlySpan{int}, ReadOnlySpan{int}, long, out DiffDiagnostic, CancellationToken)"/>).
/// </summary>
/// <remarks>
/// Tie-breaking: among minimal scripts the one returned is the one the middle-snake search meets first; it is
/// deterministic for given inputs, and equal runs are as long as the choice allows. Items that occur on one side only
/// are set aside before the search (they can never be part of a longest common subsequence), which changes nothing
/// about minimality.
/// </remarks>
public static class MyersDiff
{
    /// <summary>
    /// Compares two sequences of ids (see <see cref="TokenInterner{T}"/>) and returns a canonical <see cref="EditScript"/>
    /// that turns <paramref name="oldIds"/> into <paramref name="newIds"/>. Equal ids are equal items. The script is minimal
    /// unless the default work cap (about two billion edit-graph steps) is reached, which ordinary inputs never do; use the
    /// overload with a diagnostic to learn whether it was.
    /// </summary>
    /// <param name="oldIds">The old sequence.</param>
    /// <param name="newIds">The new sequence.</param>
    /// <param name="cancellationToken">Observed while searching; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    public static EditScript Compute(ReadOnlySpan<int> oldIds, ReadOnlySpan<int> newIds, CancellationToken cancellationToken = default)
        => Compute(oldIds, newIds, Budget.DefaultCap, out _, cancellationToken);

    /// <summary>
    /// Like the overload without a cap, but with an explicit bound on the work and a diagnostic about the run.
    /// When the work spent exceeds <paramref name="maxWork"/> the search stops; what was already settled stays minimal and
    /// the rest becomes a single deletion followed by a single insertion, so the script is still valid but may be longer
    /// than the minimum. <paramref name="diagnostic"/> then has <see cref="DiffDiagnostic.CostCapReached"/> set.
    /// </summary>
    /// <param name="oldIds">The old sequence.</param>
    /// <param name="newIds">The new sequence.</param>
    /// <param name="maxWork">Most edit-graph steps to spend; <see cref="long.MaxValue"/> for no bound. Zero allows only the common prefix and suffix to be trimmed.</param>
    /// <param name="diagnostic">Receives whether the cap was reached and how much work was spent.</param>
    /// <param name="cancellationToken">Observed while searching; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxWork"/> is negative.</exception>
    public static EditScript Compute(
        ReadOnlySpan<int> oldIds,
        ReadOnlySpan<int> newIds,
        long maxWork,
        out DiffDiagnostic diagnostic,
        CancellationToken cancellationToken = default)
    {
        Budget.CheckCap(maxWork);
        cancellationToken.ThrowIfCancellationRequested();
        var budget = new Budget(maxWork);
        var script = new EditScriptBuilder();

        // Trim here as well as inside the core, so the set-aside step below works on the smallest range.
        int prefix = 0;
        int max = Math.Min(oldIds.Length, newIds.Length);
        while (prefix < max && oldIds[prefix] == newIds[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < max - prefix && oldIds[oldIds.Length - 1 - suffix] == newIds[newIds.Length - 1 - suffix])
        {
            suffix++;
        }

        script.Equal(prefix);
        ReadOnlySpan<int> a = oldIds.Slice(prefix, oldIds.Length - prefix - suffix);
        ReadOnlySpan<int> b = newIds.Slice(prefix, newIds.Length - prefix - suffix);
        SolveMiddle(a, b, budget, script, cancellationToken);
        script.Equal(suffix);

        diagnostic = budget.ToDiagnostic();
        return script.ToEditScript();
    }

    private static void SolveMiddle(ReadOnlySpan<int> a, ReadOnlySpan<int> b, Budget budget, EditScriptBuilder script, CancellationToken cancellationToken)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            script.Delete(a.Length);
            script.Insert(b.Length);
            return;
        }

        if (!TryDiscardUnshared(a, b, out int[]? keptOld, out int[]? mapOld, out int keptOldCount, out int[]? keptNew, out int[]? mapNew, out int keptNewCount))
        {
            var core = new MyersCore(a, b, budget, cancellationToken);
            try
            {
                core.Solve(0, a.Length, 0, b.Length, script);
            }
            finally
            {
                core.Dispose();
            }

            return;
        }

        try
        {
            // The reduced problem has the same longest common subsequence; solve it, then map its equal runs back.
            var reducedScript = new EditScriptBuilder();
            var core = new MyersCore(keptOld.AsSpan(0, keptOldCount), keptNew.AsSpan(0, keptNewCount), budget, cancellationToken);
            try
            {
                core.Solve(0, keptOldCount, 0, keptNewCount, reducedScript);
            }
            finally
            {
                core.Dispose();
            }

            EditScript reduced = reducedScript.ToEditScript();
            int oldPos = 0;
            int newPos = 0;
            int run = 0;
            foreach (Edit e in reduced.Edits)
            {
                if (e.Kind != EditKind.Equal)
                {
                    continue;
                }

                for (int t = 0; t < e.OldLength; t++)
                {
                    int oi = mapOld![e.OldStart + t];
                    int nj = mapNew![e.NewStart + t];
                    if (oi == oldPos && nj == newPos)
                    {
                        run++;
                    }
                    else
                    {
                        script.Equal(run);
                        run = 1;
                        script.Delete(oi - oldPos);
                        script.Insert(nj - newPos);
                    }

                    oldPos = oi + 1;
                    newPos = nj + 1;
                }
            }

            script.Equal(run);
            script.Delete(a.Length - oldPos);
            script.Insert(b.Length - newPos);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(keptOld!);
            ArrayPool<int>.Shared.Return(mapOld!);
            ArrayPool<int>.Shared.Return(keptNew!);
            ArrayPool<int>.Shared.Return(mapNew!);
        }
    }

    /// <summary>
    /// Builds copies of both sequences without the items that occur on one side only, with each kept item's original index.
    /// Returns false (nothing rented) when nothing would be dropped or the ids are too sparse to count cheaply.
    /// </summary>
    private static bool TryDiscardUnshared(
        ReadOnlySpan<int> a,
        ReadOnlySpan<int> b,
        out int[]? keptOld,
        out int[]? mapOld,
        out int keptOldCount,
        out int[]? keptNew,
        out int[]? mapNew,
        out int keptNewCount)
    {
        keptOld = mapOld = keptNew = mapNew = null;
        keptOldCount = keptNewCount = 0;

        int min = int.MaxValue;
        int maxId = int.MinValue;
        foreach (int id in a)
        {
            min = Math.Min(min, id);
            maxId = Math.Max(maxId, id);
        }

        foreach (int id in b)
        {
            min = Math.Min(min, id);
            maxId = Math.Max(maxId, id);
        }

        if (min < 0 || maxId > (4L * (a.Length + b.Length)) + 1024)
        {
            return false;
        }

        // Bit 0: seen in the old sequence; bit 1: seen in the new one.
        byte[] seen = ArrayPool<byte>.Shared.Rent(maxId + 1);
        try
        {
            Array.Clear(seen, 0, maxId + 1);
            foreach (int id in a)
            {
                seen[id] |= 1;
            }

            foreach (int id in b)
            {
                seen[id] |= 2;
            }

            int shared = 0;
            foreach (int id in a)
            {
                if (seen[id] == 3)
                {
                    shared++;
                }
            }

            int sharedNew = 0;
            foreach (int id in b)
            {
                if (seen[id] == 3)
                {
                    sharedNew++;
                }
            }

            if (shared == a.Length && sharedNew == b.Length)
            {
                return false;
            }

            keptOld = ArrayPool<int>.Shared.Rent(shared);
            mapOld = ArrayPool<int>.Shared.Rent(shared);
            keptNew = ArrayPool<int>.Shared.Rent(sharedNew);
            mapNew = ArrayPool<int>.Shared.Rent(sharedNew);
            int count = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (seen[a[i]] == 3)
                {
                    keptOld[count] = a[i];
                    mapOld[count++] = i;
                }
            }

            keptOldCount = count;
            count = 0;
            for (int j = 0; j < b.Length; j++)
            {
                if (seen[b[j]] == 3)
                {
                    keptNew[count] = b[j];
                    mapNew[count++] = j;
                }
            }

            keptNewCount = count;
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(seen);
        }
    }
}
