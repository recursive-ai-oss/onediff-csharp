namespace OneDiff.Cleanup;

/// <summary>
/// Makes a computed <see cref="TextDiff"/> easier to read or cheaper to apply by rewriting its edit script. Every method returns a
/// new diff over the same text and tokens whose script still turns the old tokens into the new ones (equal runs only ever shrink
/// or slide onto identical tokens), and is in canonical form. Nothing is mutated; the methods are thread safe.
/// </summary>
public static class DiffCleanup
{
    /// <summary>
    /// Semantic cleanup: removes equalities too small to matter so a change reads as one replacement instead of a patchwork.
    /// </summary>
    /// <remarks>
    /// <para>The rules, in order:</para>
    /// <list type="number">
    /// <item>An equal run between two changed regions is dropped (its tokens become part of both the deletion and the insertion)
    /// when its length in characters is at most the larger of the deleted and inserted characters on its left, and also at most
    /// the larger on its right. Merging can make the equalities beside the merged region trivial, so the rule repeats until none is.</item>
    /// <item>In a region that deletes and inserts, text the deletion ends with and the insertion starts with (or the reverse) is
    /// kept as an equality when it is at least half of the shorter side.</item>
    /// <item><see cref="SemanticLosslessShift"/> aligns the remaining lone edits to word and line boundaries. It also runs once before the
    /// first rule, so edits that can slide into each other join first.</item>
    /// </list>
    /// <para>The result is generally not a minimal script. Equal runs at the very start and end are never dropped.</para>
    /// </remarks>
    /// <param name="diff">The diff to clean up.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diff"/> is null.</exception>
    public static TextDiff Semantic(TextDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        if (diff.Script.IsIdentical)
        {
            return diff;
        }

        var (oldText, newText, equality) = Prepare(diff);
        // Joining edits that can slide together first leaves fewer, larger regions for the elimination rule to judge. With a single
        // changed region there is nothing to join or merge, and only the overlap and boundary steps below can apply.
        int regionCount = ChangeRegions.CountRegions(diff.Script);
        TextDiff joined = regionCount > 1 ? With(diff, LosslessShift.Apply(diff, oldText, newText, equality, scoreMoves: false)) : diff;
        EditScript script = Regroup(joined.Script, oldText, newText, equality, ChangeRegions.Rule.Semantic, 0, extractOverlap: true);
        TextDiff merged = With(joined, script);
        return With(merged, LosslessShift.Apply(merged, oldText, newText, equality, scoreMoves: true));
    }

    // Merge trivial equalities (and keep overlaps) over the regions of `script`. The region buffer lives on the stack for scripts of up
    // to 64 regions (1 KB) and is a plain array above that.
    private static EditScript Regroup(EditScript script, TokenText oldText, TokenText newText, TokenEquality equality, ChangeRegions.Rule rule, int editCost, bool extractOverlap)
    {
        int regionCount = ChangeRegions.CountRegions(script);
        Span<int> buffer = regionCount <= 64 ? stackalloc int[ChangeRegions.Size(regionCount)] : new int[ChangeRegions.Size(regionCount)];
        int count = ChangeRegions.Collect(script, oldText, newText, rule, editCost, buffer, out bool merged);
        return ChangeRegions.ToScript(buffer, count, merged, oldText, newText, equality, extractOverlap, script);
    }

    /// <summary>
    /// Efficiency cleanup: removes equalities that cost more to keep than to rewrite, for a consumer that pays a fixed price per edit
    /// (a compact patch, a wire format).
    /// </summary>
    /// <remarks>
    /// The cost model: each deletion run or insertion run costs <paramref name="editCost"/>, each character costs 1. An equality
    /// between two changed regions is dropped when merging the regions (one deletion and one insertion, with the equal text added to
    /// both) is cheaper than keeping them apart: with four runs around the equality that is when its length is below
    /// <paramref name="editCost"/>; with three runs, below half of it; with two, never. The rule repeats as merges expose new
    /// candidates. Afterwards, edits that can slide across a whole equality to join the next change do so (same cost, fewer runs). Unlike <see cref="Semantic"/> it does not score boundaries or look for overlaps.
    /// </remarks>
    /// <param name="diff">The diff to clean up.</param>
    /// <param name="editCost">The cost of one edit run in characters. 4 when omitted; 0 changes nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diff"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="editCost"/> is negative.</exception>
    public static TextDiff Efficiency(TextDiff diff, int editCost = 4)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentOutOfRangeException.ThrowIfNegative(editCost);
        if (diff.Script.IsIdentical || editCost == 0)
        {
            return diff;
        }

        // Merging needs two regions, and joining across an equality needs another change beyond it: a single region is already done.
        if (ChangeRegions.CountRegions(diff.Script) < 2)
        {
            return diff;
        }

        var (oldText, newText, equality) = Prepare(diff);
        EditScript script = Regroup(diff.Script, oldText, newText, equality, ChangeRegions.Rule.Efficiency, editCost, extractOverlap: false);
        TextDiff merged = With(diff, script);
        return With(merged, LosslessShift.Apply(merged, oldText, newText, equality, scoreMoves: false));
    }

    /// <summary>
    /// Slides each lone insertion or deletion (one with equal text on both sides) along that text to the position that starts and
    /// ends on the most natural boundaries: text ends, blank lines, line starts, sentence starts, word edges, in that order of
    /// preference. Sliding never changes what is inserted or deleted or the number of edits, so the script's cost is unchanged.
    /// </summary>
    /// <remarks>
    /// Ties go to the leftmost position. One token of each neighbouring equal run stays, except that an edit which can slide across a whole
    /// equal run to touch the next change does so and joins it (same cost, one region fewer).
    /// </remarks>
    /// <param name="diff">The diff to adjust.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diff"/> is null.</exception>
    public static TextDiff SemanticLosslessShift(TextDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var (oldText, newText, equality) = Prepare(diff);
        return With(diff, LosslessShift.Apply(diff, oldText, newText, equality, scoreMoves: true));
    }

    private static (TokenText Old, TokenText New, TokenEquality Equality) Prepare(TextDiff diff) =>
        (new TokenText(diff.OldText, diff.OldTokenRanges), new TokenText(diff.NewText, diff.NewTokenRanges), new TokenEquality(diff.Comparer));

    private static TextDiff With(TextDiff diff, EditScript script) =>
        ReferenceEquals(script, diff.Script)
            ? diff
            : new TextDiff(diff.OldText, diff.NewText, diff.Kind, diff.OldTokenRanges, diff.NewTokenRanges, script, diff.Comparer, diff.Diagnostic);

    // Anything that alters the script goes through With, so a pass that changes nothing returns the diff it was given.
}
