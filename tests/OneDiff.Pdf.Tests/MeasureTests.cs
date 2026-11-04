using System.Diagnostics;
using System.Security.Cryptography;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;

namespace OneDiff.Pdf.Tests;

/// <summary>
/// Opt-in measurements, run by the scripts that publish them. They do nothing unless their environment variable is set:
/// ONEDIFF_HASH_OUT=file writes the SHA-256 of generated inputs, comparisons and marked PDFs (to compare machines);
/// ONEDIFF_BUDGET=file times a 100-page pair end to end and writes the figures.
/// </summary>
public class MeasureTests
{
    private static string Hex(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    [Fact]
    public void HashesOfGeneratedPairs()
    {
        string? path = Environment.GetEnvironmentVariable("ONEDIFF_HASH_OUT");
        if (path is null)
        {
            Assert.Skip("ONEDIFF_HASH_OUT is not set");
        }

        var lines = new List<string>();
        foreach (int seed in new[] { 0, 1, 2, 3, 4, 5, 11, 17, 23, 31 })
        {
            Run run = GateSetTests.PairForMeasure(seed);
            lines.Add($"seed{seed} old {Hex(run.OldPdf)}");
            lines.Add($"seed{seed} new {Hex(run.NewPdf)}");
            lines.Add($"seed{seed} json {Hex(ComparisonJson.Write(run.Comparison))}");
            lines.Add($"seed{seed} marked {Hex(run.Marked)} marks={run.Result.Marks.Length} changes={run.Comparison.Changes.Length}");
            using var old = new MemoryStream();
            PdfRedline.OnPages.MarkOldDocument(run.Comparison, run.OldPdf, old);
            lines.Add($"seed{seed} markedOld {Hex(old.ToArray())}");
        }

        File.WriteAllLines(path, lines);
    }

    [Fact]
    public void HundredPagePairEndToEnd()
    {
        string? path = Environment.GetEnvironmentVariable("ONEDIFF_BUDGET");
        if (path is null)
        {
            Assert.Skip("ONEDIFF_BUDGET is not set");
        }

        var r = new Random(2026);
        List<string> before = [.. Enumerable.Range(0, 520).Select(_ => GateSetTests.Paragraph(r))];
        List<string> after = before;
        // About 300 edits, a few at a time so that each kind of edit is exercised.
        for (int i = 0; i < 100; i++)
        {
            after = GateSetTests.Edit(r, after);
        }

        var spec = new Fixtures.Spec { Header = "Master Services Agreement", PageNumbers = true, PageHeight = 842, Size = 10 };
        byte[] a = Fixtures.Make(spec with { Paragraphs = [.. before] });
        byte[] b = Fixtures.Make(spec with { Paragraphs = [.. after] });

        var sw = Stopwatch.StartNew();
        CompareDocument da = Run.Read(a, "old.pdf"), db = Run.Read(b, "new.pdf");
        long read = sw.ElapsedMilliseconds;
        sw.Restart();
        Comparison c = Comparison.Of(da, db);
        long compare = sw.ElapsedMilliseconds;
        sw.Restart();
        using var ms = new MemoryStream();
        PdfMarkupResult res = PdfRedline.OnPages.MarkNewDocument(c, b, ms);
        long mark = sw.ElapsedMilliseconds;
        using var proc = Process.GetCurrentProcess();
        int nodes = db.Nodes.Length;
        File.WriteAllText(path,
            $"pagesOld={PageCount(a)} pagesNew={PageCount(b)} bytesOld={a.Length} bytesNew={b.Length} nodesNew={nodes}\n" +
            $"readBothMs={read} compareMs={compare} markMs={mark} totalMs={read + compare + mark}\n" +
            $"changes={c.Changes.Length} marks={res.Marks.Length} skipped={res.Skipped.Length} fallback={c.Verification.FallbackLevel} check={c.Check().Length}\n" +
            $"peakWorkingSetMB={proc.PeakWorkingSet64 / (1024 * 1024)}\n");
        Assert.Empty(c.Check());
    }

    private static int PageCount(byte[] pdf)
    {
        using OnePdf.PdfDocument d = OnePdf.PdfDocument.Open(pdf);
        return d.PageCount;
    }
}
