using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OneDiff.Bench;

/// <summary>
/// Turns BenchmarkDotNet's JSON results into the comparison table: for every size, ours against each comparator (time ratio and
/// allocation ratio), a geometric mean per area and comparator, and the verdict against the bars (geomean at most 1.0, no row
/// worse than 1.25). Rows that run on stand-in algorithms, and rows whose library side does not exist yet, are listed but
/// never count toward a verdict.
/// </summary>
internal static class ResultsTable
{
    private const double GeomeanBar = 1.0;
    private const double WorstBar = 1.25;

    private sealed record Measured(string Area, string Method, string Size, double MeanNs, double Bytes);

    private sealed record Comparison(string Area, string Size, string Ours, string Comparator, double OursNs, double CompNs, double OursBytes, double CompBytes)
    {
        public double Ratio => OursNs / CompNs;
    }

    public static int Write(string resultsDir, string? peakFile, string? infoFile, string? envFile, string outFile)
    {
        var measured = new List<Measured>();
        var host = new List<string>();
        foreach (string file in Directory.GetFiles(resultsDir, "*-report-full.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(file));
            JsonElement root = doc.RootElement;
            if (host.Count == 0 && root.TryGetProperty("HostEnvironmentInfo", out JsonElement env))
            {
                foreach (string key in new[] { "BenchmarkDotNetVersion", "OsVersion", "ProcessorName", "PhysicalCoreCount", "LogicalCoreCount", "RuntimeVersion", "Architecture", "HasAttachedDebugger", "Configuration" })
                {
                    if (env.TryGetProperty(key, out JsonElement v))
                    {
                        host.Add($"{key}: {v}");
                    }
                }
            }

            string area = AreaOf(root.GetProperty("Title").GetString()!);
            foreach (JsonElement b in root.GetProperty("Benchmarks").EnumerateArray())
            {
                string parameters = b.GetProperty("Parameters").GetString() ?? "";
                string size = parameters.Replace("Size=", "", StringComparison.Ordinal).Trim();
                double mean = b.GetProperty("Statistics").GetProperty("Mean").GetDouble();
                double bytes = b.TryGetProperty("Memory", out JsonElement mem) && mem.TryGetProperty("BytesAllocatedPerOperation", out JsonElement ba) ? ba.GetDouble() : 0;
                measured.Add(new Measured(area, b.GetProperty("Method").GetString()!, size, mean, bytes));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("# OneDiff benchmark results");
        sb.AppendLine();
        sb.AppendLine("Ratios are ours / comparator, so lower is better. Each bar: geomean per area and comparator at most 1.0, no row above 1.25.");
        sb.AppendLine();
        if (host.Count > 0)
        {
            sb.AppendLine("```");
            host.ForEach(h => sb.AppendLine(h));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (envFile is not null && File.Exists(envFile))
        {
            sb.AppendLine("Run environment (CPU, cores, load before, during and after):");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.Append(File.ReadAllText(envFile));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (!Availability.AlgorithmsAreReal)
        {
            sb.AppendLine("> **The line, word and character rows ran on the library's stand-in algorithms. They exercise the harness and are not results; no verdict is drawn from them.**");
            sb.AppendLine();
        }

        foreach (IGrouping<string, Measured> area in measured.GroupBy(m => m.Area).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"## {area.Key}");
            sb.AppendLine();
            List<Comparison> rows = Pair(area.Key, area.ToList());
            bool placeholder = area.Key is "lines" or "words" or "chars" && !Availability.AlgorithmsAreReal;
            sb.AppendLine("| Size | Ours | Comparator | Ours (µs) | Comparator (µs) | Ratio | Ours alloc (B) | Comparator alloc (B) | Alloc ratio | Row |");
            sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---|");
            foreach (Comparison c in rows)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {c.Size} | {c.Ours} | {c.Comparator} | {c.OursNs / 1000:F1} | {c.CompNs / 1000:F1} | {c.Ratio:F2} | {c.OursBytes:F0} | {c.CompBytes:F0} | {(c.CompBytes > 0 ? (c.OursBytes / c.CompBytes).ToString("F2", CultureInfo.InvariantCulture) : "n/a")} | {(placeholder ? "placeholder" : "measured")} |"));
            }

            foreach (Measured waiting in PendingComparators(area.Key, area.ToList()))
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {waiting.Size} | Ours{Operation(waiting.Method)} | {waiting.Method} | | {waiting.MeanNs / 1000:F1} | | | {waiting.Bytes:F0} | | pending: library side not available |"));
            }

            sb.AppendLine();
            foreach (IGrouping<string, Comparison> comp in rows.GroupBy(r => r.Comparator).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                double geo = Math.Exp(comp.Average(r => Math.Log(r.Ratio)));
                double worst = comp.Max(r => r.Ratio);
                string verdict = placeholder ? "n/a (placeholder rows)" : geo <= GeomeanBar && worst <= WorstBar ? "PASS" : "FAIL";
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- vs {comp.Key}: geomean {geo:F2}, worst row {worst:F2} ({comp.Count()} rows): **{verdict}**"));
            }

            sb.AppendLine();
        }

        AppendJsonTable(sb, "Peak memory (one cold run per child process)", peakFile, ["area", "impl", "size", "peakAboveInputsBytes", "allocatedBytes"]);
        AppendJsonTable(sb, "Inputs and edit distances (like-for-like check)", infoFile, ["area", "size", "pair", "oldChars", "newChars", "editDistance"]);
        File.WriteAllText(outFile, sb.ToString());
        Console.Write(sb.ToString());
        return 0;
    }

    private static string AreaOf(string title)
    {
        // "OneDiff.Bench.LineDiffBench-20260101-000000" -> lines
        string name = title.Split('.').Last().Split('-')[0];
        return name switch
        {
            "LineDiffBench" => "lines",
            "WordDiffBench" => "words",
            "CharDiffBench" => "chars",
            "CleanupBench" => "cleanup",
            "PatchBench" => "patch",
            _ => name,
        };
    }

    private static List<Comparison> Pair(string area, List<Measured> all)
    {
        var rows = new List<Comparison>();
        foreach (IGrouping<string, Measured> size in all.GroupBy(m => m.Size).OrderBy(g => SizeOrder(g.Key)))
        {
            var ours = size.Where(m => m.Method.StartsWith("Ours", StringComparison.Ordinal)).OrderBy(m => m.Method, StringComparer.Ordinal).ToList();
            var theirs = size.Where(m => !m.Method.StartsWith("Ours", StringComparison.Ordinal)).OrderBy(m => m.Method, StringComparer.Ordinal).ToList();
            foreach (Measured o in ours)
            {
                foreach (Measured t in theirs)
                {
                    // Cleanup and patch rows compare the same operation; the diff rows compare every algorithm with every comparator.
                    bool sameOperation = area is "cleanup" or "patch";
                    if (sameOperation && o.Method["Ours".Length..] != Operation(t.Method))
                    {
                        continue;
                    }

                    rows.Add(new Comparison(area, size.Key, o.Method, t.Method, o.MeanNs, t.MeanNs, o.Bytes, t.Bytes));
                }
            }
        }

        return rows;
    }

    /// <summary>The comparator rows that have no library counterpart yet, because the library side of the area is not available.</summary>
    private static IEnumerable<Measured> PendingComparators(string area, List<Measured> all)
    {
        Feature? needs = area switch { "cleanup" => Feature.Cleanup, "patch" => Feature.Patches, _ => null };
        if (needs is not { } f || Availability.Has(f) || all.Any(m => m.Method.StartsWith("Ours", StringComparison.Ordinal)))
        {
            return [];
        }

        return all.Where(m => !m.Method.StartsWith("Ours", StringComparison.Ordinal))
            .OrderBy(m => SizeOrder(m.Size)).ThenBy(m => m.Method, StringComparer.Ordinal);
    }

    /// <summary>The operation a comparator method does: its name without the comparator prefix.</summary>
    private static string Operation(string method) => method.StartsWith("DiffPlex", StringComparison.Ordinal) ? method["DiffPlex".Length..] : method.StartsWith("Dmp", StringComparison.Ordinal) ? method["Dmp".Length..] : method;

    private static int SizeOrder(string size) => size switch { Sizes.Small => 0, Sizes.Medium => 1, Sizes.Large => 2, _ => 3 };

    private static void AppendJsonTable(StringBuilder sb, string title, string? file, string[] columns)
    {
        if (file is null || !File.Exists(file))
        {
            return;
        }

        sb.AppendLine($"## {title}");
        sb.AppendLine();
        sb.AppendLine("| " + string.Join(" | ", columns) + " |");
        sb.AppendLine("|" + string.Concat(columns.Select(_ => "---|")));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            sb.AppendLine("| " + string.Join(" | ", columns.Select(c => row.TryGetProperty(c, out JsonElement v) ? v.ToString() : "")) + " |");
        }

        sb.AppendLine();
    }
}
