using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Pdf;

namespace OneDiff.Ocr.Tests;

// Reads scans, which rasterizes through OneImage.Pdf (not available under the browser runtime).
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
}
