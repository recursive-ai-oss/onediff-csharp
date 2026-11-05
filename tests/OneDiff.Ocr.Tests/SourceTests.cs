using System.Security.Cryptography;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;
using OneDiff.Pdf;
using OnePdf;

namespace OneDiff.Ocr.Tests;

// Rasterizes scanned PDFs through OneImage.Pdf, which fails to load an internal OneImage type under the browser runtime
// (TypeLoadException for OneImage.DeterministicMath, still with OneImage 1.10.5); the browser run skips these until that is fixed upstream.
[Trait("Host", "Native")]
public class SourceTests
{
    private static readonly string[] Old = ["The supplier shall deliver the goods within thirty days of the order.", "Payment is due in sixty days from the invoice date.", "This agreement is governed by the laws of Delaware."];
    private static readonly string[] New = ["The supplier shall deliver the goods within forty days of the order.", "Payment is due in sixty days from the invoice date.", "This agreement is governed by the laws of Delaware."];

    private static CompareDocument Read(byte[] bytes, string name) => new OcrDocumentSource().Read(DocumentInput.FromBytes(bytes, name));

    [Fact]
    public void ReadsAnImageIntoParagraphsWithWordAnchorsAndConfidence()
    {
        CompareDocument d = Read(Scans.Png(Old), "scan.png");
        Assert.Equal("image/png", d.SourceFormat);
        Node[] paragraphs = [.. d.Nodes.Where(static n => n.Kind == NodeKind.Paragraph)];
        Assert.Equal(3, paragraphs.Length);
        Assert.Equal(Old[1], paragraphs[1].Text);
        foreach (Node p in paragraphs)
        {
            Assert.Equal(AnchorKind.OcrWords, p.Anchor.Kind);
            Assert.InRange(p.Anchor.Confidence ?? -1, 0.0, 1.0);
            Assert.All(p.Segments, static s =>
            {
                Assert.Equal(AnchorKind.OcrWords, s.Anchor.Kind);
                Assert.Single(s.Anchor.Quads);
                Assert.True(s.Anchor.Quads[0].IsValid);
            });
        }

        // Upright page space: the first paragraph is above the second, so its quads have the larger y.
        Assert.True(paragraphs[0].Anchor.Quads[0].Y1 > paragraphs[1].Anchor.Quads[0].Y1);
    }

    [Fact]
    public void RecognizesInputsByContentAndTellsScansFromDigitalPdfs()
    {
        var source = new OcrDocumentSource();
        byte[] png = Scans.Png(Old);
        byte[] digital = Scans.DigitalPdf(Old);
        byte[] scanned = Scans.ScanPdf(png);
        Assert.True(source.CanRead(DocumentInput.FromBytes(png)));
        Assert.True(source.CanRead(DocumentInput.FromBytes(scanned)));
        Assert.False(source.CanRead(DocumentInput.FromBytes(digital)));
        Assert.False(source.CanRead(DocumentInput.FromString("plain text")));
        Assert.False(OcrDocumentSource.IsScannedPdf(digital));
        Assert.True(OcrDocumentSource.IsScannedPdf(scanned));
        CompareDocument d = Read(scanned, "scan.pdf");
        Assert.Equal("application/pdf", d.SourceFormat);
        Assert.Contains(d.Nodes, n => n.Text == Old[1]);
    }

