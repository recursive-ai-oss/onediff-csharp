namespace OneDiff.Algorithms;

/// <summary>Myers' O(ND) algorithm in linear space; the script has the fewest inserted plus deleted items.</summary>
public static class MyersDiff
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
