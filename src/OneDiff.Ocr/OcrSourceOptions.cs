using OneOcr;

namespace OneDiff.Ocr;

/// <summary>
/// Settings of <see cref="OcrDocumentSource"/>. Immutable; derive variants with <c>with</c>. The defaults read an ordinary scan at
/// 300 dpi with the OCR engine that ships with the OCR package, and keep the work bounded.
/// </summary>
public sealed record OcrSourceOptions
{
    /// <summary>The settings used when none are given.</summary>
    public static OcrSourceOptions Default { get; } = new();

    /// <summary>
    /// The recognizer, or null (the default) for the engine loaded from the OCR package's bundled models, which is created on first
    /// use and shared by the process. An engine is immutable and thread-safe.
    /// </summary>
    public OcrEngine? Engine { get; init; }

    /// <summary>
    /// The resolution of an image input in dots per inch, when the file declares none or declares it wrongly. Null (the default)
    /// takes the resolution the file declares and otherwise an estimate from the size of the text. Page sizes in the anchors follow it.
    /// </summary>
    public double? ImageDpi { get; init; }

    /// <summary>The resolution a scanned PDF page is drawn at before it is read. Default 300; the range is 72 to 600.</summary>
    public double RenderDpi { get; init; } = 300;

    /// <summary>The password of an encrypted PDF, or null.</summary>
    public string? Password { get; init; }

    /// <summary>The most pages an input may have; more is rejected with <see cref="OneDiff.Pdf.PdfSourceFailure.TooLarge"/>. Default 2,000.</summary>
    public int MaxPages { get; init; } = 2_000;

    /// <summary>The most characters of text a document may hold in total; more is rejected. Default 64 million.</summary>
    public long MaxCharacters { get; init; } = 64_000_000;

    /// <summary>The largest input in bytes; more is rejected with <see cref="OneDiff.Pdf.PdfSourceFailure.TooLarge"/>. Default 256 MiB.</summary>
    public long MaxInputBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// How many non-space characters a page's own text layer needs to count as text. A PDF is read as a scan when none of the pages
    /// examined (see <see cref="OcrDocumentSource"/>) has that many. Default 20.
    /// </summary>
    public int MinTextLayerCharacters { get; init; } = 20;

    /// <summary>
    /// Whether blocks the layout analysis recognized as tables become table, row and cell nodes. Default false: the recognizer finds
    /// the same table on two scans of one page only some of the time, and a table on one side and running text on the other is
    /// a difference of reading, not of the document. Without it the cell text still reads in order and every difference in it is shown.
    /// </summary>
    public bool ReadTables { get; init; }

    /// <summary>Whether a line that starts with a bullet or a number becomes a list item whose label is an attribute. Default true.</summary>
    public bool ReadListItems { get; init; } = true;

    /// <summary>Whether a paragraph that runs on at the end of a column or a page is joined to its continuation. Default true.</summary>
    public bool JoinAcrossColumnsAndPages { get; init; } = true;

    /// <summary>The most threads the recognizer may use; 0 (the default) lets it choose. The result is the same at every setting.</summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <summary>Checks the numeric limits.</summary>
    /// <exception cref="ArgumentException">A limit is not positive or a resolution is out of range.</exception>
    public void Validate()
    {
        if (MaxPages <= 0 || MaxCharacters <= 0 || MaxInputBytes <= 0 || MinTextLayerCharacters < 0 || MaxDegreeOfParallelism < 0)
        {
            throw new ArgumentException("The page, character and byte limits must be positive.");
        }

        if (!(RenderDpi >= 72 && RenderDpi <= 600) || (ImageDpi is double d && !(d >= 10 && d <= 4800)))
        {
            throw new ArgumentException("A resolution is out of range.");
        }
    }
}
