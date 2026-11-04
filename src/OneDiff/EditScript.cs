namespace OneDiff;

/// <summary>
/// The result of comparing an old sequence with a new one: an immutable list of <see cref="Edit"/> runs that together walk both
/// sequences from start to end.
/// </summary>
/// <remarks>
/// <para>A valid script is in canonical form: edits are contiguous in both sequences and cover them exactly, none is empty, two
/// neighbours never share a kind, and within a changed region the deletion comes before the insertion. Canonical form means two
/// algorithms that find the same alignment produce scripts that compare equal item by item.</para>
/// <para>Scripts are values: instances are immutable and safe to share between threads. Equality is by reference; compare
/// <see cref="Edits"/> to compare content.</para>
/// </remarks>
public sealed class EditScript : IReadOnlyList<Edit>
{
    private readonly Edit[] edits;

    // The caller (builder or validated factory) guarantees canonical form.
    internal EditScript(Edit[] edits, int oldLength, int newLength)
    {
        this.edits = edits;
        OldLength = oldLength;
        NewLength = newLength;
    }

    /// <summary>Number of items in the old sequence the script covers.</summary>
    public int OldLength { get; }

    /// <summary>Number of items in the new sequence the script covers.</summary>
    public int NewLength { get; }

    /// <summary>Number of runs.</summary>
    public int Count => edits.Length;

    /// <summary>The run at <paramref name="index"/>. Throws <see cref="IndexOutOfRangeException"/> when out of range.</summary>
    public Edit this[int index] => edits[index];

    /// <summary>The runs, in order. The span is a view of the script's own storage and cannot change it.</summary>
    public ReadOnlySpan<Edit> Edits => edits;

    /// <summary>True when the sequences are identical (no insert or delete runs), including when both are empty.</summary>
    public bool IsIdentical => edits.Length == 0 || (edits.Length == 1 && edits[0].Kind == EditKind.Equal);

    /// <summary>Total number of old items deleted.</summary>
    public int DeletedCount => Sum(EditKind.Delete);

    /// <summary>Total number of new items inserted.</summary>
    public int InsertedCount => Sum(EditKind.Insert);

    /// <summary>The edit distance in the insert/delete model: deleted items plus inserted items.</summary>
    public int EditDistance => DeletedCount + InsertedCount;

    /// <summary>
    /// Builds a script from runs, checking every invariant. Throws <see cref="ArgumentException"/> naming the first violation when
    /// the runs are not a canonical script for sequences of the given lengths.
    /// </summary>
    /// <param name="edits">The runs, in order. Copied.</param>
    /// <param name="oldLength">Length of the old sequence. Not negative.</param>
    /// <param name="newLength">Length of the new sequence. Not negative.</param>
    public static EditScript Create(ReadOnlySpan<Edit> edits, int oldLength, int newLength)
    {
        if (!TryValidate(edits, oldLength, newLength, out string? error))
        {
            throw new ArgumentException(error, nameof(edits));
        }

        return new EditScript(edits.ToArray(), oldLength, newLength);
    }

    /// <summary>The script for two identical sequences of <paramref name="length"/> items (empty when the length is zero).</summary>
    public static EditScript Identical(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new EditScript(length == 0 ? [] : [Edit.Equal(0, 0, length)], length, length);
    }

