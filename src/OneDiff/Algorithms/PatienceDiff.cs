namespace OneDiff.Algorithms;

/// <summary>
/// Patience diff, from the published write-ups of Bram Cohen's idea: items that occur exactly once in each range are anchors,
/// the longest run of them that keeps their order (a longest increasing subsequence) is matched, and the stretches between
/// anchors are compared the same way, falling back to Myers where a stretch has no unique common item.
/// </summary>
/// <remarks>
/// The script is always valid but not necessarily minimal. It is deterministic: ties are broken by position, earliest first.
/// </remarks>
public static class PatienceDiff
{
    /// <summary>
    /// Compares two sequences of ids (see <see cref="TokenInterner{T}"/>) and returns a canonical <see cref="EditScript"/>
    /// that turns <paramref name="oldIds"/> into <paramref name="newIds"/>. Equal ids are equal items. Uses the default work cap
    /// (about two billion steps), beyond which the unsearched remainder becomes one deletion plus one insertion.
    /// </summary>
    /// <param name="oldIds">The old sequence.</param>
    /// <param name="newIds">The new sequence.</param>
    /// <param name="cancellationToken">Observed while searching; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    public static EditScript Compute(ReadOnlySpan<int> oldIds, ReadOnlySpan<int> newIds, CancellationToken cancellationToken = default)
        => Compute(oldIds, newIds, Budget.DefaultCap, out _, cancellationToken);

    /// <summary>
    /// Like the overload without a cap, but with an explicit bound on the work and a diagnostic about the run. When the work spent
    /// exceeds <paramref name="maxWork"/> the unsearched remainder becomes one deletion plus one insertion and
    /// <see cref="DiffDiagnostic.CostCapReached"/> is set; the script stays valid.
    /// </summary>
    /// <param name="oldIds">The old sequence.</param>
    /// <param name="newIds">The new sequence.</param>
    /// <param name="maxWork">Most steps to spend; <see cref="long.MaxValue"/> for no bound.</param>
    /// <param name="diagnostic">Receives whether the cap was reached and how much work was spent.</param>
    /// <param name="cancellationToken">Observed while searching; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxWork"/> is negative.</exception>
    public static EditScript Compute(ReadOnlySpan<int> oldIds, ReadOnlySpan<int> newIds, long maxWork, out DiffDiagnostic diagnostic, CancellationToken cancellationToken = default)
        => AnchoredDiff.Run(oldIds, newIds, AnchoredDiff.Mode.Patience, maxWork, out diagnostic, cancellationToken);
}
