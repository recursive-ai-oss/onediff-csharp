using System.Runtime.CompilerServices;
using OneRender.Pdf;
using OnePdf;
using OnePdf.Text;

namespace OneDiff.Pdf.Tests;

/// <summary>
/// Behaviours of the renderer (RecursiveAI.OneRender.Pdf 1.3.3) that the rendered redline works around or accepts, each pinned by a
/// minimal case. They assert what the renderer does today, so a fixed renderer fails them on purpose: when one fails after an upgrade,
/// remove the workaround it names and flip the assertion.
/// </summary>
public class OneRenderGapTests
{
    private static string TestData([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "testdata", "onerender-gaps"));

    private static int Zs(byte[] pdf, bool includeClipped)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        var pages = d.ExtractText(new PdfTextOptions { DetectHeadersAndFooters = false, IncludeClippedAndOffPage = includeClipped, UseStructureTree = false, MaxDegreeOfParallelism = 1 });
        return string.Concat(pages.SelectMany(p => p.Words).Select(w => w.Text)).Count(c => c == 'z');
    }

    /// <summary>
    /// A table wider than the page area (the case that once looked like a table row losing its lines at a page break): the renderer
    /// draws the overflow into the side margins, so every line is still in the text layer and visible. The print stylesheets keep
    /// their own content inside the page area, which <see cref="RenderedGate"/> checks.
    /// </summary>
    [Fact]
    [Trait("Host", "Native")] // reads a fixture file
    public void AnOverwideTableIsDrawnIntoTheSideMargins()
    {
        string html = File.ReadAllText(Path.Combine(TestData(), "table-row-lines-lost.html"));
        int drawn = html.Count(c => c == 'z');
        byte[] pdf = HtmlPdf.ToPdf(html, new HtmlPdfOptions());
        Assert.Equal(drawn, Zs(pdf, includeClipped: false));
    }

    /// <summary>
    /// A table row whose cells differ in height, cut at page breaks: a line at a later cut is lost (here the 97th of 100 lines, beside a
    /// 40-line cell), although the table fits the page width. The side-by-side page therefore keeps each row whole where it fits on a
    /// page (a grid row with <c>break-inside: avoid</c>).
    /// </summary>
    [Fact]
    public void ARowOfUnequalCellsCutAtPageBreaksLosesALine()
    {
        static string Cell(int lines, string prefix) => string.Concat(Enumerable.Range(0, lines).Select(i => $"<div>{prefix}{i:D4}</div>"));
        string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>t</title><style>@page{size:A4 landscape;margin:18mm 16mm 20mm 16mm}body{font:10pt/1.5 sans-serif}"
            + "table{table-layout:fixed;width:100%;border-collapse:collapse}td{border:1px solid #ccc;padding:.25rem .6rem;vertical-align:top;width:50%}</style></head><body>"
            + "<table><tr><td>" + Cell(40, "L") + "</td><td>" + Cell(100, "R") + "</td></tr></table></body></html>";
        byte[] pdf = HtmlPdf.ToPdf(html, new HtmlPdfOptions());
        using PdfDocument d = PdfDocument.Open(pdf);
        string text = string.Join(" ", d.ExtractText(new PdfTextOptions { DetectHeadersAndFooters = false, UseStructureTree = false, MaxDegreeOfParallelism = 1 }).SelectMany(p => p.Words).Select(w => w.Text));
        Assert.Contains("R0095", text, StringComparison.Ordinal);
        Assert.DoesNotContain("R0096", text, StringComparison.Ordinal);
    }

    /// <summary>The zero-width joiner of an emoji sequence is in the text layer, and extraction keeps it (it was lost before the renderer and the extractor carried it).</summary>
    [Fact]
    public void AZeroWidthJoinerInAnEmojiSequenceIsInTheTextLayer()
    {
        byte[] pdf = HtmlPdf.ToPdf("<p>a 👩‍💻 b</p>", new HtmlPdfOptions());
        using PdfDocument d = PdfDocument.Open(pdf);
        string text = d.ExtractText(new PdfTextOptions { DetectHeadersAndFooters = false, MaxDegreeOfParallelism = 1 })[0].Text;
        Assert.Contains("👩‍💻", text, StringComparison.Ordinal);
    }
}
