using OnePdf;

namespace OneDiff.Pdf.Internal;

/// <summary>Tells a page that draws something (text, an image, a path) from one with no content stream at all.</summary>
internal static class PdfPageContent
{
    /// <summary>
    /// True when at least one page has a content stream (ISO 32000-2 7.7.3.3, <c>/Contents</c>). A scan page has one that draws its
    /// image; a truncated or damaged file leaves pages with none, and there is nothing for OCR to read either.
    /// </summary>
    public static bool AnyPageHasContent(PdfDocument pdf)
    {
        for (int i = 0; i < pdf.PageCount; i++)
        {
            if (pdf.Pages[i].ContentStreams.Count > 0)
            {
                return true;
            }
        }

        return false;
    }
}
