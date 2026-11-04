using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DiffMatchPatch;
using OneDiff.Tokenization;

namespace OneDiff.Bench;

/// <summary>One implementation of one row that the info and peak-memory commands can run directly.</summary>
internal sealed record Row(string Area, string Name, string[] SizeSet, Func<string, string, int> Run)
{
    public bool IsOurs => Name.StartsWith("Ours", StringComparison.Ordinal);

    private static readonly string[] All = [Sizes.Small, Sizes.Medium, Sizes.Large];
    private static readonly string[] NoLarge = [Sizes.Small, Sizes.Medium];

    public static IReadOnlyList<Row> Rows()
    {
        diff_match_patch dmp = Impl.NewDmp();
        var rows = new List<Row>();
        foreach ((string area, TokenKind kind, string[] sizes) in new[] { ("lines", TokenKind.Line, All), ("words", TokenKind.Word, All), ("chars", TokenKind.Character, NoLarge) })
        {
            foreach (DiffAlgorithm alg in new[] { DiffAlgorithm.Myers, DiffAlgorithm.Patience, DiffAlgorithm.Histogram })
            {
                rows.Add(new Row(area, "Ours" + alg, sizes, (a, b) => Impl.OursText(a, b, kind, alg)));
            }
        }

        rows.Add(new Row("lines", "DiffPlexLines", All, Impl.DiffPlexLines));
        rows.Add(new Row("lines", "DmpLines", All, (a, b) => Impl.DmpLines(dmp, a, b)));
        rows.Add(new Row("words", "DiffPlexWords", All, Impl.DiffPlexWords));
        rows.Add(new Row("chars", "DiffPlexChars", NoLarge, Impl.DiffPlexChars));
        rows.Add(new Row("chars", "DmpChars", NoLarge, (a, b) => Impl.DmpChars(dmp, a, b)));
        rows.Add(new Row("patch", "DmpMake", NoLarge, (a, b) => dmp.patch_make(a, b).Count));
        return rows;
    }
}

/// <summary>
/// The workload commands that are not BenchmarkDotNet runs: <c>info</c> lists the corpus pair behind each row with the edit distance
/// every implementation found (a like-for-like check), and <c>peak</c> measures peak working set one cold run per child process.
/// </summary>
internal static class Workloads
{
    private static readonly JsonWriterOptions Indented = new() { Indented = true };

    public static int Info(string outFile)
    {
        using var stream = File.Create(outFile);
        using var json = new Utf8JsonWriter(stream, Indented);
        json.WriteStartArray();
        foreach (IGrouping<(string Area, string Size), Row> group in Row.Rows().SelectMany(r => r.SizeSet.Select(s => (Row: r, Size: s))).GroupBy(x => (x.Row.Area, x.Size), x => x.Row))
        {
            Pair pair = Inputs.Pick(Inputs.SourceFor(group.Key.Area), group.Key.Size);
            json.WriteStartObject();
            json.WriteString("area", group.Key.Area);
            json.WriteString("size", group.Key.Size);
            json.WriteString("pair", pair.Name);
            json.WriteNumber("oldChars", pair.Old.Length);
            json.WriteNumber("newChars", pair.New.Length);
            json.WriteStartObject("editDistance");
            foreach (Row row in group.Where(r => r.Area != "patch"))
            {
                json.WriteNumber(row.Name, row.Run(pair.Old, pair.New));
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        return 0;
    }

    /// <summary>Child mode: run one row once, or only load the inputs when <paramref name="impl"/> is "none", and print peak memory.</summary>
    public static int PeakOne(string area, string impl, string size)
    {
        Pair pair = Inputs.Pick(Inputs.SourceFor(area), size);
        Func<string, string, int>? run = impl == "none" ? null : Row.Rows().First(r => r.Area == area && r.Name == impl).Run;
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(true);
        run?.Invoke(pair.Old, pair.New);
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        using Process self = Process.GetCurrentProcess();
        self.Refresh();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{{\"area\":\"{area}\",\"impl\":\"{impl}\",\"size\":\"{size}\",\"peakWorkingSetBytes\":{self.PeakWorkingSet64},\"allocatedBytes\":{allocated}}}"));
        return 0;
    }

    /// <summary>Spawns one child per row and size, with a load-only baseline per area and size, and writes the deltas.</summary>
    public static int PeakAll(string outFile)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        // Run through the dotnet host, the managed entry assembly is the first argument; an apphost needs none.
        string? entry = Path.GetFileNameWithoutExtension(exe) == "dotnet" ? Environment.GetCommandLineArgs()[0] : null;
        using var stream = File.Create(outFile);
        using var json = new Utf8JsonWriter(stream, Indented);
        json.WriteStartArray();
        foreach (Row row in Row.Rows())
        {
            foreach (string size in row.SizeSet)
            {
                (long peak, long alloc) = Child(exe, entry, row.Area, row.Name, size);
                (long basePeak, _) = Child(exe, entry, row.Area, "none", size);
                json.WriteStartObject();
                json.WriteString("area", row.Area);
                json.WriteString("impl", row.Name);
                json.WriteString("size", size);
                json.WriteNumber("peakWorkingSetBytes", peak);
                json.WriteNumber("peakAboveInputsBytes", Math.Max(0, peak - basePeak));
                json.WriteNumber("allocatedBytes", alloc);
                json.WriteEndObject();
            }
        }

        json.WriteEndArray();
        return 0;
    }

    private static (long Peak, long Alloc) Child(string exe, string? entry, string area, string impl, string size)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        if (entry is not null)
        {
            info.ArgumentList.Add(entry);
        }

        foreach (string a in new[] { "peak-one", area, impl, size })
        {
            info.ArgumentList.Add(a);
        }

        using Process p = Process.Start(info)!;
        string line = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        using JsonDocument doc = JsonDocument.Parse(line);
        return (doc.RootElement.GetProperty("peakWorkingSetBytes").GetInt64(), doc.RootElement.GetProperty("allocatedBytes").GetInt64());
    }
}
