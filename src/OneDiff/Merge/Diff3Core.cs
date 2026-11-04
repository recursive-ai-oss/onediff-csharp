using OneDiff.Algorithms;

namespace OneDiff.Merge;

/// <summary>Finds the runs of change between two sequences of ids. Lets a caller substitute its own alignment for the built-in one.</summary>
internal delegate List<Hunk> Aligner(int[] first, int[] second);

/// <summary>One run of changed items between two sequences: a range of the first and the range of the second that replaces it.</summary>
internal readonly record struct Hunk(int FirstStart, int FirstEnd, int SecondStart, int SecondEnd);

/// <summary>
/// A piece of the merge in progress, in item indices of the three inputs. <see cref="Anchor"/> is where the conflict this piece
/// came from began in the base; narrowing a conflict leaves it unchanged so rendering can look at the same base line.
/// </summary>
internal struct Segment
{
    public MergeRegionKind Kind;

    /// <summary>True when ours and theirs hold the same items here, so a gap made of such pieces may join the conflicts around it.</summary>
    public bool Stable;

    public int BaseStart;
    public int BaseEnd;
    public int OursStart;
    public int OursEnd;
    public int TheirsStart;
    public int TheirsEnd;
    public int Anchor;

    public readonly int OursLength => OursEnd - OursStart;

    public readonly int TheirsLength => TheirsEnd - TheirsStart;
}

/// <summary>
/// The three-way merge over interned item ids: Khanna, Kuber and Pierce, "A Formal Investigation of Diff3" (2007), with the
/// conflict narrowing git applies on top. Everything after "observed" below was settled by running git merge-file 2.51.2 on
/// crafted inputs; the documents leave it open.
/// </summary>
internal static class Diff3Core
{
    // Khanna et al. 2007 §2: diff3 aligns the base with each side, then walks both change lists together. A stretch where only one
    // side changed is taken from that side; a stretch where both changed is a conflict unless the two sides made the same change.
    public static List<Segment> Merge(int[] baseIds, int[] oursIds, int[] theirsIds, MergeSettings settings, bool[]? hasAlnum, CancellationToken cancellationToken, Aligner? aligner = null, Budget? budget = null)
    {
        aligner ??= (a, b) => Hunks(a, b, settings.Algorithm, cancellationToken, budget);
        List<Hunk> oursHunks = aligner(baseIds, oursIds);
        List<Hunk> theirsHunks = aligner(baseIds, theirsIds);

        var segments = new List<Segment>();
        int position = 0;       // next base item not yet placed in a segment
        int oursShift = 0;      // ours index minus base index, for items after the last ours hunk
        int theirsShift = 0;
        int i = 0;
        int j = 0;

        while (i < oursHunks.Count || j < theirsHunks.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool haveOurs = i < oursHunks.Count;
            bool haveTheirs = j < theirsHunks.Count;

            // Observed in output of git merge-file 2.51.2: hunks that merely touch (one ends where the other starts, or both insert
            // at the same point) are a conflict, so only a strict gap makes a hunk one-sided.
            // Khanna et al. 2007 §3: both sides made the same change, so there is nothing to decide (a false conflict). Observed in
            // output of git merge-file 2.51.2: only a pair of hunks that match exactly counts. Two sides that end up with equal text
            // from differently shaped hunks still conflict (and in the merge style narrowing then finds nothing left to mark).
            if (haveOurs && haveTheirs && SameHunk(oursHunks[i], theirsHunks[j], oursIds, theirsIds))
            {
                Hunk h = oursHunks[i++];
                Hunk g = theirsHunks[j++];
                AddUnchanged(h.FirstStart);
                segments.Add(new Segment
                {
                    Kind = MergeRegionKind.BothSame,
                    Stable = true,
                    BaseStart = h.FirstStart,
                    BaseEnd = h.FirstEnd,
                    OursStart = h.SecondStart,
                    OursEnd = h.SecondEnd,
                    TheirsStart = g.SecondStart,
                    TheirsEnd = g.SecondEnd,
                    Anchor = h.FirstStart,
                });
                position = h.FirstEnd;
                oursShift = h.SecondEnd - h.FirstEnd;
                theirsShift = g.SecondEnd - g.FirstEnd;
            }
            else if (haveOurs && (!haveTheirs || oursHunks[i].FirstEnd < theirsHunks[j].FirstStart))
            {
                Hunk h = oursHunks[i++];
                AddUnchanged(h.FirstStart);
                segments.Add(new Segment
                {
                    Kind = MergeRegionKind.OursOnly,
                    BaseStart = h.FirstStart,
                    BaseEnd = h.FirstEnd,
                    OursStart = h.SecondStart,
                    OursEnd = h.SecondEnd,
                    TheirsStart = h.FirstStart + theirsShift,
                    TheirsEnd = h.FirstEnd + theirsShift,
                    Anchor = h.FirstStart,
                });
                position = h.FirstEnd;
                oursShift = h.SecondEnd - h.FirstEnd;
            }
            else if (haveTheirs && (!haveOurs || theirsHunks[j].FirstEnd < oursHunks[i].FirstStart))
            {
                Hunk h = theirsHunks[j++];
                AddUnchanged(h.FirstStart);
                segments.Add(new Segment
                {
                    Kind = MergeRegionKind.TheirsOnly,
                    BaseStart = h.FirstStart,
                    BaseEnd = h.FirstEnd,
                    OursStart = h.FirstStart + oursShift,
                    OursEnd = h.FirstEnd + oursShift,
                    TheirsStart = h.SecondStart,
                    TheirsEnd = h.SecondEnd,
                    Anchor = h.FirstStart,
                });
                position = h.FirstEnd;
                theirsShift = h.SecondEnd - h.FirstEnd;
            }
            else
            {
                // A conflict covers every hunk of either side that overlaps or touches the stretch, so keep growing it until none does.
                int start = Math.Min(oursHunks[i].FirstStart, theirsHunks[j].FirstStart);
                int end = Math.Max(oursHunks[i].FirstEnd, theirsHunks[j].FirstEnd);
                AddUnchanged(start);
                int oursBefore = oursShift;
                int theirsBefore = theirsShift;
                i++;
                j++;
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    while (i < oursHunks.Count && oursHunks[i].FirstStart <= end)
                    {
                        end = Math.Max(end, oursHunks[i].FirstEnd);
                        i++;
                        grew = true;
                    }

                    while (j < theirsHunks.Count && theirsHunks[j].FirstStart <= end)
                    {
                        end = Math.Max(end, theirsHunks[j].FirstEnd);
                        j++;
                        grew = true;
                    }
                }

                oursShift = oursHunks[i - 1].SecondEnd - oursHunks[i - 1].FirstEnd;
                theirsShift = theirsHunks[j - 1].SecondEnd - theirsHunks[j - 1].FirstEnd;

                var conflict = new Segment
                {
                    Kind = MergeRegionKind.Conflict,
                    BaseStart = start,
                    BaseEnd = end,
                    OursStart = start + oursBefore,
                    OursEnd = end + oursShift,
                    TheirsStart = start + theirsBefore,
                    TheirsEnd = end + theirsShift,
                    Anchor = start,
                };

                segments.Add(conflict);
                position = end;
            }
        }

