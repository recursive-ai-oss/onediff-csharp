using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Docxodus;
using OfficeIMO.Word;

namespace OneDiff.Comparators;

/// <summary>The outcome of one comparison.</summary>
internal sealed record RunRecord(string Id, string Tool, bool Ok, double Milliseconds, long OutputBytes, string? Error);

/// <summary>
/// Runs the free comparison tools that are .NET packages on a directory of document pairs
/// (<c>&lt;id&gt;.old.docx</c>, <c>&lt;id&gt;.new.docx</c>) and writes each tool's tracked-changes result as <c>OUT/&lt;tool&gt;/&lt;id&gt;.docx</c>.
/// The tools are only called through their public entry points, once per pair, with their default settings.
/// </summary>
internal static class Program
{
    private const string Usage = """
        OneDiff.Comparators run --tool docxodus|officeimo --docx DIR --out DIR [--jobs N] [--limit N] [--ids FILE]
        OneDiff.Comparators versions

          run       compares every pair; writes OUT/<tool>/<id>.docx and OUT/<tool>/runs.jsonl (time and status per pair).
                    Existing results are kept, so an interrupted run resumes; a failed pair is recorded and the run goes on.
          versions  prints each tool's version as its assembly reports it.
        """;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var opt = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i++)
        {
            opt[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        if (args[0] == "versions")
        {
            Console.WriteLine("docxodus " + VersionOf(typeof(DocxCompare).Assembly));
            Console.WriteLine("officeimo.word " + VersionOf(typeof(WordDocument).Assembly));
            return 0;
        }

        if (args[0] != "run")
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string tool = opt.GetValueOrDefault("tool") ?? throw new ArgumentException("--tool is required");
        string docx = opt.GetValueOrDefault("docx") ?? throw new ArgumentException("--docx is required");
        string outDir = Path.Combine(Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required")), tool);
        int jobs = int.Parse(opt.GetValueOrDefault("jobs") ?? "4", CultureInfo.InvariantCulture);
        int limit = int.Parse(opt.GetValueOrDefault("limit") ?? "0", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(outDir);
        IEnumerable<string> ids = Directory.EnumerateFiles(docx, "*.old.docx").Select(p => Path.GetFileName(p)[..^".old.docx".Length]).OrderBy(x => x, StringComparer.Ordinal);
        if (opt.TryGetValue("ids", out string? idFile))
        {
            var want = new HashSet<string>(File.ReadAllLines(idFile), StringComparer.Ordinal);
            ids = ids.Where(want.Contains);
        }

        string[] list = limit > 0 ? [.. ids.Take(limit)] : [.. ids];
        string log = Path.Combine(outDir, "runs.jsonl");
        var done = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(log))
        {
            foreach (string line in File.ReadLines(log))
            {
                done.Add(JsonSerializer.Deserialize<RunRecord>(line)!.Id);
            }
        }

        var records = new System.Collections.Concurrent.ConcurrentBag<RunRecord>();
        Parallel.ForEach(list.Where(id => !done.Contains(id)), new ParallelOptions { MaxDegreeOfParallelism = jobs }, id =>
        {
            string o = Path.Combine(docx, id + ".old.docx");
            string n = Path.Combine(docx, id + ".new.docx");
            string target = Path.Combine(outDir, id + ".docx");
            var sw = Stopwatch.StartNew();
            try
            {
                switch (tool)
                {
                    case "docxodus":
                        WmlDocument result = DocxCompare.Compare(new WmlDocument(o), new WmlDocument(n));
                        result.SaveAs(target);
                        break;
                    case "officeimo":
                        // The redline path that writes tracked changes into a copy of the target (the default mode writes a report
                        // document instead); the author and date are fixed so a rerun gives the same file.
                        WordDocumentComparer.CreateRedlineDocument(o, n, target, new WordComparisonRedlineOptions
                        {
                            Mode = WordComparisonRedlineMode.InPlaceTarget,
                            Author = "comparator",
                            DateTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                            // Only the text findings become tracked changes. The summary, the findings table and the feature and
                            // review findings are extra report content the tool can add to the file, which is not a redline of the text.
                            IncludeSummary = false,
                            IncludeFindingsTable = false,
                            TrackTextFindings = true,
                            TrackFeatureFindings = false,
                            TrackReviewFindings = false,
                            TrackFormattingFindings = false,
                        });
                        break;
                    default:
                        throw new ArgumentException("unknown tool " + tool);
                }

                records.Add(new RunRecord(id, tool, true, sw.Elapsed.TotalMilliseconds, new FileInfo(target).Length, null));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                records.Add(new RunRecord(id, tool, false, sw.Elapsed.TotalMilliseconds, 0, e.GetType().Name + ": " + e.Message.ReplaceLineEndings(" ")));
            }
        });

        File.AppendAllLines(log, records.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => JsonSerializer.Serialize(r)));
        var all = File.ReadLines(log).Select(l => JsonSerializer.Deserialize<RunRecord>(l)!).ToList();
        Console.WriteLine($"{tool}: {all.Count} pairs recorded, {all.Count(r => r.Ok)} ok, {all.Count(r => !r.Ok)} failed; this run {records.Count} pairs");
        foreach (RunRecord r in records.Where(r => !r.Ok).OrderBy(r => r.Id, StringComparer.Ordinal).Take(5))
        {
            Console.Error.WriteLine($"{r.Id}: {r.Error}");
        }

        return 0;
    }

    private static string VersionOf(Assembly a) =>
        a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? a.GetName().Version?.ToString() ?? "?";
}
