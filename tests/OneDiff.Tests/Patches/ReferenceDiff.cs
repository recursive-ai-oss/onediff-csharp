namespace OneDiff.Tests.Patches;

/// <summary>
/// A small, exact reference diff for the patch tests: Myers' greedy forward search keeping every frontier, so it is only meant for
/// inputs whose edit distance is modest. It exists so these tests do not depend on how far the shipped algorithms have got.
/// </summary>
internal static class ReferenceDiff
{
    public static EditScript Compute<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, int maxD = 4000)
        where T : notnull
    {
        int n = a.Count;
        int m = b.Count;
        int prefix = 0;
        while (prefix < n && prefix < m && EqualityComparer<T>.Default.Equals(a[prefix], b[prefix]))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix && EqualityComparer<T>.Default.Equals(a[n - 1 - suffix], b[m - 1 - suffix]))
        {
            suffix++;
        }

        int an = n - prefix - suffix;
        int bn = m - prefix - suffix;
        var builder = new EditScriptBuilder();
        builder.Equal(prefix);
        if (an == 0 || bn == 0 || an + bn > 2 * maxD)
        {
            builder.Delete(an);
            builder.Insert(bn);
        }
        else
        {
            Core(a, prefix, an, b, prefix, bn, maxD, builder);
        }

        builder.Equal(suffix);
        return builder.ToEditScript();
    }

    private static void Core<T>(IReadOnlyList<T> a, int ao, int n, IReadOnlyList<T> b, int bo, int m, int maxD, EditScriptBuilder builder)
        where T : notnull
    {
        int max = Math.Min(n + m, maxD);
        int offset = max + 1;
        var v = new int[(2 * max) + 3];
        var trace = new List<int[]>();
        int foundD = -1;
        for (int d = 0; d <= max && foundD < 0; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && EqualityComparer<T>.Default.Equals(a[ao + x], b[bo + y]))
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    foundD = d;
                    break;
                }
            }
        }

        if (foundD < 0)
        {
            builder.Delete(n);
            builder.Insert(m);
            return;
        }

        // Walk back through the saved frontiers: each step was one edit from (px, py) followed by a diagonal run.
        var steps = new List<(int px, int py, bool del)>();
        int cx = n;
        int cy = m;
        for (int d = foundD; d > 0; d--)
        {
            int[] prev = trace[d];
            int k = cx - cy;
            bool del = !(k == -d || (k != d && prev[offset + k - 1] < prev[offset + k + 1]));
            int pk = del ? k - 1 : k + 1;
            int px = prev[offset + pk];
            int py = px - pk;
            steps.Add((px, py, del));
            cx = px;
            cy = py;
        }

        steps.Reverse();
        int x0 = 0;
        int y0 = 0;
        foreach ((int px, int py, bool del) in steps)
        {
            builder.Equal(px - x0);
            if (del)
            {
                builder.Delete(1);
                x0 = px + 1;
                y0 = py;
            }
            else
            {
                builder.Insert(1);
                x0 = px;
                y0 = py + 1;
            }
        }

        builder.Equal(n - x0);
    }
}

/// <summary>Character-level diffs for the dmp tests built on the reference diff.</summary>
internal static class ReferenceDmp
{
    public static List<OneDiff.Patching.DmpDiff> Diffs(string a, string b)
    {
        char[] ca = a.ToCharArray();
        char[] cb = b.ToCharArray();
        EditScript script = ReferenceDiff.Compute(ca.Select(c => (int)c).ToArray(), cb.Select(c => (int)c).ToArray());
        var result = new List<OneDiff.Patching.DmpDiff>();
        foreach (Edit e in script.Edits)
        {
            switch (e.Kind)
            {
                case EditKind.Equal:
                    result.Add(new OneDiff.Patching.DmpDiff(OneDiff.Patching.DmpOperation.Equal, a.Substring(e.OldStart, e.OldLength)));
                    break;
                case EditKind.Delete:
                    result.Add(new OneDiff.Patching.DmpDiff(OneDiff.Patching.DmpOperation.Delete, a.Substring(e.OldStart, e.OldLength)));
                    break;
                default:
                    result.Add(new OneDiff.Patching.DmpDiff(OneDiff.Patching.DmpOperation.Insert, b.Substring(e.NewStart, e.NewLength)));
                    break;
            }
        }

        return result;
    }
}
