namespace OneDiff.Algorithms;

/// <summary>Patience diff: anchors on items that occur exactly once on each side.</summary>
public static class PatienceDiff
{
    /// <summary>
    /// Compares two sequences of ids (see <see cref="TokenInterner{T}"/>) and returns a canonical <see cref="EditScript"/>
    /// that turns <paramref name="oldIds"/> into <paramref name="newIds"/>. Equal ids are equal items.
    /// </summary>
    /// <param name="oldIds">The old sequence.</param>
    /// <param name="newIds">The new sequence.</param>
    /// <param name="cancellationToken">Observed while searching; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    public static EditScript Compute(ReadOnlySpan<int> oldIds, ReadOnlySpan<int> newIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Only common-prefix/suffix trimming so far: valid, not yet the real search.
        return PrefixSuffixDiff.Compute(oldIds, newIds);
    }
}
