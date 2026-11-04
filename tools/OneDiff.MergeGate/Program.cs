using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using OneDiff.Merge;

namespace OneDiff.MergeGate;

/// <summary>
/// Compares OneDiff's three-way merge with the released git binary's <c>git merge-file</c> on a corpus of base/ours/theirs
/// triples. git runs only as a separate process. Files are read as Latin-1 so every byte survives the round trip.
/// </summary>
/// <remarks>
/// Usage: MergeGate &lt;git&gt; &lt;corpus-root&gt; &lt;out-dir&gt; [--align=builtin|git] [--algo=myers|patience|histogram] [--limit=N] [--jobs=N] [--time]
/// <para>
/// <c>--align=git</c> takes the base-to-side alignments from <c>git diff --no-index</c> instead of OneDiff's diff algorithm, which
/// separates differences caused by a different alignment from differences in the merge rules themselves.
/// </para>
/// <para><c>--time</c> skips git and times OneDiff alone over the corpus, one thread.</para>
/// </remarks>
internal static class Program
{
    private static readonly (string Name, ConflictStyle Style, string Flag)[] Styles =
    [
        ("merge", ConflictStyle.Merge, ""),
        ("diff3", ConflictStyle.Diff3, "--diff3"),
        ("zdiff3", ConflictStyle.ZDiff3, "--zdiff3"),
    ];

    private static readonly string[] Statuses = ["identical", "differs-markers-only", "differs-content", "error"];

