using OneDiff.Algorithms;

namespace OneDiff;

/// <summary>
/// Immutable settings for one diff: which algorithm finds the alignment and how much work it may spend. Create variations with
/// <c>with</c>. The same two knobs <see cref="Merge.MergeSettings"/> has for three-way merges.
/// </summary>
public sealed record DiffSettings
{
    /// <summary>The settings used when none are given: Myers, with the default work cap.</summary>
    public static DiffSettings Default { get; } = new();

    /// <summary>The algorithm that finds the alignment. <see cref="DiffAlgorithm.Myers"/> by default.</summary>
    public DiffAlgorithm Algorithm { get; init; } = DiffAlgorithm.Myers;

    /// <summary>
    /// The most work the search may spend, counted as in <see cref="MyersDiff"/> (edit-graph steps; see <see cref="DiffDiagnostic.Work"/>).
    /// 2^31 (about 6 seconds) by default; zero or more. When the work runs out the script is still valid, only coarser, and
    /// <see cref="TextDiff.Diagnostic"/> says so.
    /// </summary>
    public long MaxWork { get; init; } = Budget.DefaultCap;
}
