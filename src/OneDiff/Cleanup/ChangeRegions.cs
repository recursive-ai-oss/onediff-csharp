namespace OneDiff.Cleanup;

/// <summary>
/// The changed regions of a script (a deletion and/or an insertion between two equal runs), kept as four ints each
/// (old start, old end, new start, new end, in tokens) in a caller-supplied buffer, and merged left to right. Merging two regions
/// drops the equality between them: its tokens become part of both the deletion and the insertion, so the result always still
/// turns the old tokens into the new ones.
/// </summary>
internal static class ChangeRegions
{
    private const int Stride = 4;

    /// <summary>Which rule decides that an equality between two changes is not worth keeping.</summary>
    public enum Rule { Semantic, Efficiency }

    /// <summary>Number of maximal runs of non-equal edits.</summary>
    public static int CountRegions(EditScript script)
    {
        ReadOnlySpan<Edit> edits = script.Edits;
        int regions = 0;
        for (int i = 0; i < edits.Length; i++)
        {
            if (edits[i].Kind != EditKind.Equal && (i == 0 || edits[i - 1].Kind == EditKind.Equal))
            {
                regions++;
            }
        }

        return regions;
    }

    /// <summary>Ints of buffer needed for <paramref name="regions"/> regions.</summary>
    public static int Size(int regions) => regions * Stride;

    /// <summary>
    /// Reads the regions of <paramref name="script"/> into <paramref name="buffer"/>, merging across every equality that
    /// <paramref name="rule"/> calls trivial. A merged region may in turn make the equality before it trivial, so after each merge
    /// the region is compared with the one before it again, as on a stack; each merge removes a region, so the work is linear.
    /// Returns the number of regions left.
    /// </summary>
    public static int Collect(EditScript script, TokenText oldText, TokenText newText, Rule rule, int editCost, Span<int> buffer, out bool merged)
    {
        merged = false;
        int top = 0;
        ReadOnlySpan<Edit> edits = script.Edits;
        for (int i = 0; i < edits.Length; i++)
        {
            Edit e = edits[i];
            if (e.Kind == EditKind.Equal)
            {
                continue;
            }

            int os = e.OldStart, ns = e.NewStart;
            int oe = e.OldEnd, ne = e.NewEnd;
            while (i + 1 < edits.Length && edits[i + 1].Kind != EditKind.Equal)
            {
                i++;
                oe = edits[i].OldEnd;
                ne = edits[i].NewEnd;
            }

            while (top > 0)
            {
                Span<int> prev = buffer.Slice((top - 1) * Stride, Stride);
                if (!IsTrivial(oldText, newText, prev[0], prev[1], prev[2], prev[3], os, oe, ns, ne, rule, editCost))
                {
                    break;
                }

                os = prev[0];
                ns = prev[2];
                top--;
                merged = true;
            }

            Span<int> slot = buffer.Slice(top * Stride, Stride);
            slot[0] = os;
            slot[1] = oe;
            slot[2] = ns;
            slot[3] = ne;
            top++;
        }

        return top;
    }

    private static bool IsTrivial(
        TokenText oldText, TokenText newText,
        int leftOldStart, int leftOldEnd, int leftNewStart, int leftNewEnd,
        int rightOldStart, int rightOldEnd, int rightNewStart, int rightNewEnd,
        Rule rule, int editCost)
    {
        int equal = oldText.Chars(leftOldEnd, rightOldStart);
        int delLeft = oldText.Chars(leftOldStart, leftOldEnd);
        int insLeft = newText.Chars(leftNewStart, leftNewEnd);
        int delRight = oldText.Chars(rightOldStart, rightOldEnd);
        int insRight = newText.Chars(rightNewStart, rightNewEnd);

        if (rule == Rule.Semantic)
        {
            // Semantic: an equality no longer than the changes on both sides is noise between them. Reading "quick brown" -> "slow red"
            // as one replacement is easier than a keep-the-space patchwork.
            return equal <= Math.Max(delLeft, insLeft) && equal <= Math.Max(delRight, insRight);
        }

        // Efficiency: every deletion or insertion run costs `editCost` to process and every character costs 1. Merging two regions
        // leaves one deletion and one insertion (2 runs) and adds the equality's characters to both. Merge when that is cheaper.
        int runsBefore = (delLeft > 0 ? 1 : 0) + (insLeft > 0 ? 1 : 0) + (delRight > 0 ? 1 : 0) + (insRight > 0 ? 1 : 0);
        long saved = (long)(runsBefore - 2) * editCost;
        return saved > 2L * equal;
    }

