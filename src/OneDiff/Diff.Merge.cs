using OneDiff.Merge;

namespace OneDiff;

public static partial class Diff
{
    /// <summary>
    /// Three-way merge of text, line by line: applies the changes both <paramref name="ours"/> and <paramref name="theirs"/> made to
    /// <paramref name="baseText"/>, and marks the places where they disagree. Shorthand for <see cref="ThreeWayMerge.Lines"/>.
    /// </summary>
    /// <param name="baseText">The common ancestor.</param>
    /// <param name="ours">Our version.</param>
    /// <param name="theirs">Their version.</param>
    /// <param name="settings">Conflict style, resolution, labels and marker size; git's defaults when null.</param>
    /// <param name="cancellationToken">Observed while aligning; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    /// <exception cref="ArgumentNullException">A text is null.</exception>
    public static TextMerge Merge(string baseText, string ours, string theirs, MergeSettings? settings = null, CancellationToken cancellationToken = default)
        => ThreeWayMerge.Lines(baseText, ours, theirs, settings, cancellationToken);

    /// <summary>
    /// Three-way merge of arbitrary sequences, reported as regions. Shorthand for <see cref="ThreeWayMerge.Items{T}"/>.
    /// </summary>
    /// <param name="baseItems">The common ancestor.</param>
    /// <param name="ours">Our version.</param>
    /// <param name="theirs">Their version.</param>
    /// <param name="settings">Conflict style, resolution and algorithm; defaults when null.</param>
    /// <param name="comparer">Decides which items are equal. The default comparer when null.</param>
    /// <param name="cancellationToken">Observed while aligning; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    public static ItemMerge Merge<T>(
        ReadOnlySpan<T> baseItems,
        ReadOnlySpan<T> ours,
        ReadOnlySpan<T> theirs,
        MergeSettings? settings = null,
        IEqualityComparer<T>? comparer = null,
        CancellationToken cancellationToken = default)
        where T : notnull
        => ThreeWayMerge.Items(baseItems, ours, theirs, settings, comparer, cancellationToken);
}