        AddUnchanged(baseIds.Length);

        switch (settings.Style)
        {
            case ConflictStyle.Merge:
                segments = Simplify(Refine(segments, oursIds, theirsIds, aligner, cancellationToken), oursIds, hasAlnum);
                break;
            case ConflictStyle.ZDiff3:
                segments = TrimCommonEdges(segments, oursIds, theirsIds);
                break;
        }

        return segments;

        void AddUnchanged(int upTo)
        {
            if (upTo > position)
            {
                segments.Add(new Segment
                {
                    Kind = MergeRegionKind.Unchanged,
                    Stable = true,
                    BaseStart = position,
                    BaseEnd = upTo,
                    OursStart = position + oursShift,
                    OursEnd = upTo + oursShift,
                    TheirsStart = position + theirsShift,
                    TheirsEnd = upTo + theirsShift,
                    Anchor = position,
                });
                position = upTo;
            }
        }
    }

    // Observed in output of git merge-file 2.51.2 (merge style): each conflict is narrowed by diffing its ours side against its
    // theirs side. Items the sides share stay out of the markers and only the stretches where they differ remain conflicts.
    private static List<Segment> Refine(List<Segment> segments, int[] oursIds, int[] theirsIds, Aligner aligner, CancellationToken cancellationToken)
    {
        var result = new List<Segment>(segments.Count);
        foreach (Segment s in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (s.Kind != MergeRegionKind.Conflict)
            {
                result.Add(s);
                continue;
            }

            List<Hunk> hunks = aligner(oursIds.AsSpan(s.OursStart, s.OursLength).ToArray(), theirsIds.AsSpan(s.TheirsStart, s.TheirsLength).ToArray());
            int baseCursor = s.BaseStart;
            bool baseTaken = false;
            int oursAt = 0;
            int theirsAt = 0;
            foreach (Hunk h in hunks)
            {
                if (h.FirstStart > oursAt)
                {
                    AddCommon(h.FirstStart - oursAt);
                }

                result.Add(new Segment
                {
                    Kind = MergeRegionKind.Conflict,
                    BaseStart = baseCursor,
                    BaseEnd = baseTaken ? baseCursor : s.BaseEnd,
                    OursStart = s.OursStart + h.FirstStart,
                    OursEnd = s.OursStart + h.FirstEnd,
                    TheirsStart = s.TheirsStart + h.SecondStart,
                    TheirsEnd = s.TheirsStart + h.SecondEnd,
                    Anchor = s.Anchor,
                });
                if (!baseTaken)
                {
                    baseTaken = true;
                    baseCursor = s.BaseEnd;
                }

                oursAt = h.FirstEnd;
                theirsAt = h.SecondEnd;
            }

            if (oursAt < s.OursLength)
            {
                AddCommon(s.OursLength - oursAt);
            }

            void AddCommon(int length)
            {
                result.Add(new Segment
                {
                    Kind = MergeRegionKind.BothSame,
                    Stable = true,
                    BaseStart = baseCursor,
                    BaseEnd = hunks.Count == 0 ? s.BaseEnd : baseCursor,   // nothing left to dispute: the stretch is one false conflict
                    OursStart = s.OursStart + oursAt,
                    OursEnd = s.OursStart + oursAt + length,
                    TheirsStart = s.TheirsStart + theirsAt,
                    TheirsEnd = s.TheirsStart + theirsAt + length,
                    Anchor = s.Anchor,
                });
                oursAt += length;
                theirsAt += length;
            }
        }

        return result;
    }

    // Observed in output of git merge-file 2.51.2 (merge style): two conflicts with nothing but shared, unchanged items between them
    // are joined into one when the gap is at most three items long, or when no item of the gap holds a letter or digit (ASCII).
    private static List<Segment> Simplify(List<Segment> segments, int[] oursIds, bool[]? hasAlnumById)
    {
        // Without line text there is no way to ask about letters, so only the length rule applies.
        var result = new List<Segment>(segments.Count);
        int lastConflict = -1;
        foreach (Segment s in segments)
        {
            if (s.Kind != MergeRegionKind.Conflict)
            {
                if (lastConflict >= 0 && !s.Stable)
                {
                    lastConflict = -1;
                }

                result.Add(s);
                continue;
            }

            if (lastConflict >= 0)
            {
                int gapLength = 0;
                for (int k = lastConflict + 1; k < result.Count; k++)
                {
                    gapLength += result[k].OursLength;
                }

                if (gapLength <= 3 || (hasAlnumById != null && !GapHasAlnum(result, lastConflict + 1, oursIds, hasAlnumById)))
                {
                    Segment first = result[lastConflict];
                    first.BaseEnd = Math.Max(first.BaseEnd, s.BaseEnd);
                    first.OursEnd = s.OursEnd;
                    first.TheirsEnd = s.TheirsEnd;
                    result.RemoveRange(lastConflict + 1, result.Count - lastConflict - 1);
                    result[lastConflict] = first;
                    continue;
                }
            }

            result.Add(s);
            lastConflict = result.Count - 1;
        }

        return result;
    }

    private static bool GapHasAlnum(List<Segment> result, int from, int[] ours, bool[] hasAlnumById)
    {
        for (int k = from; k < result.Count; k++)
        {
            for (int p = result[k].OursStart; p < result[k].OursEnd; p++)
            {
                if (hasAlnumById[ours[p]])
                {
                    return true;
                }
            }
        }

        return false;
    }

    // zdiff3 as documented by git: lines that begin or end both sides identically are moved out of the conflict. Observed in output of
    // git merge-file 2.51.2: the shared start is taken first, and the shared end only from what is left.
    private static List<Segment> TrimCommonEdges(List<Segment> segments, int[] oursIds, int[] theirsIds)
    {
        var result = new List<Segment>(segments.Count);
        foreach (Segment s in segments)
        {
            if (s.Kind != MergeRegionKind.Conflict)
            {
                result.Add(s);
                continue;
            }

            int most = Math.Min(s.OursLength, s.TheirsLength);
            int prefix = 0;
            while (prefix < most && oursIds[s.OursStart + prefix] == theirsIds[s.TheirsStart + prefix])
            {
                prefix++;
            }

            int suffix = 0;
            while (suffix < most - prefix && oursIds[s.OursEnd - 1 - suffix] == theirsIds[s.TheirsEnd - 1 - suffix])
            {
                suffix++;
            }

            if (prefix > 0)
            {
                result.Add(new Segment
                {
                    Kind = MergeRegionKind.BothSame,
                    Stable = true,
                    BaseStart = s.BaseStart,
                    BaseEnd = s.BaseStart,
                    OursStart = s.OursStart,
                    OursEnd = s.OursStart + prefix,
                    TheirsStart = s.TheirsStart,
                    TheirsEnd = s.TheirsStart + prefix,
                    Anchor = s.Anchor,
                });
            }

            Segment core = s;
            core.OursStart += prefix;
            core.TheirsStart += prefix;
            core.OursEnd -= suffix;
            core.TheirsEnd -= suffix;
            result.Add(core);

            if (suffix > 0)
            {
                result.Add(new Segment
                {
                    Kind = MergeRegionKind.BothSame,
                    Stable = true,
                    BaseStart = s.BaseEnd,
                    BaseEnd = s.BaseEnd,
                    OursStart = s.OursEnd - suffix,
                    OursEnd = s.OursEnd,
                    TheirsStart = s.TheirsEnd - suffix,
                    TheirsEnd = s.TheirsEnd,
                    Anchor = s.Anchor,
                });
            }
        }

        return result;
    }

    private static bool SameHunk(Hunk ours, Hunk theirs, int[] oursIds, int[] theirsIds)
    {
        return ours.FirstStart == theirs.FirstStart
            && ours.FirstEnd == theirs.FirstEnd
            && SameItems(oursIds, ours.SecondStart, ours.SecondEnd, theirsIds, theirs.SecondStart, theirs.SecondEnd);
    }

    private static bool SameItems(int[] a, int aStart, int aEnd, int[] b, int bStart, int bEnd)
    {
        return aEnd - aStart == bEnd - bStart
            && a.AsSpan(aStart, aEnd - aStart).SequenceEqual(b.AsSpan(bStart, bEnd - bStart));
    }

    // With a budget, the cap left is handed to the algorithm; an algorithm that reaches it returns a coarser, complete script.
    public static EditScript Align(ReadOnlySpan<int> first, ReadOnlySpan<int> second, DiffAlgorithm algorithm, CancellationToken cancellationToken, Budget? budget = null)
    {
        if (budget is null)
        {
            return algorithm switch
            {
                DiffAlgorithm.Myers => MyersDiff.Compute(first, second, cancellationToken),
                DiffAlgorithm.Patience => PatienceDiff.Compute(first, second, cancellationToken),
                DiffAlgorithm.Histogram => HistogramDiff.Compute(first, second, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
            };
        }

        long left = Math.Max(0, budget.Cap - budget.Work);
        DiffDiagnostic diagnostic;
        EditScript script = algorithm switch
        {
            DiffAlgorithm.Myers => MyersDiff.Compute(first, second, left, out diagnostic, cancellationToken),
            DiffAlgorithm.Patience => PatienceDiff.Compute(first, second, left, out diagnostic, cancellationToken),
            DiffAlgorithm.Histogram => HistogramDiff.Compute(first, second, left, out diagnostic, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
        budget.Work += diagnostic.Work;
        budget.Exceeded |= diagnostic.CostCapReached;
        return script;
    }

    /// <summary>The runs of change between two sequences, with every run slid as far toward the end as it will go.</summary>
    public static List<Hunk> Hunks(int[] first, int[] second, DiffAlgorithm algorithm, CancellationToken cancellationToken, Budget? budget = null)
    {
        // The merge's own Myers mode follows git's alignment; the other algorithms are the library's.
        if (algorithm == DiffAlgorithm.Myers)
        {
            return GitAlignment.Hunks(first, second, cancellationToken, budget);
        }

        EditScript script = Align(first, second, algorithm, cancellationToken, budget);
        bool[] firstChanged = new bool[first.Length];
        bool[] secondChanged = new bool[second.Length];
        foreach (Edit e in script.Edits)
        {
            switch (e.Kind)
            {
                case EditKind.Delete:
                    Array.Fill(firstChanged, true, e.OldStart, e.OldLength);
                    break;
                case EditKind.Insert:
                    Array.Fill(secondChanged, true, e.NewStart, e.NewLength);
                    break;
            }
        }

        Compact(firstChanged, first, secondChanged, budget);
        Compact(secondChanged, second, firstChanged, budget);
        return ToHunks(firstChanged, secondChanged);
    }

    public static List<Hunk> ToHunks(bool[] firstChanged, bool[] secondChanged)
    {
        int firstLength = firstChanged.Length;
        int secondLength = secondChanged.Length;
        var hunks = new List<Hunk>();
        int i = 0;
        int j = 0;
        while (i < firstLength || j < secondLength)
        {
            while (i < firstLength && j < secondLength && !firstChanged[i] && !secondChanged[j])
            {
                i++;
                j++;
            }

            int startFirst = i;
            int startSecond = j;
            while (i < firstLength && firstChanged[i])
            {
                i++;
            }

            while (j < secondLength && secondChanged[j])
            {
                j++;
            }

            if (i == startFirst && j == startSecond)
            {
                if (i >= firstLength && j >= secondLength)
                {
                    break;
                }

                throw new InvalidOperationException("the edit script does not pair equal items");
            }

            hunks.Add(new Hunk(startFirst, i, startSecond, j));
        }

        return hunks;
    }

    // Observed in output of git 2.51.2 (diff and merge-file): a run of changed items that could sit in several places (the item
    // next to it equals the item at its far edge) is placed like this. Slide it up as far as it goes, joining the runs it meets.
    // Then slide it down as far as it goes, noting each place where it lines up with a run of the other sequence (the same
    // number of unchanged items come before both). If sliding down joins it to the next run, start over with the joined run. At
    // the end, settle at the last place that lined up, or at the bottom when none did.
    internal static void Compact(bool[] changed, int[] items, bool[] other, Budget? budget = null)
    {
        int n = changed.Length;
        HashSet<int> otherGaps = GapsOf(other);
        var done = new List<(int Start, int End, int Unchanged)>();
        int changedBefore = 0;
        int i = 0;
        while (i < n)
        {
            if (!changed[i])
            {
                i++;
                continue;
            }

            // Sliding is a refinement: every single step leaves a valid alignment, so running out of budget just stops here.
            if (budget is { IsSpent: true })
            {
                budget.Exceeded = true;
                return;
            }

            int start = i;
            int end = i;
            while (end < n && changed[end])
            {
                end++;
            }

            // The number of unchanged items before the run is what lines it up with a run in the other sequence.
            int unchanged = start - changedBefore;
            bool joined;
            do
            {
                joined = false;
                while (start > 0 && items[start - 1] == items[end - 1])
                {
                    changed[end - 1] = false;
                    changed[start - 1] = true;
                    start--;
                    end--;
                    unchanged--;
                    if (budget is not null && ++budget.Work > budget.Cap)
                    {
                        budget.Exceeded = true;
                        return;
                    }

                    if (start > 0 && changed[start - 1])
                    {
                        (int Start, int End, int Unchanged) previous = done[^1];
                        done.RemoveAt(done.Count - 1);
                        changedBefore -= previous.End - previous.Start;
                        start = previous.Start;
                        unchanged = previous.Unchanged;
                    }
                }

                int alignedEnd = -1;
                while (true)
                {
                    if (otherGaps.Contains(unchanged))
                    {
                        alignedEnd = end;
                    }

                    if (end < n && items[end] == items[start])
                    {
                        changed[start] = false;
                        changed[end] = true;
                        start++;
                        end++;
                        unchanged++;
                        if (budget is not null && ++budget.Work > budget.Cap)
                        {
                            budget.Exceeded = true;
                            return;
                        }

                        if (end < n && changed[end])
                        {
                            while (end < n && changed[end])
                            {
                                end++;
                            }

                            joined = true;
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                if (joined)
                {
                    continue;
                }

                while (alignedEnd != -1 && end > alignedEnd && start > 0 && !changed[start - 1] && items[start - 1] == items[end - 1])
                {
                    changed[end - 1] = false;
                    changed[start - 1] = true;
                    start--;
                    end--;
                    unchanged--;
                }
            }
            while (joined);

            done.Add((start, end, unchanged));
            changedBefore += end - start;
            i = end;
        }
    }

    // For each run of changes: how many unchanged items come before it.
    private static HashSet<int> GapsOf(bool[] changed)
    {
        var gaps = new HashSet<int>();
        int changedBefore = 0;
        int i = 0;
        while (i < changed.Length)
        {
            if (!changed[i])
            {
                i++;
                continue;
            }

            gaps.Add(i - changedBefore);
            while (i < changed.Length && changed[i])
            {
                i++;
                changedBefore++;
            }
        }

        return gaps;
    }
}
