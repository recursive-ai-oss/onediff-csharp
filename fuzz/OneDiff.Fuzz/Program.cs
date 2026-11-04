using System.Diagnostics;
using System.Text;
using OneDiff.Patching;

namespace OneDiff.Fuzz;

/// <summary>
/// Mutation fuzzer for the patch parsers and appliers. Usage: <c>OneDiff.Fuzz &lt;target&gt; &lt;seconds&gt; [seed] [outDir]</c>.
/// Targets: unified-parse, unified-apply, dmp-parse, dmp-apply, unified-roundtrip, dmp-roundtrip, and comparison-json (the change-list reader).
/// It is a plain seeded mutation runner (no coverage feedback): seeds come from patches the library writes plus hand-made hostile
/// cases, and each input is a few random mutations of one seed. A crash is an exception, a broken invariant, or an input that takes
/// longer than the time limit; each is written to the output directory as UTF-16 bytes and counted.
/// </summary>
internal static class Program
{
    private const int MaxInputChars = 1 << 16;
    private static readonly double SlowSeconds = 3.0;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "comparison-json")
        {
            return ComparisonJsonFuzz.Campaign(args[1..]);
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: OneDiff.Fuzz <unified-parse|unified-apply|dmp-parse|dmp-apply|unified-roundtrip|dmp-roundtrip> <seconds> [seed] [outDir]");
            Console.Error.WriteLine("       OneDiff.Fuzz comparison-json [--seconds N] [--seed N] [--crashes DIR]");
            return 2;
        }

        string target = args[0];
        double seconds = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        int seed = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1;
        string outDir = args.Length > 3 ? args[3] : Path.Combine(Path.GetTempPath(), "onediff-fuzz");
        Directory.CreateDirectory(outDir);

        var random = new Random(seed);
        Func<string, string?> body = target switch
        {
            "unified-parse" => UnifiedParse,
            "unified-apply" => input => UnifiedApply(input, random),
            "dmp-parse" => DmpParse,
            "dmp-apply" => input => DmpApply(input, random),
            "unified-roundtrip" => input => UnifiedRoundTrip(input, random),
            "dmp-roundtrip" => input => DmpRoundTrip(input, random),
            _ => throw new ArgumentException("unknown target " + target),
        };

        List<string> seeds = target.StartsWith("unified", StringComparison.Ordinal) ? UnifiedSeeds(random) : DmpSeeds(random);
        long executions = 0;
        int crashes = 0;
        int slow = 0;
        long start = Stopwatch.GetTimestamp();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds)
        {
            string input = target.EndsWith("roundtrip", StringComparison.Ordinal) ? string.Empty : Mutate(random, seeds);
            long t0 = Stopwatch.GetTimestamp();
            string? failure;
            try
            {
                failure = body(input);
            }
            catch (Exception e)
            {
                failure = e.GetType().Name + ": " + e.Message;
            }

            double took = Stopwatch.GetElapsedTime(t0).TotalSeconds;
            executions++;
            if (failure is null && took > SlowSeconds)
            {
                failure = $"slow: {took:F1}s";
                slow++;
            }

            if (failure is not null)
            {
                crashes++;
                string name = $"{target}-{seed}-{executions}.u16";
                File.WriteAllBytes(Path.Combine(outDir, name), Encoding.Unicode.GetBytes(input));
                Console.WriteLine($"CRASH {name}: {failure}");
                if (crashes >= 20)
                {
                    break;
                }
            }
            else if (executions % 7 == 0 && distinct.Count < 100_000)
            {
                distinct.Add(input.Length > 64 ? input[..64] : input);
            }

            // Grow the seed pool with inputs that parsed, so later mutations reach deeper states.
            if (failure is null && seeds.Count < 2000 && executions % 50 == 0 && input.Length > 0 && input.Length < 4000)
            {
                seeds.Add(input);
            }
        }

        Console.WriteLine($"target={target} seed={seed} seconds={Stopwatch.GetElapsedTime(start).TotalSeconds:F0} executions={executions} crashes={crashes} slow={slow} seedPool={seeds.Count} accepted={Accepted} rejected={Rejected}");
        return crashes == 0 ? 0 : 1;
    }

    private static long Accepted;
    private static long Rejected;

    // ------------------------------------------------------------------ targets

    private static string? UnifiedParse(string input)
    {
        bool ok = UnifiedPatch.TryParse(input, out UnifiedPatch? p, out PatchParseError err);
        if (!ok)
        {
            Rejected++;
            return p is null && err.Line >= 1 && err.Offset >= 0 && err.Offset <= input.Length ? null : "bad error position";
        }

        Accepted++;
        string text = p!.ToString();
        if (!UnifiedPatch.TryParse(text, out UnifiedPatch? again, out _))
        {
            return "formatted patch does not parse";
        }

        return again!.ToString() == text ? null : "format is not stable";
    }

    private static string? UnifiedApply(string input, Random random)
    {
        if (!UnifiedPatch.TryParse(input, out UnifiedPatch? p, out _))
        {
            Rejected++;
            return null;
        }

        Accepted++;
        foreach (UnifiedFilePatch f in p.Files)
        {
            // A text built from the patch's own old lines, shuffled with noise, so hunks sometimes fit, sometimes drift.
            var sb = new StringBuilder();
            foreach (UnifiedHunk h in f.Hunks)
            {
                foreach (UnifiedLine l in h.Lines.ToArray().Where(l => l.Kind != UnifiedLineKind.Insert))
                {
                    if (random.Next(5) != 0)
                    {
                        sb.Append(l.Text);
                    }
                }

                sb.Append("noise ").Append(random.Next(10)).Append('\n');
            }

            string text = sb.ToString();
            foreach (var options in new[] { new UnifiedApplyOptions(), new UnifiedApplyOptions { Reverse = true, MaxFuzz = 0 }, new UnifiedApplyOptions { MaxOffset = 5 } })
            {
                UnifiedApplyResult r = f.Apply(text, options);
                if (r.Hunks.Count != f.Hunks.Count)
                {
                    return "result has the wrong number of hunks";
                }

                int rejected = r.Hunks.Count(h => h.Outcome == HunkOutcome.Rejected);
                if ((r.Rejected?.Hunks.Count ?? 0) != rejected)
                {
                    return "rejected patch disagrees with hunk results";
                }

                if (rejected == 0 && !r.Success)
                {
                    return "success flag disagrees";
                }
            }
        }

        return null;
    }

    private static string? DmpParse(string input)
    {
        bool ok = DmpPatcher.TryFromText(input, out IReadOnlyList<DmpPatch>? patches, out PatchParseError err);
        if (!ok)
        {
            Rejected++;
            return patches is null && err.Line >= 1 && err.Offset >= 0 && err.Offset <= input.Length ? null : "bad error position";
        }

        Accepted++;
        string text = DmpPatcher.ToText(patches!);
        if (!DmpPatcher.TryFromText(text, out IReadOnlyList<DmpPatch>? again, out _))
        {
            return "written patch text does not parse";
        }

        return DmpPatcher.ToText(again!) == text ? null : "text is not stable";
    }

    private static string? DmpApply(string input, Random random)
    {
        if (!DmpPatcher.TryFromText(input, out IReadOnlyList<DmpPatch>? patches, out _))
        {
            Rejected++;
            return null;
        }

        Accepted++;
        var sb = new StringBuilder();
        foreach (DmpPatch p in patches.Take(50))
        {
            if (random.Next(4) != 0)
            {
                sb.Append(p.OldText());
            }

            sb.Append(random.Next(3) == 0 ? "xyz" : string.Empty);
        }

        string text = sb.ToString();
        if (text.Length > 20_000)
        {
            text = text[..20_000];
        }

        var patcher = new DmpPatcher { MatchDistance = random.Next(0, 3) == 0 ? 0 : 1000, MatchThreshold = random.Next(0, 3) == 0 ? 0.0 : 0.5 };
        DmpApplyResult r = patcher.Apply(patches, text);
        if (r.Patches.Count != patches.Count)
        {
            return "result has the wrong number of patches";
        }

        // Patches that all applied cleanly must change the text only by what the patches say; at least keep the two bookkeeping views equal.
        return r.Success == r.Patches.All(x => x.Applied) ? null : "success flag disagrees";
    }

    private static string? UnifiedRoundTrip(string unused, Random random)
    {
        _ = unused;
        string o = RandomLines(random);
        string n = random.Next(3) == 0 ? RandomLines(random) : MutateLines(random, o);
        List<string> ol = PatchLines.Split(o);
        List<string> nl = PatchLines.Split(n);
        EditScript script = Diff.Sequences<string>(ol.ToArray(), nl.ToArray(), (DiffAlgorithm)random.Next(3), StringComparer.Ordinal);
        string patch = UnifiedFilePatch.Create(ol, nl, script, "a", "b", random.Next(0, 5)).ToString();
        UnifiedApplyResult r = Diff.ApplyUnified(o, patch);
        if (!r.Success || r.Text != n)
        {
            return "forward apply does not give the new text";
        }

        if (patch.Length > 0)
        {
            UnifiedApplyResult back = UnifiedPatch.Parse(patch).Files[0].Apply(n, new UnifiedApplyOptions { Reverse = true });
            if (!back.Success || back.Text != o)
            {
                return "reverse apply does not give the old text";
            }
        }

        return null;
    }

    private static string? DmpRoundTrip(string unused, Random random)
    {
        _ = unused;
        string a = RandomChars(random);
        string b = random.Next(4) == 0 ? RandomChars(random) : MutateChars(random, a);
        var patcher = new DmpPatcher { PatchMargin = random.Next(0, 3) == 0 ? random.Next(0, 9) : 4 };
        string text = DmpPatcher.ToText(patcher.Make(a, b));
        DmpApplyResult r = patcher.Apply(DmpPatcher.FromText(text), a);
        return r.Success && r.Text == b ? null : "patch text does not take the old text to the new";
    }

    // ------------------------------------------------------------------ seeds and mutation

    private static List<string> UnifiedSeeds(Random random)
    {
        var seeds = new List<string>
        {
            string.Empty,
            "--- a\n+++ b\n@@ -1 +1 @@\n-x\n+y\n",
            "--- a\n+++ b\n@@ -0,0 +1,2 @@\n+x\n+y\n",
            "--- a\n+++ b\n@@ -1,2 +0,0 @@\n-x\n-y\n\\ No newline at end of file\n",
            "--- a\t2020-01-01\n+++ b\t2020-01-02\n@@ -1,3 +1,3 @@ section\n a\n-b\n+c\n d\n",
            "diff --git a/x b/x\nindex 1..2 100644\n--- a/x\n+++ b/x\n@@ -1 +1 @@\n-1\n+2\ndiff --git a/y b/y\n--- \"a/y\\303\\251\"\n+++ b/y\n@@ -1 +1 @@\n-1\n+2\n",
            "--- a\n+++ b\n@@ -2147483647,2147483647 +1,1 @@\n x\n",
            "--- a\n+++ b\n@@ -1,99999999999 +1 @@\n x\n",
            "--- a\n+++ b\n@@ -1,3 +1,3 @@\n a\n\n-c\n+d\n",
            "--- a\r\n+++ b\r\n@@ -1 +1 @@\r\n-x\r\n+y\r\n",
        };
        for (int i = 0; i < 40; i++)
        {
            string o = RandomLines(random);
            string n = MutateLines(random, o);
            List<string> ol = PatchLines.Split(o);
            List<string> nl = PatchLines.Split(n);
            EditScript s = Diff.Sequences<string>(ol.ToArray(), nl.ToArray(), DiffAlgorithm.Myers, StringComparer.Ordinal);
            seeds.Add(UnifiedFilePatch.Create(ol, nl, s, "a/f", "b/f", random.Next(0, 4)).ToString());
        }

        return seeds;
    }

    private static List<string> DmpSeeds(Random random)
    {
        var seeds = new List<string>
        {
            string.Empty,
            "@@ -1 +1 @@\n-a\n+b\n",
            "@@ -0,0 +1,3 @@\n+abc\n",
            "@@ -1,8 +1,7 @@\n Th\n-e\n+at\n  quick b\n@@ -22,18 +22,17 @@\n jump\n-s\n+ed\n  over \n-the\n+a\n %0Alaz\n",
            "@@ -1,4 +1,4 @@\n %E2%9D%A4%EF%B8%8F\n-%F0%9F%98%80\n+%ED%A0%80\n",
            "@@ -99999999999,4 +1,4 @@\n a\n",
            "@@ -1,4 +1,4 @@\n+%\n",
            "@@ -1,4 +1,4 @@\n+%F\n",
            "@@ -1,4 +1,4 @@\n+%FF%FF\n",
        };
        for (int i = 0; i < 40; i++)
        {
            string a = RandomChars(random);
            string b = MutateChars(random, a);
            seeds.Add(DmpPatcher.ToText(new DmpPatcher().Make(a, b)));
        }

        return seeds;
    }

    private static readonly string[] Snippets =
    [
        "\n", "\r\n", "@@ -1,1 +1,1 @@\n", "@@ -2147483647,2147483647 +2147483647,2147483647 @@\n", "--- a\n+++ b\n", "\\ No newline at end of file\n",
        " ", "+", "-", "@@", ",", "0", "-99999999999999999999", "%", "%0A", "%FF", "%C3", "%ED%A0%80", "\uFFFD", "\uD800", "\uDC00", "\U0001F600",
        "\0", "\t", "diff --git a/x b/x\n", "\"", "\\303",
    ];

    private static string Mutate(Random random, List<string> seeds)
    {
        string s = seeds[random.Next(seeds.Count)];
        var sb = new StringBuilder(s);
        int rounds = random.Next(1, 6);
        for (int i = 0; i < rounds; i++)
        {
            int at = sb.Length == 0 ? 0 : random.Next(sb.Length + 1);
            switch (random.Next(8))
            {
                case 0 when at < sb.Length:
                    sb.Remove(at, Math.Min(sb.Length - at, random.Next(1, 20)));
                    break;
                case 1:
                    sb.Insert(at, Snippets[random.Next(Snippets.Length)]);
                    break;
                case 2 when at < sb.Length:
                    sb[at] = (char)random.Next(0x80);
                    break;
                case 3:
                    sb.Length = at; // truncate
                    break;
                case 4:
                    // splice from another seed
                    string other = seeds[random.Next(seeds.Count)];
                    sb.Insert(at, other.AsSpan(random.Next(other.Length + 1)));
                    break;
                case 5 when at < sb.Length:
                    // duplicate a chunk
                    int len = Math.Min(sb.Length - at, random.Next(1, 200));
                    string chunk = sb.ToString(at, len);
                    for (int k = random.Next(1, 20); k > 0 && sb.Length < MaxInputChars; k--)
                    {
                        sb.Insert(at, chunk);
                    }

                    break;
                case 6:
                    sb.Insert(at, random.Next(2) == 0 ? random.Next(0, 300).ToString(System.Globalization.CultureInfo.InvariantCulture) : "9" + new string('9', random.Next(1, 12)));
                    break;
                default:
                    if (at < sb.Length)
                    {
                        sb[at] = (char)random.Next(0x20, 0x250);
                    }

                    break;
            }

            if (sb.Length > MaxInputChars)
            {
                sb.Length = MaxInputChars;
            }
        }

        return sb.ToString();
    }

    private static string RandomLines(Random random)
    {
        int count = random.Next(0, 30);
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.Append((char)('a' + random.Next(5)));
            if (random.Next(12) == 0)
            {
                sb.Append('\r');
            }

            if (i < count - 1 || random.Next(3) != 0)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string MutateLines(Random random, string text)
    {
        List<string> lines = PatchLines.Split(text);
        for (int e = random.Next(0, 5); e > 0; e--)
        {
            int at = lines.Count == 0 ? 0 : random.Next(lines.Count + 1);
            if (random.Next(2) == 0 && at < lines.Count)
            {
                lines.RemoveAt(at);
            }
            else
            {
                lines.Insert(at, (char)('a' + random.Next(5)) + "\n");
            }
        }

        return string.Concat(lines);
    }

    private static string RandomChars(Random random)
    {
        int n = random.Next(0, 400);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            int r = random.Next(30);
            sb.Append(r switch
            {
                < 20 => (char)('a' + (r % 6)),
                < 22 => ' ',
                < 24 => '\n',
                24 => '%',
                25 => '\uFFFD',
                26 => '\u00e9',
                27 => '\u2764',
                28 => '\uD83D', // start of a pair, completed below when the next char is drawn
                _ => '\uDE00',
            });
        }

        return sb.ToString();
    }

    private static string MutateChars(Random random, string text)
    {
        var sb = new StringBuilder(text);
        for (int e = random.Next(0, 6); e > 0; e--)
        {
            int at = random.Next(sb.Length + 1);
            switch (random.Next(3))
            {
                case 0 when at < sb.Length:
                    sb.Remove(at, Math.Min(sb.Length - at, random.Next(1, 60)));
                    break;
                case 1:
                    string extra = RandomChars(random);
                    sb.Insert(at, extra.AsSpan(0, Math.Min(extra.Length, random.Next(0, 40))));
                    break;
                default:
                    if (at < sb.Length)
                    {
                        sb[at] = 'Z';
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