    /// <summary>
    /// Writes the regions back as a script, or returns <paramref name="original"/> when nothing merged and no overlap applies. With
    /// <paramref name="extractOverlap"/>, a region that deletes text which the insertion then starts with (or the reverse) keeps that
    /// shared text as an equality, e.g. <c>-abcxxx +xxxdef</c> becomes <c>-abc =xxx +def</c>, when the shared text is at least half of
    /// the shorter side.
    /// </summary>
    public static EditScript ToScript(ReadOnlySpan<int> buffer, int count, bool merged, TokenText oldText, TokenText newText, TokenEquality equality, bool extractOverlap, EditScript original)
    {
        if (!merged)
        {
            bool any = false;
            if (extractOverlap)
            {
                for (int r = 0; r < count && !any; r++)
                {
                    ChooseOverlap(buffer.Slice(r * Stride, Stride), oldText, newText, equality, out int afterDelete, out int afterInsert);
                    any = afterDelete > 0 || afterInsert > 0;
                }
            }

            if (!any)
            {
                return original;
            }
        }

        var builder = new EditScriptBuilder();
        int oldPos = 0;
        for (int r = 0; r < count; r++)
        {
            ReadOnlySpan<int> region = buffer.Slice(r * Stride, Stride);
            builder.Equal(region[0] - oldPos);
            int dels = region[1] - region[0];
            int ins = region[3] - region[2];
            int overlapAfterDelete = 0;
            int overlapAfterInsert = 0;
            if (extractOverlap)
            {
                ChooseOverlap(region, oldText, newText, equality, out overlapAfterDelete, out overlapAfterInsert);
            }

            if (overlapAfterDelete > 0)
            {
                builder.Delete(dels - overlapAfterDelete);
                builder.Equal(overlapAfterDelete);
                builder.Insert(ins - overlapAfterDelete);
            }
            else if (overlapAfterInsert > 0)
            {
                builder.Insert(ins - overlapAfterInsert);
                builder.Equal(overlapAfterInsert);
                builder.Delete(dels - overlapAfterInsert);
            }
            else
            {
                builder.Delete(dels);
                builder.Insert(ins);
            }

            oldPos = region[1];
        }

        builder.Equal(original.OldLength - oldPos);
        EditScript script = builder.ToEditScript();
        return script.OldLength == original.OldLength && script.NewLength == original.NewLength
            ? script
            : throw new InvalidOperationException("cleanup produced a script of the wrong size");
    }

    // For a region that both deletes and inserts: the longest suffix of the deletion that the insertion starts with (kept as an
    // equality after the deletion), or the mirror image, when the shared text is at least half of the shorter side.
    private static void ChooseOverlap(ReadOnlySpan<int> region, TokenText oldText, TokenText newText, TokenEquality equality, out int afterDelete, out int afterInsert)
    {
        afterDelete = 0;
        afterInsert = 0;
        int oldStart = region[0], oldEnd = region[1], newStart = region[2], newEnd = region[3];
        if (oldEnd == oldStart || newEnd == newStart)
        {
            return;
        }

        int shorter = Math.Min(oldText.Chars(oldStart, oldEnd), newText.Chars(newStart, newEnd));
        int k1 = Overlap(oldText, oldStart, oldEnd, newText, newStart, newEnd, equality);
        int k2 = Overlap(newText, newStart, newEnd, oldText, oldStart, oldEnd, equality);
        int chars1 = k1 > 0 ? oldText.Chars(oldEnd - k1, oldEnd) : 0;
        int chars2 = k2 > 0 ? newText.Chars(newEnd - k2, newEnd) : 0;
        if (chars1 >= chars2 && k1 > 0 && 2 * chars1 >= shorter)
        {
            afterDelete = k1;
        }
        else if (k2 > 0 && 2 * chars2 >= shorter)
        {
            afterInsert = k2;
        }
    }

    // Longest suffix of text[from..to) that is a prefix of pattern[pFrom..pTo), in tokens.
    private static int Overlap(TokenText text, int from, int to, TokenText pattern, int pFrom, int pTo, TokenEquality equality)
    {
        // Quick answer for the usual case, no overlap at all: the overlap must start with the pattern's first token, so look for
        // that token in the text. Plain characters under ordinal comparison can use a vectorized search.
        if (text.IsChars && pattern.IsChars && equality.IsOrdinal)
        {
            ReadOnlySpan<char> t = text.Text.AsSpan(from, to - from);
            ReadOnlySpan<char> p = pattern.Text.AsSpan(pFrom, pTo - pFrom);
            int result = OverlapChars(t, p, out bool decided);
            if (decided)
            {
                return result;
            }
        }

        return OverlapKmp(text, from, to, pattern, pFrom, pTo, equality);
    }

    // Candidate starts are the places in the text where the pattern's first char occurs. A handful of candidates are checked directly;
    // many (repetitive text) are left to the linear-time search, so the worst case stays linear.
    private static int OverlapChars(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern, out bool decided)
    {
        decided = true;
        int max = Math.Min(text.Length, pattern.Length);
        int from = text.Length - max;
        char first = pattern[0];
        int checks = 0;
        while (from < text.Length)
        {
            int found = text.Slice(from).IndexOf(first);
            if (found < 0)
            {
                return 0;
            }

            int start = from + found;
            if (++checks > 16)
            {
                decided = false;
                return 0;
            }

            int k = text.Length - start;
            if (text.Slice(start).SequenceEqual(pattern.Slice(0, k)))
            {
                return k;
            }

            from = start + 1;
        }

        return 0;
    }

    // Knuth-Morris-Pratt: failure links over the pattern, then one pass over the text; the state at the end is the overlap.
    private static int OverlapKmp(TokenText text, int from, int to, TokenText pattern, int pFrom, int pTo, TokenEquality equality)
    {
        int m = pTo - pFrom;
        // Bounded: at most 128 ints (512 bytes) on the stack; longer patterns use the heap.
        Span<int> failure = m <= 128 ? stackalloc int[m] : new int[m];
        int k = 0;
        for (int q = 1; q < m; q++)
        {
            while (k > 0 && !equality.Equal(pattern.Token(pFrom + q), pattern.Token(pFrom + k)))
            {
                k = failure[k - 1];
            }

            if (equality.Equal(pattern.Token(pFrom + q), pattern.Token(pFrom + k)))
            {
                k++;
            }

            failure[q] = k;
        }

        int state = 0;
        for (int t = from; t < to; t++)
        {
            if (state == m)
            {
                state = failure[state - 1];
            }

            while (state > 0 && !equality.Equal(text.Token(t), pattern.Token(pFrom + state)))
            {
                state = failure[state - 1];
            }

            if (equality.Equal(text.Token(t), pattern.Token(pFrom + state)))
            {
                state++;
            }
        }

        return state;
    }
}
