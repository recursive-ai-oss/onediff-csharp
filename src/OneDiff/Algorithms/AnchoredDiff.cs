using System.Buffers;

namespace OneDiff.Algorithms;

/// <summary>
/// The shared driver for the anchor-based algorithms (patience and histogram). It repeatedly picks an anchor, a run of
/// items the two sides share, inside a pair of ranges, reports the anchor as equal and recurses on what is left of it
/// and right of it; where no anchor exists it falls back to Myers on that range. The recursion is an explicit stack so
/// long inputs cannot overflow the call stack.
/// </summary>
internal ref struct AnchoredDiff : IDisposable
{
    public enum Mode
    {
        Patience,
        Histogram,
    }

    // A histogram anchor is only taken from items that occur at most this often in the old range; beyond it the
    // candidate lists get long and the choice stops meaning "rare". Value fixed here, not tuned.
    private const int MaxChainLength = 64;

    private readonly struct Item(bool isEqualRun, int aLo, int aHi, int bLo, int bHi)
    {
        public readonly bool IsEqualRun = isEqualRun;
        public readonly int ALo = aLo;
        public readonly int AHi = aHi;
        public readonly int BLo = bLo;
        public readonly int BHi = bHi;
    }

    private readonly ReadOnlySpan<int> a;
    private readonly ReadOnlySpan<int> b;
    private readonly Mode mode;
    private readonly Budget budget;
    private readonly CancellationToken cancellationToken;
    private readonly int[] oldCount;
    private readonly int[] newCount;
    private readonly int[] position;      // histogram: start of the item's chain in `chains`
    private readonly int[] fill;          // patience: where the unique item sits in the new range; histogram: chain fill count
    private readonly List<Item> stack;
    private readonly List<(int A, int B, int Length)> anchors;
    private MyersCore myers;
    private int[] chains;
    private int[] scratchA;

    private AnchoredDiff(ReadOnlySpan<int> a, ReadOnlySpan<int> b, int idCount, Mode mode, Budget budget, CancellationToken cancellationToken)
    {
        this.a = a;
        this.b = b;
        this.mode = mode;
        this.budget = budget;
        this.cancellationToken = cancellationToken;
        oldCount = ArrayPool<int>.Shared.Rent(idCount);
        newCount = ArrayPool<int>.Shared.Rent(idCount);
        position = ArrayPool<int>.Shared.Rent(idCount);
        fill = ArrayPool<int>.Shared.Rent(idCount);
        Array.Clear(oldCount, 0, idCount);
        Array.Clear(newCount, 0, idCount);
        Array.Fill(position, -1, 0, idCount);
        stack = [];
        anchors = [];
        myers = new MyersCore(a, b, budget, cancellationToken);
        chains = [];
        scratchA = [];
    }

    public static EditScript Run(ReadOnlySpan<int> oldIds, ReadOnlySpan<int> newIds, Mode mode, long maxWork, out DiffDiagnostic diagnostic, CancellationToken cancellationToken)
    {
        Budget.CheckCap(maxWork);
        cancellationToken.ThrowIfCancellationRequested();
        var budget = new Budget(maxWork);

        int min = int.MaxValue;
        int maxId = int.MinValue;
        foreach (int id in oldIds)
        {
            min = Math.Min(min, id);
            maxId = Math.Max(maxId, id);
        }

        foreach (int id in newIds)
        {
            min = Math.Min(min, id);
            maxId = Math.Max(maxId, id);
        }

        EditScript result;
        if (oldIds.Length == 0 && newIds.Length == 0)
        {
            result = EditScript.Identical(0);
        }
        else if (min >= 0 && maxId <= (4L * (oldIds.Length + newIds.Length)) + 1024)
        {
            result = RunDense(oldIds, newIds, maxId + 1, mode, budget, cancellationToken);
        }
        else
        {
            // Ids too sparse (or negative) to index arrays by: renumber them in order of first appearance.
            var renumber = new Dictionary<int, int>();
            int[] denseOld = Renumber(oldIds, renumber);
            int[] denseNew = Renumber(newIds, renumber);
            result = RunDense(denseOld, denseNew, renumber.Count, mode, budget, cancellationToken);
        }

        diagnostic = budget.ToDiagnostic();
        return result;
    }

    private static int[] Renumber(ReadOnlySpan<int> ids, Dictionary<int, int> map)
    {
        int[] result = new int[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            if (!map.TryGetValue(ids[i], out int dense))
            {
                dense = map.Count;
                map.Add(ids[i], dense);
            }

            result[i] = dense;
        }

        return result;
    }

    private static EditScript RunDense(ReadOnlySpan<int> oldIds, ReadOnlySpan<int> newIds, int idCount, Mode mode, Budget budget, CancellationToken cancellationToken)
    {
        using var driver = new AnchoredDiff(oldIds, newIds, idCount, mode, budget, cancellationToken);
        return driver.Execute();
    }

    public void Dispose()
    {
        ArrayPool<int>.Shared.Return(oldCount);
        ArrayPool<int>.Shared.Return(newCount);
        ArrayPool<int>.Shared.Return(position);
        ArrayPool<int>.Shared.Return(fill);
        myers.Dispose();
        ReturnScratch(ref chains);
        ReturnScratch(ref scratchA);
    }

    private static void ReturnScratch(ref int[] buffer)
    {
        if (buffer.Length > 0)
        {
            ArrayPool<int>.Shared.Return(buffer);
            buffer = [];
        }
    }

    private static void Grow(ref int[] buffer, int length)
    {
        if (buffer.Length >= length)
        {
            return;
        }

        ReturnScratch(ref buffer);
        buffer = ArrayPool<int>.Shared.Rent(length);
    }

    private EditScript Execute()
    {
        var script = new EditScriptBuilder();
        stack.Add(new Item(false, 0, a.Length, 0, b.Length));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Item item = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (item.IsEqualRun)
            {
                script.Equal(item.AHi - item.ALo);
                continue;
            }

            Segment(item.ALo, item.AHi, item.BLo, item.BHi, script);
        }

        return script.ToEditScript();
    }

    private void Segment(int aLo, int aHi, int bLo, int bHi, EditScriptBuilder script)
    {
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

        // The common suffix is reported after everything inside the segment, so it waits on the stack under the pieces.
        if (suffix > 0)
        {
            stack.Add(new Item(true, 0, suffix, 0, 0));
        }

        if (aLo == aHi || bLo == bHi)
        {
            script.Delete(aHi - aLo);
            script.Insert(bHi - bLo);
            return;
        }

        budget.Work += (aHi - aLo) + (bHi - bLo);
        if (budget.IsSpent)
        {
            budget.Exceeded = true;
            script.Delete(aHi - aLo);
            script.Insert(bHi - bLo);
            return;
        }

        anchors.Clear();
        bool found;
        if (mode == Mode.Patience)
        {
            found = FindPatienceAnchors(aLo, aHi, bLo, bHi);
        }
        else
        {
            found = FindHistogramAnchor(aLo, aHi, bLo, bHi, out bool anyCommon);
            if (!found && !anyCommon)
            {
                script.Delete(aHi - aLo);
                script.Insert(bHi - bLo);
                return;
            }
        }

        if (!found)
        {
            // No usable anchor here: finish this range with Myers, as the write-ups for both algorithms do.
            myers.Solve(aLo, aHi, bLo, bHi, script);
            return;
        }

        // Push right to left so the leftmost piece is on top: remainder, anchor, remainder, anchor, ..., remainder.
        int endA = aHi;
        int endB = bHi;
        for (int i = anchors.Count - 1; i >= 0; i--)
        {
            (int anchorA, int anchorB, int length) = anchors[i];
            PushSegment(anchorA + length, endA, anchorB + length, endB);
            stack.Add(new Item(true, 0, length, 0, 0));
            endA = anchorA;
            endB = anchorB;
        }

        PushSegment(aLo, endA, bLo, endB);
    }

    private void PushSegment(int aLo, int aHi, int bLo, int bHi)
    {
        if (aLo < aHi || bLo < bHi)
        {
            stack.Add(new Item(false, aLo, aHi, bLo, bHi));
        }
    }

    /// <summary>
    /// Patience (Cohen's write-up): items that occur exactly once in each range are candidate anchors; the longest
    /// subsequence of them that is increasing in both ranges (found with patience sorting) becomes the anchors.
    /// </summary>
    private bool FindPatienceAnchors(int aLo, int aHi, int bLo, int bHi)
    {
        for (int i = aLo; i < aHi; i++)
        {
            int id = a[i];
            oldCount[id]++;
        }

        for (int j = bLo; j < bHi; j++)
        {
            newCount[b[j]]++;
        }

        // Candidates in old order, each with its position in the new range.
        int candidates = 0;
        Grow(ref scratchA, aHi - aLo);
        for (int i = aLo; i < aHi; i++)
        {
            int id = a[i];
            if (oldCount[id] == 1 && newCount[id] == 1)
            {
                scratchA[candidates] = i;
                candidates++;
            }
        }

        // Find where each unique item sits in the new range.
        for (int j = bLo; j < bHi; j++)
        {
            int id = b[j];
            if (newCount[id] == 1 && oldCount[id] == 1)
            {
                fill[id] = j;
            }
        }

        bool any = false;
        if (candidates > 0)
        {
            // Patience sorting on the new positions: tails[p] = candidate ending the best increasing run of length p + 1.
            Grow(ref chains, 2 * candidates);
            Span<int> tails = chains.AsSpan(0, candidates);
            Span<int> previous = chains.AsSpan(candidates, candidates);
            int piles = 0;
            for (int c = 0; c < candidates; c++)
            {
                int newPos = fill[a[scratchA[c]]];
                int lo = 0;
                int hi = piles;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (fill[a[scratchA[tails[mid]]]] < newPos)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                previous[c] = lo > 0 ? tails[lo - 1] : -1;
                tails[lo] = c;
                if (lo == piles)
                {
                    piles++;
                }
            }

            if (piles > 0)
            {
                any = true;
                int count = piles;
                // Walk the chain back from the last pile, then reverse into old order.
                int cursor = tails[piles - 1];
                for (int p = 0; p < count; p++)
                {
                    int oldPos = scratchA[cursor];
                    anchors.Add((oldPos, fill[a[oldPos]], 1));
                    cursor = previous[cursor];
                }

                anchors.Reverse();
            }
        }

        for (int i = aLo; i < aHi; i++)
        {
            oldCount[a[i]] = 0;
        }

        for (int j = bLo; j < bHi; j++)
        {
            newCount[b[j]] = 0;
        }

        return any;
    }

    /// <summary>
    /// Histogram (published descriptions): count how often each item occurs in the old range; for each item of the new
    /// range that occurs there, grow the match in both directions and keep the match whose rarest item is rarest, longer
    /// matches winning ties and earlier ones after that. Items occurring more than <see cref="MaxChainLength"/> times
    /// are not tried as starting points. Reports whether the ranges share any item at all in <paramref name="anyCommon"/>.
    /// </summary>
    private bool FindHistogramAnchor(int aLo, int aHi, int bLo, int bHi, out bool anyCommon)
    {
        int aLength = aHi - aLo;
        Grow(ref chains, aLength);
        for (int i = aLo; i < aHi; i++)
        {
            oldCount[a[i]]++;
        }

        // Lay the old positions of each item side by side in `chains`, in increasing order, items in order of first appearance.
        int next = 0;
        for (int i = aLo; i < aHi; i++)
        {
            int id = a[i];
            if (position[id] < 0)
            {
                position[id] = next;
                next += oldCount[id];
                fill[id] = 0;
            }

            chains[position[id] + fill[id]++] = i;
        }

        int bestA = -1;
        int bestB = -1;
        int bestLength = 0;
        int bestRarity = int.MaxValue;
        anyCommon = false;
        int j = bLo;
        while (j < bHi)
        {
            int id = b[j];
            int count = oldCount[id];
            if (count == 0)
            {
                j++;
                continue;
            }

            anyCommon = true;
            if (count > MaxChainLength)
            {
                j++;
                continue;
            }

            int reach = j + 1;
            int start = position[id];
            for (int c = 0; c < count; c++)
            {
                int i = chains[start + c];
                int lowA = i;
                int lowB = j;
                int rarity = count;
                while (lowA > aLo && lowB > bLo && a[lowA - 1] == b[lowB - 1])
                {
                    lowA--;
                    lowB--;
                    rarity = Math.Min(rarity, oldCount[a[lowA]]);
                }

                int highA = i + 1;
                int highB = j + 1;
                while (highA < aHi && highB < bHi && a[highA] == b[highB])
                {
                    rarity = Math.Min(rarity, oldCount[a[highA]]);
                    highA++;
                    highB++;
                }

                budget.Work += highA - lowA;
                int length = highA - lowA;
                if (rarity < bestRarity || (rarity == bestRarity && length > bestLength))
                {
                    bestRarity = rarity;
                    bestLength = length;
                    bestA = lowA;
                    bestB = lowB;
                }

                reach = Math.Max(reach, highB);
            }

            // Later starting points inside a region just examined would re-grow the same match.
            j = reach;
        }

        for (int i = aLo; i < aHi; i++)
        {
            int id = a[i];
            oldCount[id] = 0;
            position[id] = -1;
        }

        if (bestLength == 0)
        {
            return false;
        }

        anchors.Add((bestA, bestB, bestLength));
        return true;
    }
}
