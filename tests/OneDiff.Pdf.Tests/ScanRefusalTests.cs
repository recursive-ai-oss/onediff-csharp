using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;

namespace OneDiff.Pdf.Tests;

/// <summary>A PDF that cannot be read as text is refused with a message that says what to do; it is never compared as an empty document.</summary>
public class ScanRefusalTests
{
    private static readonly string[] Old = ["The supplier shall deliver within thirty days.", "Payment is due in sixty days."];
    private static readonly string[] New = ["The supplier shall deliver within forty days.", "Payment is due in sixty days.", "Notices must be written."];

    [Fact]
    public void APdfWithoutATextLayerIsRefusedNamingTheOcrPackage()
    {
        byte[] noText = Fixtures.Make(new Fixtures.Spec { Paragraphs = [] });
        var input = DocumentInput.FromBytes(noText, "scan.pdf");

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => new PdfDocumentSource().Read(input));
        Assert.Equal(PdfSourceFailure.NoTextLayer, PdfSourceErrors.FailureOf(e));
        Assert.Contains("RecursiveAI.OneDiff.Ocr", e.Message, StringComparison.Ordinal);
        Assert.Contains("OcrRedline.Register()", e.Message, StringComparison.Ordinal);

        // Through the comparison, in every mode: two different image-only files never compare as identical.
        var other = DocumentInput.FromBytes(Fixtures.Make(new Fixtures.Spec { Paragraphs = Old }), "digital.pdf");
        PdfRedline.Register();
        Assert.Throws<InvalidDataException>(() => Comparison.Of(input, other));
        Assert.Throws<InvalidDataException>(() => Comparison.Of(input, other, CompareSettings.Strict));
    }

    [Fact]
    public void ATruncatedPdfIsNotReadAsAnEmptyDocument()
    {
        byte[] good = Fixtures.Make(new Fixtures.Spec { Paragraphs = Old });
        foreach (int keep in new[] { 8, 120, good.Length / 3 })
        {
            var input = DocumentInput.FromBytes(good.AsMemory(0, keep), "cut.pdf");
            InvalidDataException e = Assert.Throws<InvalidDataException>(() => new PdfDocumentSource().Read(input));
            Assert.NotNull(PdfSourceErrors.FailureOf(e));
        }
    }

    [Fact]
    public void AutoChoosesFromTheComparisonAndNeverFromProcessState()
    {
        byte[] oldPdf = Fixtures.Make(new Fixtures.Spec { Paragraphs = Old });
        byte[] newPdf = Fixtures.Make(new Fixtures.Spec { Paragraphs = New });
        Comparison c = Comparison.Of(Run.Read(oldPdf, "a.pdf"), Run.Read(newPdf, "b.pdf"));

        // A PDF's own pages are marked: the original bytes stay in place.
        Assert.True(c.ToBytes(PdfRedline.Auto).AsSpan().StartsWith(newPdf));

        // The same comparison read back from JSON has no PDF to mark. It says so instead of writing a different kind of file.
        Comparison back = ComparisonJson.Read(ComparisonJson.Write(c));
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => back.ToBytes(PdfRedline.Auto));
        Assert.Contains("PdfRedline.Rendered", e.Message, StringComparison.Ordinal);
        Assert.Contains("MarkNewDocument", e.Message, StringComparison.Ordinal);

        // The explicit choices work whatever the comparison's history, and marking by hand with the bytes still works.
        byte[] rendered = back.ToBytes(PdfRedline.Rendered);
        Assert.True(rendered.AsSpan().StartsWith("%PDF-"u8));
        Assert.False(rendered.AsSpan().StartsWith(newPdf));
        using var marked = new MemoryStream();
        Assert.NotEmpty(PdfRedline.OnPages.MarkNewDocument(back, newPdf, marked).Marks);

        // Plain text is never paged input: a rendered redline.
        Comparison text = Comparison.Of(CompareDocument.FromText("one two"), CompareDocument.FromText("one three"));
        Assert.True(text.ToBytes(PdfRedline.Auto).AsSpan().StartsWith("%PDF-"u8));
    }
}
