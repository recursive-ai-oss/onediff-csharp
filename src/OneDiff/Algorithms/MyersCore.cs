using System.Buffers;

namespace OneDiff.Algorithms;

/// <summary>
/// Myers' greedy O(ND) algorithm with the linear-space refinement (Myers 1986, sections 3 and 4b). Works on ranges of two
/// id sequences and writes the script, in order, to an <see cref="EditScriptBuilder"/>.
/// </summary>
internal ref struct MyersCore : IDisposable
{
    // Stands for "this diagonal has no usable furthest-reaching point". Far enough below zero that adding any index
    // to it never reaches the overlap test, and not so far that adding two of them overflows.
    private const int None = -(1 << 30);

    private readonly ReadOnlySpan<int> a;
    private readonly ReadOnlySpan<int> b;
    private readonly Budget budget;
    private readonly CancellationToken cancellationToken;
    private int[]? forward;
    private int[]? backward;

    public MyersCore(ReadOnlySpan<int> a, ReadOnlySpan<int> b, Budget budget, CancellationToken cancellationToken)
    {
        this.a = a;
        this.b = b;
        this.budget = budget;
        this.cancellationToken = cancellationToken;
        forward = null;
        backward = null;
    }

    public void Dispose()
    {
        if (forward is not null)
        {
            ArrayPool<int>.Shared.Return(forward);
            forward = null;
        }

        if (backward is not null)
        {
            ArrayPool<int>.Shared.Return(backward);
            backward = null;
        }
    }

    /// <summary>
    /// Reports the edits that turn <c>a[aLo..aHi]</c> into <c>b[bLo..bHi]</c> to <paramref name="script"/>, in order.
    /// Minimal unless the work cap is reached, in which case the unsearched remainder is one delete plus one insert.
    /// </summary>
    public void Solve(int aLo, int aHi, int bLo, int bHi, EditScriptBuilder script)
    {
        // A common prefix and suffix are always part of some minimal script, and taking them first shrinks the search.
        int prefix = 0;
        while (aLo + prefix < aHi && bLo + prefix < bHi && a[aLo + prefix] == b[bLo + prefix])
        {
            prefix++;
        }

        script.Equal(prefix);
        aLo += prefix;
        bLo += prefix;

        int suffix = 0;
        while (aHi - suffix > aLo && bHi - suffix > bLo && a[aHi - 1 - suffix] == b[bHi - 1 - suffix])
        {
            suffix++;
        }

        aHi -= suffix;
        bHi -= suffix;

        int n = aHi - aLo;
        int m = bHi - bLo;
        if (n == 0)
        {
            script.Insert(m);
        }
        else if (m == 0)
        {
            script.Delete(n);
        }
        else if (MiddleSnake(aLo, n, bLo, m, out int x, out int y))
        {
            // Myers 1986 §4b: the middle snake lies on a shortest path, so each half is a smaller problem of the same kind.
            Solve(aLo, aLo + x, bLo, bLo + y, script);
            Solve(aLo + x, aHi, bLo + y, bHi, script);
        }
        else
        {
            script.Delete(n);
            script.Insert(m);
        }

        script.Equal(suffix);
    }

    /// <summary>
    /// Runs the forward and reverse searches from both corners, one edit at a time, until they overlap on a diagonal
    /// (Myers 1986 §4b). Returns the point (x, y), relative to the range, where a shortest path passes through the middle.
    /// Returns false when the work cap is reached first.
    /// </summary>
    private bool MiddleSnake(int aLo, int n, int bLo, int m, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (budget.IsSpent)
        {
            budget.Exceeded = true;
            return false;
        }

        int delta = n - m;
        bool odd = (delta & 1) != 0;
        int max = (n + m + 1) / 2;
        int offset = max + 1;
        int size = (2 * max) + 3;
        if (forward is null || forward.Length < size)
        {
            if (forward is not null)
            {
                ArrayPool<int>.Shared.Return(forward);
            }

            if (backward is not null)
            {
                ArrayPool<int>.Shared.Return(backward);
            }

            forward = ArrayPool<int>.Shared.Rent(size);
            backward = ArrayPool<int>.Shared.Rent(size);
        }

        int[] vf = forward;
        int[] vb = backward!;
        ReadOnlySpan<int> aa = a.Slice(aLo, n);
        ReadOnlySpan<int> bb = b.Slice(bLo, m);

        vf[offset + 1] = 0;
        vb[offset + 1] = 0;
        vf[offset - 1] = None;
        vb[offset - 1] = None;

        for (int d = 0; d <= max; d++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long work = 0;

            // Just outside the diagonals reached so far there is nothing: a sentinel lets the choice below be one max
            // instead of a branch that the data makes unpredictable.
            if (d > 0)
            {
                vf[offset - d - 1] = None;
                vf[offset + d + 1] = None;
                vb[offset - d - 1] = None;
                vb[offset + d + 1] = None;
            }

            // Forward: furthest-reaching d-path on each diagonal k = x - y.
            for (int k = -d; k <= d; k += 2)
            {
                int index = offset + k;
                // Come from the diagonal below (x stays, y grows) or from the one above (x grows), whichever reaches further.
                int px = Math.Max(vf[index - 1] + 1, vf[index + 1]);
                int py = px - k;
                work++;
                if (px < 0 || px > n || py < 0 || py > m)
                {
                    vf[index] = None;
                    continue;
                }

                int sx = px;
                int sy = py;
                while (sx < aa.Length && sy < bb.Length && aa[sx] == bb[sy])
                {
                    sx++;
                    sy++;
                }

                work += sx - px;
                vf[index] = sx;

                // Odd delta: the forward path of d edits can meet a reverse path of d-1 edits (total 2d-1 = D).
                if (odd)
                {
                    int rk = delta - k;
                    if (rk >= -(d - 1) && rk <= d - 1)
                    {
                        int rx = vb[offset + rk];
                        if (rx >= 0 && sx + rx >= n)
                        {
                            x = sx;
                            y = sy;
                            budget.Work += work;
                            return true;
                        }
                    }
                }
            }

            // Reverse: the same search from the far corner, in coordinates counted from the ends.
            for (int k = -d; k <= d; k += 2)
            {
                int index = offset + k;
                int px = Math.Max(vb[index - 1] + 1, vb[index + 1]);
                int py = px - k;
                work++;
                if (px < 0 || px > n || py < 0 || py > m)
                {
                    vb[index] = None;
                    continue;
                }

                int sx = px;
                int sy = py;
                while (sx < n && sy < m && aa[n - 1 - sx] == bb[m - 1 - sy])
                {
                    sx++;
                    sy++;
                }

                work += sx - px;
                vb[index] = sx;

                // Even delta: the reverse path of d edits meets a forward path of d edits (total 2d = D).
                if (!odd)
                {
                    int fk = delta - k;
                    if (fk >= -d && fk <= d)
                    {
                        int fx = vf[offset + fk];
                        if (fx >= 0 && sx + fx >= n)
                        {
                            x = n - sx;
                            y = m - sy;
                            budget.Work += work;
                            return true;
                        }
                    }
                }
            }

            budget.Work += work;
            if (budget.IsSpent)
            {
                budget.Exceeded = true;
                return false;
            }
        }

        // Unreachable: the searches always overlap by d = ceil(D / 2) <= max.
        budget.Exceeded = true;
        return false;
    }
}
