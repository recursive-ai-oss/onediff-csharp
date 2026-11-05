using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Docxodus;
using OfficeIMO.Word;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Word;

namespace OneDiff.Bench.Docx;

/// <summary>
/// Document in, tracked-changes document out, for each tool: read both files, compare, write the result. The pairs are the
/// gold-set pairs joined into one ("OneDiff.GoldSets.Docx bench-pairs"), by size: n1 is about five pages with about eight changes,
/// n8 about forty pages and n24 about a hundred and twenty. Every tool runs with its default settings, one thread, and the same
/// input files. Set ONEDIFF_DOCX_BENCH to the folder holding n1, n8 and n24.
/// <para>
/// OneDiff is the same shape: read both files through the Word source, compare, write the tracked-changes document.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 5, invocationCount: 1, launchCount: 1)]
public class DocxBench
{
    private string old = string.Empty;
    private string current = string.Empty;
    private string output = string.Empty;

    [Params("n1", "n8", "n24")]
    public string Size { get; set; } = "n1";

    [GlobalSetup]
    public void Setup()
    {
        string root = Environment.GetEnvironmentVariable("ONEDIFF_DOCX_BENCH") ?? throw new InvalidOperationException("set ONEDIFF_DOCX_BENCH");
        old = Path.Combine(root, Size, "old.docx");
        current = Path.Combine(root, Size, "new.docx");
        output = Path.Combine(Path.GetTempPath(), "docx-bench-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".docx");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (File.Exists(output))
        {
            File.Delete(output);
        }
    }

    [Benchmark]
    public long OneDiff()
    {
        var source = new WordDocumentSource();
        Comparison c = Comparison.Of(source.Read(DocumentInput.FromBytes(File.ReadAllBytes(old), "old.docx")), source.Read(DocumentInput.FromBytes(File.ReadAllBytes(current), "new.docx")));
        using (FileStream fs = File.Create(output))
        {
            new DocxRedline().Create(c, fs);
        }

        return new FileInfo(output).Length;
    }

    [Benchmark]
    public long Docxodus()
    {
        WmlDocument result = DocxCompare.Compare(new WmlDocument(old), new WmlDocument(current));
        result.SaveAs(output);
        return new FileInfo(output).Length;
    }

    [Benchmark]
    public long OfficeImo()
    {
        WordDocumentComparer.CreateRedlineDocument(old, current, output, new WordComparisonRedlineOptions
        {
            Mode = WordComparisonRedlineMode.InPlaceTarget,
            Author = "comparator",
            DateTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IncludeSummary = false,
            IncludeFindingsTable = false,
            TrackFeatureFindings = false,
            TrackReviewFindings = false,
            TrackFormattingFindings = false,
        });
        return new FileInfo(output).Length;
    }
}

internal static class Program
{
    private static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
