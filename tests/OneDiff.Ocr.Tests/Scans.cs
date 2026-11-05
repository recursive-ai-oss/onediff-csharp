using Image = OneImage.Image;
using OneImage;
using OneImage.Pdf;
using OnePdf;
using OnePdf.Layout;

namespace OneDiff.Ocr.Tests;

/// <summary>Makes the scans the tests read: pages typeset with OnePDF's layout engine, drawn to pixels, and optionally wrapped in a PDF.</summary>
internal static class Scans
{
    /// <summary>A digital PDF of the paragraphs at 11 pt on one page of the given height.</summary>
    public static byte[] DigitalPdf(string[] paragraphs, double height = 460, double size = 11) =>
        LayoutDocument.Compose(d =>
        {
            d.Title("fixture");
            d.Page(page =>
            {
                page.Size(595, height);
                page.Margin(48);
                page.Content().Column(col =>
                {
                    col.Spacing(26);
                    foreach (string p in paragraphs)
                    {
                        col.Item().Text(p).FontSize(size);
                    }
                });
            });
        }).ToBytes(new PdfDocumentWriterOptions { DocumentId = new byte[16] });

    /// <summary>Draws every page of a PDF at the resolution.</summary>
    public static Image[] Render(byte[] pdf, double dpi)
    {
        var load = new LoadOptions { Configuration = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = dpi })) };
        Image doc = Image.Load(pdf, load);
        return [.. doc.Frames.Select(f => f.ToImage())];
    }

    /// <summary>A PNG of the first page of the paragraphs, drawn at the resolution.</summary>
    public static byte[] Png(string[] paragraphs, double dpi = 300)
    {
        Image page = Render(DigitalPdf(paragraphs), dpi)[0];
        using var ms = new MemoryStream();
        page.Save(ms, PngFormat.Instance);
        return ms.ToArray();
    }

    /// <summary>An image-only PDF: one page that shows the PNG edge to edge at the resolution.</summary>
    public static byte[] ScanPdf(byte[] png, double dpi = 300)
    {
        Image decoded = Image.Load(png);
        double w = decoded.Frames[0].Width * 72.0 / dpi, h = decoded.Frames[0].Height * 72.0 / dpi;
        using PdfDocument doc = PdfDocument.CreateEmpty();
        PdfCreatedImage image = doc.AddPngImage(png);
        PdfPage page = doc.InsertBlankPage(0, w, h);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.DrawImage(image, 0, 0, w, h);
            b.Commit();
        }

        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { DocumentId = new byte[16] });
        return ms.ToArray();
    }
}
