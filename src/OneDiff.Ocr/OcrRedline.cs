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
    /// <summary>The priority the scan source is registered with: above the default 0 of the PDF source.</summary>
    public const int ScanPriority = 100;

    /// <summary>
    /// Registers the PDF source, the scan source and the on-page markup form for <c>.pdf</c> files, process-wide. After this,
    /// <c>Redline.Create("scan-v1.pdf", "scan-v2.png", "marked.pdf")</c> sniffs each input by content: a scanned PDF or an image goes
    /// to <see cref="OcrDocumentSource"/>, any other PDF to <see cref="PdfDocumentSource"/>; and a <c>.pdf</c> output marks the new
    /// input's pages. Safe to call more than once, in any order with <see cref="PdfRedline.Register"/>: the scan source is registered
    /// with a higher priority than the PDF source, so it is asked first whichever was registered last.
    /// </summary>
    public static void Register()
    {
        PdfRedline.Register();
        Redline.Register(new OcrDocumentSource(), ScanPriority);
    }
}
