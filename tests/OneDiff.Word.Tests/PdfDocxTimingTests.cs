using System.Diagnostics;
using System.Globalization;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Pdf;
using OnePdf;
using OnePdf.Layout;

namespace OneDiff.Word.Tests;

/// <summary>
/// Opt-in timing of a PDF pair written as a reconstructed tracked DOCX (it does nothing unless ONEDIFF_PDFDOCX_TIMING_OUT is set): a
/// pair of ONEDIFF_PDFDOCX_TIMING_PAGES pages (default 100; 17 paragraphs a page, about 3 edits a page) is generated once, then one untimed
/// warm-up and ONEDIFF_PDFDOCX_TIMING_REPS (default 3) timed repetitions read both PDFs, compare, and write the DOCX; the file gets the
/// median of each step in milliseconds. Run it with DOTNET_PROCESSOR_COUNT=4.
/// </summary>
public class PdfDocxTimingTests
{
    private static readonly string[] Words = [.. "the supplier customer shall deliver pay invoice within days of receipt agreement party terminate notice written liability warranty software services schedule confidential information governed laws term renew fees interest late damages cap indemnify breach cure assign consent amend waiver notice address".Split(' ')];

    private static string Sentence(Random r) => string.Join(' ', Enumerable.Range(0, 8 + r.Next(10)).Select(_ => Words[r.Next(Words.Length)])) + ".";

    private static string Paragraph(Random r) => string.Join(' ', Enumerable.Range(0, 1 + r.Next(4)).Select(_ => Sentence(r)));

    private static byte[] Pdf(IReadOnlyList<string> paragraphs) =>
        LayoutDocument.Compose(d =>
        {
            d.Title("timing");
            d.Page(page =>
            {
                page.Size(595, 842);
                page.Margin(54);
                page.Header().PaddingBottom(24).AlignCenter().Text("Master Services Agreement").FontSize(9);
                page.Footer().PaddingTop(24).AlignCenter().Text(t =>
                {
                    t.FontSize(9);
                    t.Span("Page ");
                    t.CurrentPageNumber();
                });
                page.Content().Column(col =>
                {
                    col.Spacing(6);
                    foreach (string p in paragraphs)
                    {
                        col.Item().Text(p).FontSize(10);
                    }
                });
            });
        }).ToBytes(new PdfDocumentWriterOptions { DocumentId = new byte[16] });

    [Fact]
    public void TimeAPdfPairToATrackedDocx()
    {
        string? path = Environment.GetEnvironmentVariable("ONEDIFF_PDFDOCX_TIMING_OUT");
        if (path is null)
        {
            Assert.Skip("ONEDIFF_PDFDOCX_TIMING_OUT is not set");
        }

        int pages = int.Parse(Environment.GetEnvironmentVariable("ONEDIFF_PDFDOCX_TIMING_PAGES") ?? "100", CultureInfo.InvariantCulture);
        int reps = int.Parse(Environment.GetEnvironmentVariable("ONEDIFF_PDFDOCX_TIMING_REPS") ?? "3", CultureInfo.InvariantCulture);
        var r = new Random(2026);
        List<string> before = [.. Enumerable.Range(0, pages * 17).Select(_ => Paragraph(r))];
        List<string> after = [.. before];
        for (int i = 0; i < pages * 3; i++)
        {
            int at = r.Next(after.Count);
            switch (r.Next(3))
            {
                case 0:
                    after.RemoveAt(at);
                    break;
                case 1:
                    after.Insert(at, Paragraph(r));
                    break;
                default:
                    {
                        string[] w = after[at].Split(' ');
                        w[r.Next(w.Length)] = Words[r.Next(Words.Length)] + "s";
                        after[at] = string.Join(' ', w);
                        break;
                    }
            }
        }

        byte[] a = Pdf(before), b = Pdf(after);
        PdfRedline.Register();
        var read = new List<double>();
        var compare = new List<double>();
        var write = new List<double>();
        var total = new List<double>();
        int changes = 0, notCarried = 0, outBytes = 0;
        for (int rep = -1; rep < reps; rep++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var src = new PdfDocumentSource();
            CompareDocument o = src.Read(DocumentInput.FromBytes(a, "old.pdf"));
            CompareDocument n = src.Read(DocumentInput.FromBytes(b, "new.pdf"));
            long t1 = Stopwatch.GetTimestamp();
            Comparison cmp = Comparison.Of(o, n, CompareSettings.Legal);
            long t2 = Stopwatch.GetTimestamp();
            using var ms = new MemoryStream();
            DocxRedlineResult res = new DocxRedline().Create(cmp, ms);
            long t3 = Stopwatch.GetTimestamp();
            changes = cmp.Changes.Length;
            notCarried = res.NotCarried.Length;
            outBytes = (int)ms.Length;
            if (rep >= 0)
            {
                read.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
                compare.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
                write.Add(Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds);
                total.Add(Stopwatch.GetElapsedTime(t0, t3).TotalMilliseconds);
            }
        }

        static string Median(List<double> v) => v.Order().ElementAt(v.Count / 2).ToString("F0", CultureInfo.InvariantCulture);
        int pagesNew;
        using (PdfDocument d = PdfDocument.Open(b))
        {
            pagesNew = d.PageCount;
        }

        File.WriteAllText(path,
            $"pages_new={pagesNew} changes={changes} not_carried={notCarried} docx_bytes={outBytes} reps={reps}\n"
            + $"read_ms={Median(read)} compare_ms={Median(compare)} write_docx_ms={Median(write)} total_ms={Median(total)}\n");
    }
}
