namespace OneDiff.Algorithms;

/// <summary>
/// The simplest valid script: trim the common prefix and suffix and treat everything between as one deletion plus one insertion.
/// It is correct (it always round-trips) but not minimal. The algorithm entry points stand on it until they have their own search.
/// </summary>
internal static class PrefixSuffixDiff
{
    public static EditScript Compute(ReadOnlySpan<int> oldIds, ReadOnlySpan<int> newIds)
    {
        int max = Math.Min(oldIds.Length, newIds.Length);
        int prefix = 0;
        while (prefix < max && oldIds[prefix] == newIds[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < max - prefix && oldIds[oldIds.Length - 1 - suffix] == newIds[newIds.Length - 1 - suffix])
        {
            suffix++;
        }

        var builder = new EditScriptBuilder();
        builder.Equal(prefix);
        builder.Delete(oldIds.Length - prefix - suffix);
        builder.Insert(newIds.Length - prefix - suffix);
        builder.Equal(suffix);
        return builder.ToEditScript();
    }
}
