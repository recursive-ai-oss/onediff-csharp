using OneDiff.Merge;

namespace OneDiff.Cli;

/// <summary>
/// <c>onediff merge [options] OURS BASE THEIRS</c>, in the argument order of <c>git merge-file</c>. The merged text goes to standard
/// output (or <c>-o FILE</c>). The exit status follows the git merge-file documentation: the number of conflicts, at most 127, and a
/// negative value (255 as a process status) on error. A merge settled with --ours, --theirs or --union has no conflicts left.
/// </summary>
internal static class MergeCommand
{
    public const int ErrorStatus = 255;
    public const int MaxConflictStatus = 127;

    public const string Usage = """
        usage: onediff merge [options] OURS BASE THEIRS   ("-" reads standard input)
              --style NAME         merge | diff3 | zdiff3 (merge); --diff3 and --zdiff3 are short for --style
              --ours, --theirs     settle every conflict with that side
              --union              settle every conflict with both sides, ours first
          -L, --label NAME         marker label; give it up to three times for OURS, BASE, THEIRS (the file names by default)
              --marker-size N      length of the conflict markers (7)
          -A, --algorithm NAME     myers | patience | histogram (myers)
          -o, --output FILE        write the result to FILE (standard output by default)
        exit status: the number of conflicts (at most 127); 255 on error
        """;

    private static readonly OptionSet Options = new OptionSet()
        .Value("style", "--style")
        .Flag("diff3", "--diff3")
        .Flag("zdiff3", "--zdiff3")
        .Flag("ours", "--ours")
        .Flag("theirs", "--theirs")
        .Flag("union", "--union")
        .Value("label", "-L", "--label")
        .Value("marker-size", "--marker-size")
        .Value("algorithm", "-A", "--algorithm")
        .Value("output", "-o", "--output")
        .Flag("help", "-h", "--help");

    public static int Run(string[] args, CliIo io)
    {
        ParsedArgs p = Options.Parse(args);
        if (p.Has("help"))
        {
            io.Write(null, TextEncoding.Utf8Bytes(Usage + "\n"));
            return 0;
        }

        if (p.Operands.Count != 3)
        {
            throw new CliException("expected three inputs: OURS BASE THEIRS");
        }

        ConflictStyle style = p.GetChoice("style", "--style", ConflictStyle.Merge,
            ("merge", ConflictStyle.Merge), ("diff3", ConflictStyle.Diff3), ("zdiff3", ConflictStyle.ZDiff3));
        if (p.Has("diff3"))
        {
            style = ConflictStyle.Diff3;
        }

        if (p.Has("zdiff3"))
        {
            style = ConflictStyle.ZDiff3;
        }

        int chosen = (p.Has("ours") ? 1 : 0) + (p.Has("theirs") ? 1 : 0) + (p.Has("union") ? 1 : 0);
        if (chosen > 1)
        {
            throw new CliException("--ours, --theirs and --union exclude each other");
        }

        ConflictResolution resolution = p.Has("ours") ? ConflictResolution.Ours
            : p.Has("theirs") ? ConflictResolution.Theirs
            : p.Has("union") ? ConflictResolution.Union
            : ConflictResolution.None;
        IReadOnlyList<string> labels = p.All("label");
        if (labels.Count > 3)
        {
            throw new CliException("--label may be given at most three times");
        }

        int markerSize = p.GetInt("marker-size", "--marker-size", 7);
        if (markerSize < 1)
        {
            throw new CliException("--marker-size must be at least 1");
        }

        var settings = new MergeSettings
        {
            Style = style,
            Resolution = resolution,
            Algorithm = p.GetChoice("algorithm", "--algorithm", DiffAlgorithm.Myers,
                ("myers", DiffAlgorithm.Myers), ("patience", DiffAlgorithm.Patience), ("histogram", DiffAlgorithm.Histogram)),
            OursLabel = labels.Count > 0 ? labels[0] : p.Operands[0],
            BaseLabel = labels.Count > 1 ? labels[1] : p.Operands[1],
            TheirsLabel = labels.Count > 2 ? labels[2] : p.Operands[2],
            MarkerSize = markerSize,
        };

        int stdinCount = p.Operands.Count(o => o == "-");
        if (stdinCount > 1)
        {
            throw new CliException("only one input can be standard input");
        }

        byte[][] inputs = p.Operands.Select(io.Read).ToArray();
        if (inputs.Any(TextEncoding.IsBinary))
        {
            throw new CliException("cannot merge binary files");
        }

        (TextEncoding encoding, string[] texts) = TextEncoding.Decode(inputs);
        TextMerge merge = Diff.Merge(texts[1], texts[0], texts[2], settings);
        io.Write(p.Get("output"), encoding.Bytes(merge.Text));
        return Math.Min(merge.ConflictCount, MaxConflictStatus);
    }
}
