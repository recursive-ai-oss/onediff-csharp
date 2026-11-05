using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Verification;
using OneDiff.Pdf;

namespace OneDiff.Ocr.Tests;

// Rasterizes scanned PDFs through OneImage.Pdf, which fails to load an internal OneImage type under the browser runtime
// (TypeLoadException for OneImage.DeterministicMath, still with OneImage 1.10.5); the browser run skips these until that is fixed upstream.
[Trait("Host", "Native")]
public class RegistrationOrderTests
{
    private static readonly string[] Old = ["The supplier shall deliver the goods within thirty days of the order.", "Payment is due in sixty days from the invoice date."];
    private static readonly string[] New = ["The supplier shall deliver the goods within forty days of the order.", "Payment is due in sixty days from the invoice date."];

    [Fact]
    public void ScansAreReadByOcrWhicheverRegisterCallCameLast()
    {
        DocumentInput scan1 = DocumentInput.FromBytes(Scans.ScanPdf(Scans.Png(Old)), "scan1.pdf");
        DocumentInput scan2 = DocumentInput.FromBytes(Scans.ScanPdf(Scans.Png(New)), "scan2.pdf");

        foreach (bool pdfLast in new[] { false, true, true })
        {
            if (pdfLast)
            {
                OcrRedline.Register();
                PdfRedline.Register();
            }
            else
            {
                PdfRedline.Register();
                OcrRedline.Register();
            }

            Assert.IsType<OcrDocumentSource>(DocumentSources.Sniff(scan1));
            Comparison c = Comparison.Of(scan1, scan2);
            Assert.NotEmpty(c.Changes);
        }
    }

    [Fact]
    public void ADamagedPdfWithNoPageContentIsRefusedEvenWithOcrRegistered()
    {
        using var doc = OnePdf.PdfDocument.CreateEmpty();
        doc.InsertBlankPage(0, 300, 300);
        using var ms = new MemoryStream();
        doc.Save(ms);
        DocumentInput damaged = DocumentInput.FromBytes(ms.ToArray(), "damaged.pdf");
        DocumentInput scan = DocumentInput.FromBytes(Scans.ScanPdf(Scans.Png(Old)), "scan.pdf");

        PdfRedline.Register();
        OcrRedline.Register();
        Assert.IsNotType<OcrDocumentSource>(DocumentSources.Sniff(damaged));
        Assert.Throws<InvalidDataException>(() => Comparison.Of(damaged, scan));
        Assert.Throws<InvalidDataException>(() => Comparison.Of(damaged, scan, CompareSettings.Strict));
    }

    [Fact]
    public void AScanWhereNothingIsRecognizedCarriesAWarningAndStrictThrows()
    {
        PdfRedline.Register();
        OcrRedline.Register();
        DocumentInput blank = DocumentInput.FromBytes(Scans.ScanPdf(Scans.Png([])), "blank.pdf");
        DocumentInput scan = DocumentInput.FromBytes(Scans.ScanPdf(Scans.Png(Old)), "scan.pdf");

        Comparison c = Comparison.Of(blank, scan);
        Assert.Contains(c.Diagnostics, d => d.Code.Contains("ocr.nothingRecognized", StringComparison.Ordinal));
        Assert.Throws<ComparisonVerificationException>(() => Comparison.Of(blank, scan, CompareSettings.Strict));
    }
}