    /// <summary>
    /// Checks the canonical-form invariants without throwing. On failure <paramref name="error"/> says which run broke which rule.
    /// </summary>
    public static bool TryValidate(ReadOnlySpan<Edit> edits, int oldLength, int newLength, out string? error)
    {
        if (oldLength < 0 || newLength < 0)
        {
            error = "sequence lengths cannot be negative";
            return false;
        }

        int oldPos = 0, newPos = 0;
        for (int i = 0; i < edits.Length; i++)
        {
            Edit e = edits[i];
            if (e.OldStart != oldPos || e.NewStart != newPos)
            {
                error = $"edit {i} starts at ({e.OldStart}, {e.NewStart}) but the previous one ended at ({oldPos}, {newPos})";
                return false;
            }

            bool shapeOk = e.Kind switch
            {
                EditKind.Equal => e.OldLength > 0 && e.OldLength == e.NewLength,
                EditKind.Delete => e.OldLength > 0 && e.NewLength == 0,
                EditKind.Insert => e.OldLength == 0 && e.NewLength > 0,
                _ => false,
            };
            if (!shapeOk)
            {
                error = $"edit {i} ({e.Kind}) has lengths ({e.OldLength}, {e.NewLength}) that do not fit its kind";
                return false;
            }

            if (i > 0)
            {
                EditKind prev = edits[i - 1].Kind;
                if (prev == e.Kind)
                {
                    error = $"edits {i - 1} and {i} are both {e.Kind} and should be one run";
                    return false;
                }

                if (prev == EditKind.Insert && e.Kind == EditKind.Delete)
                {
                    error = $"edit {i} deletes right after an insert; a changed region lists its deletion first";
                    return false;
                }
            }

            oldPos += e.OldLength;
            newPos += e.NewLength;
        }

        if (oldPos != oldLength || newPos != newLength)
        {
            error = $"edits cover ({oldPos}, {newPos}) items but the sequences have ({oldLength}, {newLength})";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Reproduces the new sequence: equal runs are copied from <paramref name="oldItems"/>, inserted runs from <paramref name="newItems"/>.
    /// Equal runs are not compared; use <see cref="Matches{T}"/> to check them. Throws <see cref="ArgumentException"/> when either
    /// sequence has a different length from the one the script covers.
    /// </summary>
    public T[] Apply<T>(ReadOnlySpan<T> oldItems, ReadOnlySpan<T> newItems)
    {
        CheckLengths(oldItems.Length, newItems.Length);
        T[] result = new T[NewLength];
        int written = 0;
        foreach (Edit e in edits)
        {
            switch (e.Kind)
            {
                case EditKind.Equal:
                    oldItems.Slice(e.OldStart, e.OldLength).CopyTo(result.AsSpan(written));
                    written += e.OldLength;
                    break;
                case EditKind.Insert:
                    newItems.Slice(e.NewStart, e.NewLength).CopyTo(result.AsSpan(written));
                    written += e.NewLength;
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Reproduces the old sequence from the new one by undoing the script: equal runs are copied from <paramref name="newItems"/>,
    /// deleted runs from <paramref name="oldItems"/>. The mirror of <see cref="Apply{T}"/>.
    /// </summary>
    public T[] Unapply<T>(ReadOnlySpan<T> oldItems, ReadOnlySpan<T> newItems)
    {
        CheckLengths(oldItems.Length, newItems.Length);
        T[] result = new T[OldLength];
        int written = 0;
        foreach (Edit e in edits)
        {
            switch (e.Kind)
            {
                case EditKind.Equal:
                    newItems.Slice(e.NewStart, e.NewLength).CopyTo(result.AsSpan(written));
                    written += e.NewLength;
                    break;
                case EditKind.Delete:
                    oldItems.Slice(e.OldStart, e.OldLength).CopyTo(result.AsSpan(written));
                    written += e.OldLength;
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// True when every equal run really is equal: <c>old[e.OldStart + k]</c> equals <c>new[e.NewStart + k]</c> under
    /// <paramref name="comparer"/> (the default comparer when null). Throws <see cref="ArgumentException"/> on a length mismatch.
    /// Together with <see cref="Apply{T}"/> this is the round-trip check every algorithm must pass.
    /// </summary>
    public bool Matches<T>(ReadOnlySpan<T> oldItems, ReadOnlySpan<T> newItems, IEqualityComparer<T>? comparer = null)
    {
        CheckLengths(oldItems.Length, newItems.Length);
        comparer ??= EqualityComparer<T>.Default;
        foreach (Edit e in edits)
        {
            if (e.Kind != EditKind.Equal)
            {
                continue;
            }

            for (int k = 0; k < e.OldLength; k++)
            {
                if (!comparer.Equals(oldItems[e.OldStart + k], newItems[e.NewStart + k]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The script that turns the new sequence back into the old one: inserts become deletes and the reverse.</summary>
    public EditScript Invert()
    {
        var builder = new EditScriptBuilder();
        foreach (Edit e in edits)
        {
            switch (e.Kind)
            {
                case EditKind.Equal: builder.Equal(e.OldLength); break;
                case EditKind.Delete: builder.Insert(e.OldLength); break;
                case EditKind.Insert: builder.Delete(e.NewLength); break;
            }
        }

        return builder.ToEditScript();
    }

    /// <summary>Enumerates the runs in order.</summary>
    public IEnumerator<Edit> GetEnumerator() => ((IEnumerable<Edit>)edits).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private int Sum(EditKind kind)
    {
        int total = 0;
        foreach (Edit e in edits)
        {
            if (e.Kind == kind)
            {
                total += kind == EditKind.Delete ? e.OldLength : e.NewLength;
            }
        }

        return total;
    }

    private void CheckLengths(int oldItems, int newItems)
    {
        if (oldItems != OldLength || newItems != NewLength)
        {
            throw new ArgumentException($"the script covers ({OldLength}, {NewLength}) items but was given ({oldItems}, {newItems})");
        }
    }
}
