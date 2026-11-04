namespace OneDiff;

/// <summary>
/// Assembles an <see cref="EditScript"/> from a walk over the two sequences. Algorithms report what they pass (<see cref="Equal"/>,
/// <see cref="Delete"/>, <see cref="Insert"/>) and the builder keeps the script canonical: zero counts are ignored, neighbours of
/// one kind merge, and the deletes and inserts between two equal runs come out deletion first however they were reported.
/// </summary>
public sealed class EditScriptBuilder
{
    private readonly List<Edit> edits = [];
    private int oldPos;
    private int newPos;
    private int pendingDeletes;
    private int pendingInserts;

    /// <summary>Items of the old sequence passed so far.</summary>
    public int OldLength => oldPos + pendingDeletes;

    /// <summary>Items of the new sequence passed so far.</summary>
    public int NewLength => newPos + pendingInserts;

    /// <summary>Passes <paramref name="count"/> items present in both sequences. Throws when negative.</summary>
    public void Equal(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return;
        }

        Flush();
        int last = edits.Count - 1;
        if (last >= 0 && edits[last].Kind == EditKind.Equal)
        {
            Edit prev = edits[last];
            edits[last] = Edit.Equal(prev.OldStart, prev.NewStart, prev.OldLength + count);
        }
        else
        {
            edits.Add(Edit.Equal(oldPos, newPos, count));
        }

        oldPos += count;
        newPos += count;
    }

    /// <summary>Passes <paramref name="count"/> items present in the old sequence only. Throws when negative.</summary>
    public void Delete(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        pendingDeletes += count;
    }

    /// <summary>Passes <paramref name="count"/> items present in the new sequence only. Throws when negative.</summary>
    public void Insert(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        pendingInserts += count;
    }

    /// <summary>Finishes the script. The builder can keep being used; later calls continue after what was already reported.</summary>
    public EditScript ToEditScript()
    {
        Flush();
        return new EditScript(edits.ToArray(), oldPos, newPos);
    }

    private void Flush()
    {
        if (pendingDeletes > 0)
        {
            edits.Add(Edit.Delete(oldPos, pendingDeletes, newPos));
            oldPos += pendingDeletes;
            pendingDeletes = 0;
        }

        if (pendingInserts > 0)
        {
            edits.Add(Edit.Insert(oldPos, newPos, pendingInserts));
            newPos += pendingInserts;
            pendingInserts = 0;
        }
    }
}
