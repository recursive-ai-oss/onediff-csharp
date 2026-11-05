using System.Security.Cryptography;
using OneDiff.Pdf;
using OneDiff.Pdf.Internal;
using OneImage;
using OnePdf;
using OnePdf.Text;
using OneOcr;

namespace OneDiff.Ocr.Internal;

/// <summary>Deciding whether a PDF is a scan, and wrapping image inputs into PDF pages so the markup writer has pages to mark.</summary>
internal static class ScanPdf
{
    /// <summary>How many pages are examined for a text layer. Pages beyond it are not read: a scan is a scan on every page.</summary>
    private const int SampledPages = 8;

    /// <summary>
    /// A PDF is a scan when none of the pages examined has a text layer of at least <see cref="OcrSourceOptions.MinTextLayerCharacters"/>
    /// non-space characters. The pages examined are all of them up to eight, otherwise eight spread evenly from the first to the last.
    /// A scan that carries an invisible text layer from an earlier OCR run therefore counts as digital: its text is the file's own.
    /// A file that cannot be opened is not a scan (the PDF source reports why).
    /// </summary>
    public static bool IsScan(ReadOnlyMemory<byte> content, OcrSourceOptions options)
    {
        try
        {
            using PdfDocument pdf = PdfDocument.Open(content, new PdfOpenOptions { Password = options.Password });
            int n = pdf.PageCount;
            if (n == 0)
            {
                return false;
            }

            // Pages with no content stream have nothing to recognize: leave the file to the PDF source, which refuses it as damaged.
            if (!PdfPageContent.AnyPageHasContent(pdf))
            {
                return false;
            }

            int count = Math.Min(n, SampledPages);
            var text = new PdfTextOptions { RetainGlyphs = false, DetectHeadersAndFooters = false };
            for (int i = 0; i < count; i++)
            {
                int index = count == n ? i : (int)((long)i * (n - 1) / (count - 1));
                PdfPageText page = pdf.Pages[index].ExtractText(text);
                int visible = 0;
                foreach (char c in page.Text)
                {
                    if (!char.IsWhiteSpace(c) && ++visible >= options.MinTextLayerCharacters)
                    {
                        return false;
                    }
                }

                if (options.MinTextLayerCharacters == 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is PdfException or InvalidDataException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a PDF with one page per image frame, each page the size of its frame at the given resolution and showing the frame
    /// edge to edge. PNG and JPEG inputs are embedded as they are; other formats are decoded and embedded as raw samples. The file
    /// is reproducible: its identifier is derived from the input.
    /// </summary>
    /// <param name="kind">The format of <paramref name="content"/>.</param>
    /// <param name="content">The image file.</param>
    /// <param name="pages">The recognized pages, one per frame: pixel size and resolution.</param>
    public static byte[] Wrap(InputKind kind, ReadOnlyMemory<byte> content, IReadOnlyList<OcrPage> pages)
    {
        using PdfDocument doc = PdfDocument.CreateEmpty();
        if (kind is InputKind.Png or InputKind.Jpeg)
        {
            PdfCreatedImage image = kind == InputKind.Png ? doc.AddPngImage(content) : doc.AddJpegImage(content);
            AddPage(doc, 0, pages[0].Width, pages[0].Height, pages[0].UnitsPerInch, image);
        }
        else
        {
            Image decoded = Image.Load(content);
            for (int i = 0; i < decoded.Frames.Count && i < pages.Count; i++)
            {
                ImageFrame frame = decoded.Frames[i];
                (byte[] samples, PdfImageColor color) = Samples(frame);
                PdfCreatedImage image = doc.AddRawImage(frame.Width, frame.Height, color, samples);
                AddPage(doc, i, frame.Width, frame.Height, pages[i].UnitsPerInch, image);
            }
        }

        using var output = new MemoryStream();
        doc.Save(output, new PdfSaveOptions { DocumentId = SHA256.HashData(content.Span)[..16] });
        return output.ToArray();
    }

    private static void AddPage(PdfDocument doc, int index, double pixelsWide, double pixelsHigh, double dpi, PdfCreatedImage image)
    {
        double w = pixelsWide * 72.0 / dpi, h = pixelsHigh * 72.0 / dpi;
        PdfPage page = doc.InsertBlankPage(index, w, h);
        using PdfPageContentBuilder b = page.BeginContent();
        b.DrawImage(image, 0, 0, w, h);
        b.Commit();
    }

    private static (byte[] Samples, PdfImageColor Color) Samples(ImageFrame frame)
    {
        Rgba32[] pixels = frame.ToArray<Rgba32>();
        bool gray = true;
        foreach (Rgba32 p in pixels)
        {
            if (p.R != p.G || p.G != p.B)
            {
                gray = false;
                break;
            }
        }

        if (gray)
        {
            var g = new byte[pixels.Length];
            for (int i = 0; i < g.Length; i++)
            {
                g[i] = Flatten(pixels[i].R, pixels[i].A);
            }

            return (g, PdfImageColor.Gray);
        }

        var rgb = new byte[pixels.Length * 3];
        for (int i = 0; i < pixels.Length; i++)
        {
            rgb[(3 * i)] = Flatten(pixels[i].R, pixels[i].A);
            rgb[(3 * i) + 1] = Flatten(pixels[i].G, pixels[i].A);
            rgb[(3 * i) + 2] = Flatten(pixels[i].B, pixels[i].A);
        }

        return (rgb, PdfImageColor.Rgb);
    }

    /// <summary>A sample over a white page: transparent pixels read as paper.</summary>
    private static byte Flatten(byte sample, byte alpha) => alpha == 255 ? sample : (byte)(((sample * alpha) + (255 * (255 - alpha)) + 127) / 255);
}
