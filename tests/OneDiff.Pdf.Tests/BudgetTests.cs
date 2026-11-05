using System.Diagnostics;
using System.Globalization;
using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Pdf.Tests;

/// <summary>
/// Opt-in timing of the document budgets, run by <c>bench/run-budgets.sh</c> (it does nothing unless ONEDIFF_BUDGET_OUT is set).
/// One process times one part, so that its peak memory belongs to that part alone:
/// ONEDIFF_BUDGET_PART = <c>markup</c> (read both PDFs, compare, mark the new document's pages),
/// <c>rendered-inline</c> or <c>rendered-side-by-side</c> (read, compare, then write the rendered redline);
/// ONEDIFF_BUDGET_PAGES = pages of the new document (default 100; about 17 paragraphs a page, about 3 changes a page);
/// ONEDIFF_BUDGET_DIR = folder for the generated pair: part <c>generate</c> writes old.pdf and new.pdf there, and the timed parts read them, so
/// that a timed part's peak memory does not include the cost of writing the inputs;
/// ONEDIFF_BUDGET_REPS = timed repetitions after one untimed warm-up (default 3).
/// The result is key=value lines in the file named by ONEDIFF_BUDGET_OUT.
/// </summary>
public class BudgetTests
{
    [Fact]
    public void TimePart()
    {
        string? path = Environment.GetEnvironmentVariable("ONEDIFF_BUDGET_OUT");
        if (path is null)
        {
            Assert.Skip("ONEDIFF_BUDGET_OUT is not set");
        }

        string part = Environment.GetEnvironmentVariable("ONEDIFF_BUDGET_PART") ?? "markup";
        int pages = int.Parse(Environment.GetEnvironmentVariable("ONEDIFF_BUDGET_PAGES") ?? "100", CultureInfo.InvariantCulture);
        int reps = int.Parse(Environment.GetEnvironmentVariable("ONEDIFF_BUDGET_REPS") ?? "3", CultureInfo.InvariantCulture);
        Assert.True(part is "generate" or "markup" or "rendered-inline" or "rendered-side-by-side", "unknown ONEDIFF_BUDGET_PART");
        string? dir = Environment.GetEnvironmentVariable("ONEDIFF_BUDGET_DIR");

        using var proc = Process.GetCurrentProcess();
        long baseline = proc.WorkingSet64;

        byte[] a, b;
        if (part != "generate" && dir is not null && File.Exists(Path.Combine(dir, "new.pdf")))
        {
            a = File.ReadAllBytes(Path.Combine(dir, "old.pdf"));
            b = File.ReadAllBytes(Path.Combine(dir, "new.pdf"));
        }
        else
        {
            // The same generator and seed as the end-to-end measurement: 17 paragraphs a page, then about 1.2 edits a page.
            var r = new Random(2026);
            List<string> before = [.. Enumerable.Range(0, pages * 17).Select(_ => GateSetTests.Paragraph(r))];
            List<string> after = before;
            for (int i = 0; i < pages * 6 / 5; i++)
            {
                after = GateSetTests.Edit(r, after);
            }

            var spec = new Fixtures.Spec { Header = "Master Services Agreement", PageNumbers = true, PageHeight = 842, Size = 10 };
            a = Fixtures.Make(spec with { Paragraphs = [.. before] });
            b = Fixtures.Make(spec with { Paragraphs = [.. after] });
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "old.pdf"), a);
                File.WriteAllBytes(Path.Combine(dir, "new.pdf"), b);
            }
        }

        int newPages;
        using (OnePdf.PdfDocument d = OnePdf.PdfDocument.Open(b))
        {
            newPages = d.PageCount;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        proc.Refresh();
        // Inputs are in memory now; what the run adds above this is the cost of the work itself.
        long inputsResident = proc.WorkingSet64;

        if (part == "generate")
        {
            File.WriteAllText(path, $"generated pages_new={newPages} bytes_old={a.Length} bytes_new={b.Length}\n");
            return;
        }

        var lines = new List<string>
        {
            $"part={part}",
            $"pages_old={PageCount(a)} pages_new={newPages} bytes_old={a.Length} bytes_new={b.Length}",
        };
        var totals = new List<double>();
        for (int rep = -1; rep < reps; rep++)
        {
            var sw = Stopwatch.StartNew();
            CompareDocument da = Run.Read(a, "old.pdf"), db = Run.Read(b, "new.pdf");
            double read = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            Comparison c = Comparison.Of(da, db);
            double compare = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            int outBytes;
            string detail;
            using (var ms = new MemoryStream())
            {
                if (part == "markup")
                {
                    PdfMarkupResult res = PdfRedline.OnPages.MarkNewDocument(c, b, ms);
                    detail = $"marks={res.Marks.Length} skipped={res.Skipped.Length}";
                }
                else
                {
                    RenderedPdfResult res = (part == "rendered-inline" ? RenderedPdfRedline.Inline : RenderedPdfRedline.SideBySideLayout).Render(c, ms);
                    detail = $"rendered_pages={res.PageCount}";
                }

                outBytes = (int)ms.Length;
            }

            double output = sw.Elapsed.TotalMilliseconds;
            double total = read + compare + output;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"rep={(rep < 0 ? "warm-up" : rep.ToString(CultureInfo.InvariantCulture))} read_ms={read:F0} compare_ms={compare:F0} output_ms={output:F0} total_ms={total:F0} changes={c.Changes.Length} {detail} out_bytes={outBytes} fallback={c.Verification.FallbackLevel}"));
            if (rep >= 0)
            {
                totals.Add(total);
            }

            Assert.Empty(c.Check());
        }

        totals.Sort();
        proc.Refresh();
        long peak = proc.PeakWorkingSet64;
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"median_total_ms={totals[totals.Count / 2]:F0} min_total_ms={totals[0]:F0} max_total_ms={totals[^1]:F0}"));
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"peak_working_set_MB={peak / (1024 * 1024)} baseline_MB={baseline / (1024 * 1024)} inputs_resident_MB={inputsResident / (1024 * 1024)} bytes_per_page={(peak - inputsResident) / Math.Max(newPages, 1)}"));
        File.WriteAllLines(path, lines);
    }

    private static int PageCount(byte[] pdf)
    {
        using OnePdf.PdfDocument d = OnePdf.PdfDocument.Open(pdf);
        return d.PageCount;
    }
}
