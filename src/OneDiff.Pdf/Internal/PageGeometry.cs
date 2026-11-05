using OnePdf;

namespace OneDiff.Pdf.Internal;

/// <summary>
/// Maps points between the upright page space the PDF source reports (points, origin at the lower left of the page as displayed,
/// y up, rotation and crop box applied) and default user space, where annotations are written (ISO 32000-2 8.3.2.3 and 12.5.2,
/// <c>/Rotate</c> in 7.7.3.3: the page is displayed rotated clockwise by that angle).
/// </summary>
internal readonly struct PageGeometry
{
    private readonly PdfRectangle box;
    private readonly int rotate;

    private PageGeometry(PdfRectangle box, int rotate)
    {
        this.box = box;
        this.rotate = rotate;
    }

    /// <summary>The crop box (clipped to the media box) and rotation of a page: the area the extraction measures from.</summary>
    public static PageGeometry Of(PdfPage page)
    {
        PdfRectangle media = page.MediaBox;
        PdfRectangle crop = page.CropBox.Intersect(media) ?? media;
        return new PageGeometry(crop, page.Rotate);
    }

    /// <summary>Creates a geometry from a box and a clockwise rotation (0, 90, 180 or 270).</summary>
    public static PageGeometry Of(PdfRectangle box, int rotate) => new(box, rotate);

    public double UprightWidth => rotate is 90 or 270 ? box.Height : box.Width;

    public double UprightHeight => rotate is 90 or 270 ? box.Width : box.Height;

    /// <summary>Upright page space to default user space.</summary>
    public (double X, double Y) ToUser(double x, double y)
    {
        double w = box.Width, h = box.Height;
        (double a, double b) = rotate switch
        {
            90 => (w - y, x),
            180 => (w - x, h - y),
            270 => (y, h - x),
            _ => (x, y),
        };
        return (box.Left + a, box.Bottom + b);
    }

    /// <summary>Default user space to upright page space.</summary>
    public (double X, double Y) ToUpright(double ux, double uy)
    {
        double a = ux - box.Left, b = uy - box.Bottom;
        double w = box.Width, h = box.Height;
        return rotate switch
        {
            90 => (b, w - a),
            180 => (w - a, h - b),
            270 => (h - b, a),
            _ => (a, b),
        };
    }
}
