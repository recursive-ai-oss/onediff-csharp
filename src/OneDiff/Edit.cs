namespace OneDiff;

/// <summary>What an <see cref="Edit"/> does to the old sequence to reach the new one.</summary>
public enum EditKind : byte
{
    /// <summary>The same items sit in both sequences.</summary>
    Equal = 0,

    /// <summary>Items present in the old sequence only.</summary>
    Delete = 1,

    /// <summary>Items present in the new sequence only.</summary>
    Insert = 2,
}

/// <summary>
/// One run of an edit script: a half-open range of the old sequence and a half-open range of the new sequence, in item
/// (token) indices. Indices count items of whatever sequence was compared (lines, words, characters, ids), never UTF-16 offsets,
/// unless the sequence is characters.
/// </summary>
/// <remarks>
/// The shape is fixed by the kind. <see cref="EditKind.Equal"/>: both lengths equal and positive. <see cref="EditKind.Delete"/>:
/// <see cref="OldLength"/> positive and <see cref="NewLength"/> zero, with <see cref="NewStart"/> the place in the new sequence where
/// the deletion happened. <see cref="EditKind.Insert"/>: <see cref="NewLength"/> positive and <see cref="OldLength"/> zero, with
/// <see cref="OldStart"/> the place in the old sequence where the insertion happened. A replacement is a Delete followed by an Insert.
/// </remarks>
/// <param name="Kind">What the run does.</param>
/// <param name="OldStart">First index of the run in the old sequence.</param>
/// <param name="OldLength">Number of old items in the run.</param>
/// <param name="NewStart">First index of the run in the new sequence.</param>
/// <param name="NewLength">Number of new items in the run.</param>
public readonly record struct Edit(EditKind Kind, int OldStart, int OldLength, int NewStart, int NewLength)
{
    /// <summary>Index one past the run in the old sequence.</summary>
    public int OldEnd => OldStart + OldLength;

    /// <summary>Index one past the run in the new sequence.</summary>
    public int NewEnd => NewStart + NewLength;

    /// <summary>Creates an equal run of <paramref name="length"/> items starting at the given indices.</summary>
    public static Edit Equal(int oldStart, int newStart, int length) => new(EditKind.Equal, oldStart, length, newStart, length);

    /// <summary>Creates a deletion of <paramref name="length"/> old items; <paramref name="newStart"/> is where it happened in the new sequence.</summary>
    public static Edit Delete(int oldStart, int length, int newStart) => new(EditKind.Delete, oldStart, length, newStart, 0);

    /// <summary>Creates an insertion of <paramref name="length"/> new items; <paramref name="oldStart"/> is where it happened in the old sequence.</summary>
    public static Edit Insert(int oldStart, int newStart, int length) => new(EditKind.Insert, oldStart, 0, newStart, length);
}
