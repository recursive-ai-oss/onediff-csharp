using OneDiff.Documents;
using OneDiff.Pdf;

namespace OneDiff.Ocr;

/// <summary>
/// Registration of the scan source and the output form that marks scans. A scan is marked on its own pages with real PDF annotations,
/// exactly as a digital PDF is: <see cref="PdfRedline.OnPages"/> is the form, and this class only makes sure the source that
/// produces scan documents is known to <see cref="Redline"/> and to the content sniffing.
/// </summary>
public static class OcrRedline
{
    /// <summary>
    /// Registers the PDF source, the scan source and the on-page markup form for <c>.pdf</c> files, process-wide. After this,
    /// <c>Redline.Create("scan-v1.pdf", "scan-v2.png", "marked.pdf")</c> sniffs each input by content: a scanned PDF or an image goes
    /// to <see cref="OcrDocumentSource"/>, any other PDF to <see cref="PdfDocumentSource"/>; and a <c>.pdf</c> output marks the new
    /// input's pages. Safe to call more than once. The scan source is registered after the PDF source, which is the order that makes
    /// it asked first; if <see cref="PdfRedline.Register"/> is called later, call this again.
    /// </summary>
    public static void Register()
    {
        PdfRedline.Register();
        Redline.Register(new OcrDocumentSource());
    }
}
