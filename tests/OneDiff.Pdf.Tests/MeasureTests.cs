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
        List<string> before = [.. Enumerable.Range(0, 1700).Select(_ => GateSetTests.Paragraph(r))];
        List<string> after = before;
        // About 300 edits, a few at a time so that each kind of edit is exercised.
        for (int i = 0; i < 120; i++)
        {
            after = GateSetTests.Edit(r, after);
        }

        var spec = new Fixtures.Spec { Header = "Master Services Agreement", PageNumbers = true, PageHeight = 842, Size = 10 };
        byte[] a = Fixtures.Make(spec with { Paragraphs = [.. before] });
        byte[] b = Fixtures.Make(spec with { Paragraphs = [.. after] });

        var sw = Stopwatch.StartNew();
        using (OnePdf.PdfDocument raw = OnePdf.PdfDocument.Open(b))
        {
            _ = raw.ExtractText(new OnePdf.Text.PdfTextOptions { RetainGlyphs = true, DetectHeadersAndFooters = true, MaxDegreeOfParallelism = 1 });
        }

        long extractOne = sw.ElapsedMilliseconds;
        sw.Restart();
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
            $"extractNewDocOnlyMs={extractOne} readBothMs={read} compareMs={compare} markMs={mark} totalMs={read + compare + mark}\n" +
            $"changes={c.Changes.Length} marks={res.Marks.Length} skipped={res.Skipped.Length} fallback={c.Verification.FallbackLevel} check={c.Check().Length}\n" +
            $"peakWorkingSetMB={proc.PeakWorkingSet64 / (1024 * 1024)}\n");
        Assert.Empty(c.Check());
    }

    /// <summary>
    /// ONEDIFF_RENDER_BUDGET=file: times a rendered redline of a synthetic contract of 3,000 paragraphs with about 300 edits, and of the
    /// 100-page PDF pair, splitting the page's HTML from the renderer. Each input is run three times in the order given; the first run
    /// is a warm-up and is reported apart.
    /// </summary>
    [Fact]
    public void RenderedRedlineCost()
    {
        string? path = Environment.GetEnvironmentVariable("ONEDIFF_RENDER_BUDGET");
        if (path is null)
        {
            Assert.Skip("ONEDIFF_RENDER_BUDGET is not set");
        }

        var r = new Random(7);
        string[] words = ["party", "agreement", "shall", "payment", "within", "days", "notice", "term", "services", "delivery", "liability", "clause", "the", "of", "and", "to", "in", "under", "this", "such"];
        string Para() => string.Join(' ', Enumerable.Range(0, r.Next(10, 22)).Select(_ => words[r.Next(words.Length)])) + ".";
        List<string> before = [.. Enumerable.Range(0, 3000).Select(_ => Para())];
        List<string> after = [.. before];
        for (int i = 0; i < 300; i++)
        {
            int k = r.Next(after.Count);
            string[] w = after[k].Split(' ');
            w[r.Next(w.Length)] = words[r.Next(words.Length)] + r.Next(100);
            after[k] = string.Join(' ', w);
        }

        var lines = new List<string>();
        using var proc = Process.GetCurrentProcess();
        void Measure(string name, Comparison c)
        {
            foreach (bool sbs in new[] { false, true })
            {
                for (int rep = 0; rep < 3; rep++)
                {
                    long alloc0 = GC.GetAllocatedBytesForCurrentThread();
                    var sw = Stopwatch.StartNew();
                    string html = OneDiff.Documents.Html.HtmlRedline.Static(c, sbs ? RedlineStyle.SideBySide : RedlineStyle.Blackline, sbs, "A4");
                    double htmlMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    using var ms = new MemoryStream();
                    OneRender.Pdf.HtmlPdfResult res = OneRender.Pdf.HtmlPdf.ToPdf(html, ms, new OneRender.Pdf.HtmlPdfOptions { Output = new OneRender.Pdf.PdfOutputOptions { LeaveOpen = true } });
                    double renderMs = sw.Elapsed.TotalMilliseconds;
                    proc.Refresh();
                    lines.Add($"{name} layout={(sbs ? "side-by-side" : "inline")} run={rep}{(rep == 0 ? "(warm-up)" : "")} blocks={c.New.Nodes.Length} changes={c.Changes.Length} htmlBytes={html.Length} pages={res.PageCount} pdfBytes={ms.Length} html_ms={htmlMs:F0} render_ms={renderMs:F0} total_ms={htmlMs + renderMs:F0} thread_alloc_MB={(GC.GetAllocatedBytesForCurrentThread() - alloc0) / (1024 * 1024)} peakWorkingSetMB={proc.PeakWorkingSet64 / (1024 * 1024)} gcHeapMB={GC.GetTotalMemory(false) / (1024 * 1024)}");
                }
            }
        }

        Comparison contract = Comparison.Of(DocumentInput.FromBytes(System.Text.Encoding.UTF8.GetBytes(string.Join("\n\n", before)), "contract-v1.txt"), DocumentInput.FromBytes(System.Text.Encoding.UTF8.GetBytes(string.Join("\n\n", after)), "contract-v2.txt"));
        Measure("contract", contract);

        var rp = new Random(2026);
        List<string> pb = [.. Enumerable.Range(0, 1700).Select(_ => GateSetTests.Paragraph(rp))];
        List<string> pa = pb;
        for (int i = 0; i < 120; i++)
        {
            pa = GateSetTests.Edit(rp, pa);
        }

        var spec = new Fixtures.Spec { Header = "Master Services Agreement", PageNumbers = true, PageHeight = 842, Size = 10 };
        Measure("pdfpair", Comparison.Of(Run.Read(Fixtures.Make(spec with { Paragraphs = [.. pb] }), "old.pdf"), Run.Read(Fixtures.Make(spec with { Paragraphs = [.. pa] }), "new.pdf")));
        File.WriteAllLines(path, lines);
    }

    private static int PageCount(byte[] pdf)
    {
        using OnePdf.PdfDocument d = OnePdf.PdfDocument.Open(pdf);
        return d.PageCount;
    }
}