    private static DiffAlgorithm algorithm = DiffAlgorithm.Myers;

    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: MergeGate <git> <corpus-root> <out-dir> [--align=builtin|git] [--algo=myers|patience|histogram] [--limit=N] [--jobs=N] [--time]");
            return 2;
        }

        string git = args[0];
        string root = args[1];
        string outDir = args[2];
        bool alignWithGit = args.Contains("--align=git");
        bool timeOnly = args.Contains("--time");
        int limit = int.MaxValue;
        int jobs = 4;
        foreach (string a in args.Skip(3))
        {
            if (a.StartsWith("--limit=", StringComparison.Ordinal))
            {
                limit = int.Parse(a["--limit=".Length..]);
            }
            else if (a.StartsWith("--algo=", StringComparison.Ordinal))
            {
                algorithm = Enum.Parse<DiffAlgorithm>(a["--algo=".Length..], true);
            }
            else if (a.StartsWith("--jobs=", StringComparison.Ordinal))
            {
                jobs = int.Parse(a["--jobs=".Length..]);
            }
        }

        Directory.CreateDirectory(outDir);
        string[] triples = Directory.EnumerateFiles(root, "base", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(d => d != null && File.Exists(Path.Combine(d, "ours")) && File.Exists(Path.Combine(d, "theirs")))
            .Select(d => d!)
            .Order(StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

        return timeOnly ? Time(triples) : Compare(git, root, outDir, triples, alignWithGit, jobs);
    }

    private static int Time(string[] triples)
    {
        var inputs = triples.Select(d => (Read(Path.Combine(d, "base")), Read(Path.Combine(d, "ours")), Read(Path.Combine(d, "theirs")))).ToArray();
        foreach ((string name, ConflictStyle style, _) in Styles)
        {
            var settings = new MergeSettings { Style = style, Algorithm = algorithm };
            long conflicts = 0;
            var sw = Stopwatch.StartNew();
            foreach ((string b, string o, string t) in inputs)
            {
                conflicts += ThreeWayMerge.Lines(b, o, t, settings).ConflictCount;
            }

            sw.Stop();
            Console.WriteLine($"time style={name} triples={inputs.Length} conflicts={conflicts} ms={sw.Elapsed.TotalMilliseconds:F0}");
        }

        return 0;
    }

    private static int Compare(string git, string root, string outDir, string[] triples, bool alignWithGit, int jobs)
    {
        var results = new ConcurrentBag<string>();
        var counts = new ConcurrentDictionary<string, int>();
        string work = Path.Combine(outDir, "work");
        Directory.CreateDirectory(work);

        Parallel.ForEach(
            triples,
            new ParallelOptions { MaxDegreeOfParallelism = jobs },
            dir =>
            {
                string rel = Path.GetRelativePath(root, dir);
                string b = Read(Path.Combine(dir, "base"));
                string o = Read(Path.Combine(dir, "ours"));
                string t = Read(Path.Combine(dir, "theirs"));
                string scratch = Path.Combine(work, $"w{Environment.CurrentManagedThreadId}");
                Directory.CreateDirectory(scratch);

                foreach ((string name, ConflictStyle style, string flag) in Styles)
                {
                    var settings = new MergeSettings { Style = style, Algorithm = algorithm };
                    (string gitText, int gitRc) = RunGit(git, dir, flag);
                    TextMerge? ours = null;
                    string status;
                    string detail = string.Empty;
                    try
                    {
                        ours = alignWithGit
                            ? ThreeWayMerge.LinesAligned(b, o, t, settings, lineText => GitAligner(git, lineText, scratch), default)
                            : ThreeWayMerge.Lines(b, o, t, settings);
                    }
                    catch (Exception e) when (e is InvalidOperationException or ArgumentException)
                    {
                        status = "error";
                        detail = e.Message;
                    }

                    if (ours == null)
                    {
                        status = "error";
                    }
                    else
                    {
                        int ourRc = Math.Min(ours.ConflictCount, 127);
                        if (ours.Text == gitText && ourRc == gitRc)
                        {
                            status = "identical";
                        }
                        else
                        {
                            // Same conflicts either way; do the three resolutions still agree? Then only the markers' layout differs.
                            bool resolutionsAgree = true;
                            foreach ((string rflag, ConflictResolution r) in new[] { ("--ours", ConflictResolution.Ours), ("--theirs", ConflictResolution.Theirs), ("--union", ConflictResolution.Union) })
                            {
                                (string gt, _) = RunGit(git, dir, flag + " " + rflag);
                                string mine = ThreeWayMerge.LinesAligned(b, o, t, settings with { Resolution = r }, alignWithGit ? (lineText => GitAligner(git, lineText, scratch)) : null, default).Text;
                                resolutionsAgree &= gt == mine;
                            }

                            status = resolutionsAgree ? "differs-markers-only" : "differs-content";
                            detail = $"conflicts ours={ours.ConflictCount} git={gitRc}";
                            string caseDir = Path.Combine(outDir, "diffs", rel.Replace('/', '_'), name);
                            Directory.CreateDirectory(caseDir);
                            File.WriteAllText(Path.Combine(caseDir, "ours.out"), ours.Text, Encoding.Latin1);
                            File.WriteAllText(Path.Combine(caseDir, "git.out"), gitText, Encoding.Latin1);
                        }
                    }

                    counts.AddOrUpdate($"{name} {status}", 1, (_, n) => n + 1);
                    results.Add($"{rel}\t{name}\t{status}\t{detail}");
                }

            });

        File.WriteAllLines(Path.Combine(outDir, "results.tsv"), results.Order(StringComparer.Ordinal));
        foreach ((string name, _, _) in Styles)
        {
            Console.WriteLine($"style={name} cases={triples.Length} " + string.Join(
                " ",
                Statuses.Select(s => $"{s}={counts.GetValueOrDefault($"{name} {s}")}")));
        }

        return 0;
    }

    private static Aligner GitAligner(string git, string[] lineText, string scratch)
    {
        return (first, second) =>
        {
            string a = Path.Combine(scratch, "a.txt");
            string b = Path.Combine(scratch, "b.txt");
            File.WriteAllText(a, string.Concat(first.Select(id => lineText[id])), Encoding.Latin1);
            File.WriteAllText(b, string.Concat(second.Select(id => lineText[id])), Encoding.Latin1);
            (string diff, _) = Run(git, ["diff", "--no-index", "--no-color", "--no-indent-heuristic", "--diff-algorithm=myers", "-U0", a, b], null);
            var hunks = new List<Hunk>();
            foreach (string line in diff.Split('\n'))
            {
                if (!line.StartsWith("@@ -", StringComparison.Ordinal))
                {
                    continue;
                }

                // "@@ -l,s +l,s @@": a length of zero means the lines went in (or out) after line l.
                string[] parts = line.Split(' ');
                (int fs, int fl) = Range(parts[1][1..]);
                (int ss, int sl) = Range(parts[2][1..]);
                hunks.Add(new Hunk(fl == 0 ? fs : fs - 1, (fl == 0 ? fs : fs - 1) + fl, sl == 0 ? ss : ss - 1, (sl == 0 ? ss : ss - 1) + sl));
            }

            return hunks;
        };

        static (int Start, int Length) Range(string text)
        {
            int comma = text.IndexOf(',', StringComparison.Ordinal);
            return comma < 0 ? (int.Parse(text), 1) : (int.Parse(text[..comma]), int.Parse(text[(comma + 1)..]));
        }
    }

    private static (string Text, int Rc) RunGit(string git, string dir, string flag)
    {
        var args = new List<string> { "merge-file", "-p" };
        args.AddRange(flag.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        args.AddRange(["-L", "ours", "-L", "base", "-L", "theirs", Path.Combine(dir, "ours"), Path.Combine(dir, "base"), Path.Combine(dir, "theirs")]);
        return Run(git, args, null);
    }

    private static (string Text, int Rc) Run(string exe, IEnumerable<string> args, string? stdin)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        // Raw bytes: a text reader would swallow a leading byte-order mark.
        using var buffer = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(buffer);
        string text = Encoding.Latin1.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        p.WaitForExit();
        err.Wait();
        return (text, p.ExitCode);
    }

    private static string Read(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));
}
