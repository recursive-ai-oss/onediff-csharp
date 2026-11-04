namespace OneDiff.Cleanup;

/// <summary>
/// The changed regions of a script (a deletion and/or an insertion between two equal runs) as a doubly linked list over token
/// ranges, so neighbouring regions can be merged by dropping the equality between them. Merging only turns equal tokens into
/// a deletion plus an insertion of the same tokens, so the result is always a valid script for the same sequences.
/// </summary>
internal sealed class ChangeRegions
{
    private readonly int[] oldStart;
    private readonly int[] oldEnd;
    private readonly int[] newStart;
    private readonly int[] newEnd;
    private readonly int[] next;
    private readonly int[] previous;
    private readonly bool[] alive;
    private readonly int count;

    private ChangeRegions(int count)
    {
        this.count = count;
        oldStart = new int[count];
        oldEnd = new int[count];
        newStart = new int[count];
        newEnd = new int[count];
        next = new int[count];
        previous = new int[count];
        alive = new bool[count];
        for (int i = 0; i < count; i++)
        {
            next[i] = i + 1 < count ? i + 1 : -1;
            previous[i] = i - 1;
            alive[i] = true;
        }
    }

    public int Count => count;

    public static ChangeRegions From(EditScript script)
    {
        // A region is a maximal run of non-equal edits; count them, then fill.
        ReadOnlySpan<Edit> edits = script.Edits;
        int regions = 0;
        for (int i = 0; i < edits.Length; i++)
        {
            if (edits[i].Kind != EditKind.Equal && (i == 0 || edits[i - 1].Kind == EditKind.Equal))
            {
                regions++;
            }
        }

        var result = new ChangeRegions(regions);
        int r = -1;
        for (int i = 0; i < edits.Length; i++)
        {
            Edit e = edits[i];
            if (e.Kind == EditKind.Equal)
            {
                continue;
            }

            if (i == 0 || edits[i - 1].Kind == EditKind.Equal)
            {
                r++;
                result.oldStart[r] = e.OldStart;
                result.newStart[r] = e.NewStart;
            }

            result.oldEnd[r] = e.OldEnd;
            result.newEnd[r] = e.NewEnd;
        }

        return result;
    }

    /// <summary>Which rule decides that an equality between two changes is not worth keeping.</summary>
    public enum Rule { Semantic, Efficiency }

    /// <summary>
    /// Merges regions across the equalities <paramref name="rule"/> calls trivial, until none is left. A merge can make the
    /// neighbouring equalities trivial in turn, so after each merge the walk steps back one region. Every merge removes a region
    /// and every step back is paid for by one, so the work is linear in the number of regions.
    /// </summary>
    public void Eliminate(TokenText oldText, TokenText newText, Rule rule, int editCost)
    {
        // Walk left to right; after a merge step back one region, because the equality before the merged region may have become trivial.
        int left = count > 0 ? 0 : -1;
        while (left >= 0)
        {
            int right = next[left];
            if (right < 0)
            {
                break;
            }

            if (!IsTrivial(oldText, newText, left, right, rule, editCost))
            {
                left = right;
                continue;
            }

            // Merge `right` into `left`: the equality between them becomes part of the deletion and of the insertion.
            oldEnd[left] = oldEnd[right];
            newEnd[left] = newEnd[right];
            next[left] = next[right];
            if (next[right] >= 0)
            {
                previous[next[right]] = left;
            }

            alive[right] = false;
            if (previous[left] >= 0)
            {
                left = previous[left];
            }
        }
    }

    private bool IsTrivial(TokenText oldText, TokenText newText, int left, int right, Rule rule, int editCost)
    {
        int equal = oldText.Chars(oldEnd[left], oldStart[right]);
        int delLeft = oldText.Chars(oldStart[left], oldEnd[left]);
        int insLeft = newText.Chars(newStart[left], newEnd[left]);
        int delRight = oldText.Chars(oldStart[right], oldEnd[right]);
        int insRight = newText.Chars(newStart[right], newEnd[right]);

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
    /// Writes the regions back as a script. With <paramref name="extractOverlap"/>, a region that deletes text which the
    /// insertion then starts with (or the reverse) keeps that shared text as an equality, e.g. <c>-abcxxx +xxxdef</c> becomes
    /// <c>-abc =xxx +def</c>, when the shared text is at least half of the shorter side.
    /// </summary>
    public EditScript ToScript(TokenText oldText, TokenText newText, TokenEquality equality, bool extractOverlap, int oldLength, int newLength)
    {
        var builder = new EditScriptBuilder();
        int oldPos = 0;
        for (int r = FirstAlive(); r >= 0; r = next[r])
        {
            builder.Equal(oldStart[r] - oldPos);
            int dels = oldEnd[r] - oldStart[r];
            int ins = newEnd[r] - newStart[r];
            int overlapAfterDelete = 0;
            int overlapAfterInsert = 0;
            if (extractOverlap && dels > 0 && ins > 0)
            {
                // Longest suffix of the deletion that starts the insertion, and the mirror image.
                int k1 = Overlap(oldText, oldStart[r], oldEnd[r], newText, newStart[r], newEnd[r], equality);
                int k2 = Overlap(newText, newStart[r], newEnd[r], oldText, oldStart[r], oldEnd[r], equality);
                int shorter = Math.Min(oldText.Chars(oldStart[r], oldEnd[r]), newText.Chars(newStart[r], newEnd[r]));
                int chars1 = k1 > 0 ? oldText.Chars(oldEnd[r] - k1, oldEnd[r]) : 0;
                int chars2 = k2 > 0 ? newText.Chars(newEnd[r] - k2, newEnd[r]) : 0;
                if (chars1 >= chars2 && k1 > 0 && 2 * chars1 >= shorter)
                {
                    overlapAfterDelete = k1;
                }
                else if (k2 > 0 && 2 * chars2 >= shorter)
                {
                    overlapAfterInsert = k2;
                }
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

            oldPos = oldEnd[r];
        }

        builder.Equal(oldLength - oldPos);
        EditScript script = builder.ToEditScript();
        return script.OldLength == oldLength && script.NewLength == newLength
            ? script
            : throw new InvalidOperationException("cleanup produced a script of the wrong size");
    }

    private int FirstAlive()
    {
        for (int i = 0; i < count; i++)
        {
            if (alive[i])
            {
                return i;
            }
        }

        return -1;
    }

    // Longest suffix of text[from..to) that is a prefix of pattern[pFrom..pTo), in tokens. Knuth-Morris-Pratt: failure links over
    // the pattern, then one pass over the text; the state at the end is the overlap.
    private static int Overlap(TokenText text, int from, int to, TokenText pattern, int pFrom, int pTo, TokenEquality equality)
    {
        int m = pTo - pFrom;
        int[] failure = new int[m];
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
