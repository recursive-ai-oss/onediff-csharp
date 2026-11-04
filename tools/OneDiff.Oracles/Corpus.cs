namespace OneDiff.Oracles;

/// <summary>One input to a gate: an id that is stable across runs and the files that make it up.</summary>
internal sealed record Case(string Id, string[] Files);

/// <summary>Walks the corpus directories in a fixed (ordinal) order.</summary>
internal static class Corpus
{
    /// <summary>Version pairs: <c>text/&lt;source&gt;/&lt;n&gt;/{old,new}</c>.</summary>
    public static List<Case> TextCases(string root, string? sourceFilter)
        => Enumerate(root, "text", ["old", "new"], sourceFilter);

    /// <summary>Three-way triples: <c>merge/&lt;repo&gt;/&lt;n&gt;/{base,ours,theirs}</c>.</summary>
    public static List<Case> MergeCases(string root, string? sourceFilter)
        => Enumerate(root, "merge", ["base", "ours", "theirs"], sourceFilter);

    private static List<Case> Enumerate(string root, string kind, string[] names, string? sourceFilter)
    {
        var cases = new List<Case>();
        string top = Path.Combine(root, kind);
        if (!Directory.Exists(top))
        {
            return cases;
        }

        foreach (string source in Directory.EnumerateDirectories(top).OrderBy(d => d, StringComparer.Ordinal))
        {
            string sourceName = Path.GetFileName(source);
            if (sourceFilter is not null && !sourceName.StartsWith(sourceFilter, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string dir in Directory.EnumerateDirectories(source).OrderBy(d => d, StringComparer.Ordinal))
            {
                string[] files = names.Select(n => Path.Combine(dir, n)).ToArray();
                if (files.All(File.Exists))
                {
                    cases.Add(new Case($"{kind}/{sourceName}/{Path.GetFileName(dir)}", files));
                }
            }
        }

        return cases;
    }
}