    [Fact]
    public void ComparesTwoScansWithAFlaggedChangeInTheJson()
    {
        CompareDocument o = Read(Scans.Png(Old), "old.png"), n = Read(Scans.Png(New), "new.png");
        Comparison c = Comparison.Of(o, n, CompareSettings.Legal);
        Assert.True(c.Verification.Passed);
        Assert.Equal(0, c.Verification.FallbackLevel);
        Assert.Empty(c.Check());
        Change change = Assert.Single(c.Changes);
        Assert.Contains("thirty", change.Old.Text);
        Assert.Contains("forty", change.New.Text);
        Assert.NotNull(change.OcrConfidence);

        // A threshold above any confidence flags it; the default does not flag a clean word.
        Comparison strict = Comparison.Of(o, n, CompareSettings.Legal with { OcrConfidenceThreshold = 1.0 });
        Assert.True(strict.Changes.All(static x => x.PossibleOcrError));
        Comparison read = ComparisonJson.Read(ComparisonJson.Write(strict));
        Assert.Equal(strict.Changes.Select(static x => (x.PossibleOcrError, x.OcrConfidence)), read.Changes.Select(static x => (x.PossibleOcrError, x.OcrConfidence)));
        Assert.Equal(1.0, read.Settings.OcrConfidenceThreshold);
    }

    [Fact]
    public void MarksTheChangedWordsOnTheScannedPdfAndOnTheWrappedImage()
    {
        byte[] oldPng = Scans.Png(Old), newPng = Scans.Png(New);
        byte[] newScan = Scans.ScanPdf(newPng);
        Comparison c = Comparison.Of(Read(Scans.ScanPdf(oldPng), "old.pdf"), Read(newScan, "new.pdf"));

        using var scanOut = new MemoryStream();
        PdfMarkupResult r = PdfRedline.OnPages.MarkNewDocument(c, newScan, scanOut);
        Assert.NotEmpty(r.Marks);
        Assert.Empty(r.Skipped);
        using (PdfDocument marked = PdfDocument.Open(scanOut.ToArray()))
        {
            Assert.Contains(marked.Pages[0].Annotations, static a => a.Subtype == "Highlight");
        }

        // An image input is marked on a PDF page made from it, through the form the extension picks.
        Comparison img = Comparison.Of(Read(oldPng, "old.png"), Read(newPng, "new.png"));
        using var imageOut = new MemoryStream();
        PdfRedline.OnPages.Write(img, imageOut, RedlineStyle.SideBySide);
        using PdfDocument pdf = PdfDocument.Open(imageOut.ToArray());
        Assert.Equal(1, pdf.PageCount);
        Assert.Contains(pdf.Pages[0].Annotations, static a => a.Subtype == "Highlight");
    }

    [Fact]
    public void IsDeterministic()
    {
        byte[] a = Scans.Png(Old), b = Scans.Png(New);
        byte[] First() => ComparisonJson.Write(Comparison.Of(Read(a, "a.png"), Read(b, "b.png")));
        Assert.Equal(SHA256.HashData(First()), SHA256.HashData(First()));
    }

    [Fact]
    public void RejectsInputsItCannotRead()
    {
        var source = new OcrDocumentSource();
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => source.Read(DocumentInput.FromString("not a scan")));
        Assert.Equal(PdfSourceFailure.Malformed, PdfSourceErrors.FailureOf(e));
        byte[] truncated = Scans.Png(Old)[..200];
        e = Assert.Throws<InvalidDataException>(() => source.Read(DocumentInput.FromBytes(truncated)));
        Assert.Equal(PdfSourceFailure.Malformed, PdfSourceErrors.FailureOf(e));
        Assert.Throws<InvalidDataException>(() => new OcrDocumentSource(new OcrSourceOptions { MaxInputBytes = 100 }).Read(DocumentInput.FromBytes(Scans.Png(Old))));
    }

    [Fact]
    public void RegistrationRoutesScansAndDigitalPdfsToTheirSources()
    {
        OcrRedline.Register();
        OcrRedline.Register();
        Assert.IsType<OcrDocumentSource>(DocumentSources.Sniff(DocumentInput.FromBytes(Scans.Png(Old))));
        Assert.IsType<OcrDocumentSource>(DocumentSources.Sniff(DocumentInput.FromBytes(Scans.ScanPdf(Scans.Png(Old)))));
        Assert.IsType<PdfDocumentSource>(DocumentSources.Sniff(DocumentInput.FromBytes(Scans.DigitalPdf(Old))));
    }
}
