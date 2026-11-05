using System.Runtime.CompilerServices;
using OneRender.Pdf;
using OnePdf;
using OnePdf.Text;

namespace OneDiff.Pdf.Tests;

/// <summary>
/// Behaviours of the renderer (RecursiveAI.OneRender.Pdf 1.3.2) that the rendered redline works around or accepts, each pinned by a
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
    /// A table row whose two cells are each longer than the page, cut at a page break: the lines that pass the bottom of the page are
    /// drawn outside the page area (so clipped away) and are not continued on the next page. The side-by-side page therefore keeps
    /// each row whole where it fits on a page (a grid row with <c>break-inside: avoid</c>).
    /// </summary>
    [Fact]
    public void ARowCutAtAPageBreakLosesLines()
    {
        string html = File.ReadAllText(Path.Combine(TestData(), "table-row-lines-lost.html"));
        int drawn = html.Count(c => c == 'z');
        byte[] pdf = HtmlPdf.ToPdf(html, new HtmlPdfOptions());
        Assert.True(Zs(pdf, includeClipped: false) < drawn, "lines past the page bottom are not visible");
        Assert.Equal(drawn, Zs(pdf, includeClipped: true));
    }

    /// <summary>An emoji sequence loses its zero-width joiner in the text layer when no emoji font is present (the sequence is drawn from fallback faces or not at all).</summary>
    [Fact]
    public void AZeroWidthJoinerInAnEmojiSequenceIsNotInTheTextLayer()
    {
        byte[] pdf = HtmlPdf.ToPdf("<p>a \U0001F469\u200D\U0001F4BB b</p>", new HtmlPdfOptions());
        using PdfDocument d = PdfDocument.Open(pdf);
        string text = d.ExtractText(new PdfTextOptions { DetectHeadersAndFooters = false, MaxDegreeOfParallelism = 1 })[0].Text;
        Assert.Contains("\U0001F469", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u200D', text);
    }
}
