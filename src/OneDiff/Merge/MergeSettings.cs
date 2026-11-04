namespace OneDiff.Merge;

/// <summary>Immutable settings for a three-way merge. Create variations with <c>with</c>.</summary>
public sealed record MergeSettings
{
    /// <summary>The settings used when none are given: <see cref="ConflictStyle.Merge"/>, conflicts left marked, Myers, git's labels.</summary>
    public static MergeSettings Default { get; } = new();

    /// <summary>How conflicts are laid out. <see cref="ConflictStyle.Merge"/> by default.</summary>
    public ConflictStyle Style { get; init; } = ConflictStyle.Merge;

    /// <summary>
    /// Settles conflicts automatically. <see cref="ConflictResolution.None"/> by default. With anything else the merged text has no
    /// markers and the conflict count is zero; the regions still report where the sides disagreed.
    /// </summary>
    public ConflictResolution Resolution { get; init; } = ConflictResolution.None;

    /// <summary>The algorithm that aligns each side with the base (and, narrowing conflicts, the sides with each other). Myers by default.</summary>
    public DiffAlgorithm Algorithm { get; init; } = DiffAlgorithm.Myers;

    /// <summary>The text after <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> on our side of a conflict. "ours" by default; null or empty prints the marker alone.</summary>
    public string? OursLabel { get; init; } = "ours";

    /// <summary>The text after <c>|||||||</c> on the base section of a conflict. "base" by default; null or empty prints the marker alone.</summary>
    public string? BaseLabel { get; init; } = "base";

    /// <summary>The text after <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> on their side of a conflict. "theirs" by default; null or empty prints the marker alone.</summary>
    public string? TheirsLabel { get; init; } = "theirs";

    /// <summary>How many characters each marker has: 7 by default (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>). Must be at least 1.</summary>
    public int MarkerSize { get; init; } = 7;
}
