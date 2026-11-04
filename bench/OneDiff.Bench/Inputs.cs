namespace OneDiff.Bench;

/// <summary>The sizes every row is run at. Inputs are real corpus pairs of about this many characters.</summary>
internal static class Sizes
{
    public const string Small = "Small";
    public const string Medium = "Medium";
    public const string Large = "Large";

    public static int Target(string size) => size switch
    {
        Small => 2_000,
        Medium => 40_000,
        Large => 400_000,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };
}

/// <summary>One old/new pair read from the corpus at run time. Corpus files are never part of the repository.</summary>
internal sealed record Pair(string Name, string Old, string New);

/// <summary>
/// Picks the corpus pair whose larger side is closest (in ratio) to a target size, breaking ties by directory order, so the
/// same corpus always gives the same inputs. Pairs with identical sides are skipped.
/// </summary>
internal static class Inputs
{
    public static string CorpusRoot()
    {
        if (Environment.GetEnvironmentVariable("ONEDIFF_CORPORA") is { Length: > 0 } c)
        {
            return c;
        }

        string home = Environment.GetEnvironmentVariable("ONEDIFF_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "onediff");
        return Path.Combine(home, "corpora");
    }

    /// <summary>Prose for the word and character rows, code for the line rows.</summary>
    public static string SourceFor(string area) => area == "lines" ? "git-" : "wiki-en";

    public static Pair Pick(string sourcePrefix, string size)
    {
        string text = Path.Combine(CorpusRoot(), "text");
        if (!Directory.Exists(text))
        {
            throw new DirectoryNotFoundException($"corpus not found at {text}; run tools/fetch-corpora.py or set ONEDIFF_CORPORA");
        }

        double target = Sizes.Target(size);
        var candidates = new List<(double Score, string Dir)>();
        foreach (string source in Directory.EnumerateDirectories(text).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!Path.GetFileName(source).StartsWith(sourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string dir in Directory.EnumerateDirectories(source).OrderBy(d => d, StringComparer.Ordinal))
            {
                var o = new FileInfo(Path.Combine(dir, "old"));
                var n = new FileInfo(Path.Combine(dir, "new"));
                if (!o.Exists || !n.Exists || Math.Max(o.Length, n.Length) == 0)
                {
                    continue;
                }

                candidates.Add((Math.Abs(Math.Log(Math.Max(o.Length, n.Length) / target)), dir));
            }
        }

        foreach ((double _, string dir) in candidates.OrderBy(c => c.Score))
        {
            string a = File.ReadAllText(Path.Combine(dir, "old"));
            string b = File.ReadAllText(Path.Combine(dir, "new"));
            if (a != b)
            {
                return new Pair(Path.GetRelativePath(CorpusRoot(), dir), a, b);
            }
        }

        throw new InvalidOperationException($"no corpus pair under {text} for source '{sourcePrefix}'");
    }
}
