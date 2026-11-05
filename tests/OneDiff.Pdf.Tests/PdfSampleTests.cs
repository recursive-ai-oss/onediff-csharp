using System.Runtime.CompilerServices;
using OneDiff.Documents;
using OneDiff.Tests.Shared;

namespace OneDiff.Pdf.Tests;

/// <summary>
/// The sample PDFs under docs/samples: the rendered redline in each preset, inline and side by side, from the short sample pair, and
/// one redline marked on the original pages of a PDF. The test compares freshly written files with the committed ones, so a change in
/// the look is a visible change in review; set ONEDIFF_UPDATE_SAMPLES=1 to rewrite them.
/// </summary>
public class PdfSampleTests
{
    private static string SamplesDir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "samples"));

    private static readonly RedlineStyle[] Presets = [RedlineStyle.Blackline, RedlineStyle.SideBySide, RedlineStyle.Word, RedlineStyle.Developer];

    private static bool Update => Environment.GetEnvironmentVariable("ONEDIFF_UPDATE_SAMPLES") == "1";

    private static void CheckSample(string file, byte[] bytes)
    {
        if (Update)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
            return;
        }

        Assert.True(File.Exists(file), $"missing sample {file}; run with ONEDIFF_UPDATE_SAMPLES=1");
        Assert.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes), $"sample {Path.GetFileName(file)} is out of date; run with ONEDIFF_UPDATE_SAMPLES=1");
    }

    [Fact]
    public void RenderedSamplesMatchTheWriters()
    {
        Comparison c = SamplePair.Create();
        foreach (RedlineStyle style in Presets)
        {
            foreach (bool sbs in new[] { false, true })
            {
                RedlineForm form = sbs ? PdfRedline.RenderedSideBySide : PdfRedline.Rendered;
                byte[] pdf = c.ToBytes(form, style);
                CheckSample(Path.Combine(SamplesDir(), style.Name.ToLowerInvariant() + (sbs ? "-side-by-side.pdf" : "-inline.pdf")), pdf);
            }
        }
    }

    /// <summary>
    /// Original-page markup: two short PDFs written for the purpose, compared and marked on the pages of the new one, so the new
    /// document's pages stay as they were and the review sits on top as highlights, carets and notes.
    /// </summary>
    [Fact]
    public void MarkupSampleMatchesTheWriter()
    {
        byte[] oldPdf = Fixtures.Make(new Fixtures.Spec
        {
            Paragraphs =
            [
                "Services Agreement",
                "1.1 The supplier shall deliver the goods within thirty days of the order.",
                "1.2 Payment is due within sixty days of receipt of the invoice.",
                "1.3 Either party may terminate this agreement on written notice.",
                "1.4 The supplier shall keep the goods insured against fire until delivery.",
            ],
        });
        byte[] newPdf = Fixtures.Make(new Fixtures.Spec
        {
            Paragraphs =
            [
                "Services Agreement",
                "1.1 The supplier shall deliver the goods within forty-five days of the order.",
                "1.2 Payment is due within sixty days of receipt of the invoice.",
                "1.3 Either party may terminate this agreement on thirty days written notice.",
                "1.4 Disputes shall be referred to arbitration in London.",
            ],
        });
        Comparison c = Comparison.Of(Run.Read(oldPdf, "services-v1.pdf"), Run.Read(newPdf, "services-v2.pdf"));
        byte[] marked = c.ToBytes(PdfRedline.OnPages);
        Assert.True(marked.AsSpan().StartsWith(newPdf));
        Assert.NotEmpty(Run.ReadAnnotations(marked));
        CheckSample(Path.Combine(SamplesDir(), "markup-on-pages.pdf"), marked);
    }
}

