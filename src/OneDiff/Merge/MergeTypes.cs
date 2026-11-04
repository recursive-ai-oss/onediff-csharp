namespace OneDiff.Merge;

/// <summary>How conflicts are laid out in merged text. The names and layouts follow git's conflict styles.</summary>
public enum ConflictStyle
{
    /// <summary>
    /// Two sections, ours and theirs. Each conflict is narrowed first: lines both sides share inside it are moved out, and conflicts
    /// separated by only a few shared lines are joined again, so a conflict reads as one coherent block.
    /// </summary>
    Merge = 0,

    /// <summary>Three sections: ours, the common ancestor (base), then theirs. Conflicts are shown exactly as the merge found them.</summary>
    Diff3 = 1,

    /// <summary>Like <see cref="Diff3"/>, but lines that open or close both sides identically are moved out of the conflict.</summary>
    ZDiff3 = 2,
}

/// <summary>How conflicts are settled automatically instead of being marked.</summary>
public enum ConflictResolution
{
    /// <summary>Leave conflicts in place, marked according to the <see cref="ConflictStyle"/>.</summary>
    None = 0,

    /// <summary>Take our side of every conflict.</summary>
    Ours = 1,

    /// <summary>Take their side of every conflict.</summary>
    Theirs = 2,

    /// <summary>Take both sides of every conflict, ours first.</summary>
    Union = 3,
}

/// <summary>What happened to one stretch of the merged sequence, from the three inputs' point of view.</summary>
public enum MergeRegionKind
{
    /// <summary>All three inputs agree. Every range holds the same items.</summary>
    Unchanged = 0,

    /// <summary>Only our side changed this stretch. The merge takes ours.</summary>
    OursOnly = 1,

    /// <summary>Only their side changed this stretch. The merge takes theirs.</summary>
    TheirsOnly = 2,

    /// <summary>
    /// Both sides changed the stretch, and to the same items (a "false conflict"). The merge takes either; the ours and theirs
    /// ranges hold equal items.
    /// </summary>
    BothSame = 3,

    /// <summary>Both sides changed the stretch, differently. The three ranges are what each input holds there.</summary>
    Conflict = 4,
}

/// <summary>Names one of the three merge inputs.</summary>
public enum MergeSide
{
    /// <summary>The common ancestor.</summary>
    Base = 0,

    /// <summary>Our version.</summary>
    Ours = 1,

    /// <summary>Their version.</summary>
    Theirs = 2,
}

/// <summary>A half-open range of items (lines, for text merges) in one input.</summary>
/// <param name="Start">Index of the first item.</param>
/// <param name="Length">Number of items; zero for an empty range at <see cref="Start"/>.</param>
public readonly record struct ItemRange(int Start, int Length)
{
    /// <summary>Index one past the last item.</summary>
    public int End => Start + Length;
}

/// <summary>
/// One stretch of a three-way merge. The regions of a merge are in order and tile each input exactly: the ranges of one side,
/// taken region by region, are adjacent and cover the whole input.
/// </summary>
/// <param name="Kind">What happened to the stretch.</param>
/// <param name="Base">The stretch in the common ancestor.</param>
/// <param name="Ours">The stretch in our version.</param>
/// <param name="Theirs">The stretch in their version.</param>
/// <remarks>
/// When a conflict was narrowed into several pieces (see <see cref="ConflictStyle.Merge"/> and <see cref="ConflictStyle.ZDiff3"/>),
/// the pieces share the conflict's base range: the first conflicting piece carries all of it, and the shared items around the
/// pieces are <see cref="MergeRegionKind.BothSame"/> regions with an empty base range. The regions still tile the base exactly.
/// </remarks>
public readonly record struct MergeRegion(MergeRegionKind Kind, ItemRange Base, ItemRange Ours, ItemRange Theirs);
