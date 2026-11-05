using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Pdf.Internal;
using OnePdf;
using OnePdf.Text;

namespace OneDiff.Pdf;

/// <summary>
/// Reads a digital PDF into the flowing compare model. The input is recognized by its <c>%PDF-</c> header (within the first
/// 1024 bytes, as the format allows), never by file name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Structure.</b> One section holds the body in reading order: paragraphs, list items (the marker is the <c>list.label</c>
/// attribute) and, when the text analysis finds a grid, tables of rows and cells. A paragraph that runs on at the end of a column
/// or page is one node. A second section holds the running headers and footers, one header and one footer node per page, with
/// page numbers folded to <c>#</c>.
/// </para>
/// <para>
/// <b>Ledger.</b> The line breaks inside a paragraph, hyphens that end a line inside a word, and page numbers in headers and
/// footers are normalization folds (<see cref="LedgerCategory.LineBreaks"/>, <see cref="LedgerCategory.Hyphenation"/>,
/// <see cref="LedgerCategory.PageNumbers"/>), so the default settings hide them in the ledger and the settings that show them undo them.
/// Quotes, dashes and spaces fold as in every source.
/// </para>
/// <para>
/// <b>Anchors.</b> Each node carries one quadrilateral per line, and its text is covered by segments, one per word or punctuation
/// mark, each with its glyph quadrilateral. Quadrilaterals are in the text extraction's upright page space (points, origin at the
/// lower left of the page as displayed, y up, the page's rotation and crop box applied), with corners in the order lower left,
/// lower right, upper right, upper left of the text, and a zero-based page index.
/// </para>
/// <para>
/// Not in the model: images, vector graphics, annotations, form fields and document properties. Differences there are not itemized.
/// </para>
/// </remarks>
public sealed class PdfDocumentSource : IDocumentSource
{
    private const int HeaderWindow = 1024;
    private readonly PdfSourceOptions options;

    /// <summary>Creates a source with the default <see cref="PdfSourceOptions"/>.</summary>
    public PdfDocumentSource()
        : this(PdfSourceOptions.Default)
    {
    }

    /// <summary>Creates a source.</summary>
    /// <param name="options">The settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">A limit is not positive.</exception>
    public PdfDocumentSource(PdfSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
    }

    /// <summary>The source's media type, written to <see cref="CompareDocument.SourceFormat"/>.</summary>
    public const string MediaType = "application/pdf";

    /// <inheritdoc />
    public string Name => "pdf";

    /// <inheritdoc />
    public bool CanRead(DocumentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ReadOnlySpan<byte> b = input.Content.Span;
        return b[..Math.Min(b.Length, HeaderWindow)].IndexOf("%PDF-"u8) >= 0;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">The input is not a readable PDF, is encrypted and needs a password, or exceeds a limit.</exception>
    public CompareDocument Read(DocumentInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!CanRead(input))
        {
            throw PdfSourceErrors.Make(PdfSourceFailure.Malformed, "The input has no PDF header.");
        }

        try
        {
            using PdfDocument pdf = PdfDocument.Open(input.Content, new PdfOpenOptions { Password = options.Password });
            if (pdf.PageCount > options.MaxPages)
            {
                throw PdfSourceErrors.Make(PdfSourceFailure.TooLarge, $"The document has {pdf.PageCount} pages; the limit is {options.MaxPages}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PdfPageText> pages = pdf.ExtractText(
                new PdfTextOptions { RetainGlyphs = true, DetectHeadersAndFooters = true },
                cancellationToken);
            if (pages.Count == 0)
            {
                throw PdfSourceErrors.Make(PdfSourceFailure.Malformed, "No pages could be read from this PDF; the file is damaged or empty.");
            }

            // Pages without any text are a scan, not an empty document: refuse instead of comparing nothing (ISO 32000-2 puts a scan's
            // page content in an image, which text extraction never sees).
            if (pages.All(static p => string.IsNullOrWhiteSpace(p.Text)))
            {
                throw PdfSourceErrors.Make(
                    PdfSourceFailure.NoTextLayer,
                    "This PDF has no text layer, so it is probably a scan. Reference RecursiveAI.OneDiff.Ocr and call OcrRedline.Register() to read it by OCR.");
            }

            var labels = new string?[pages.Count];
            for (int i = 0; i < labels.Length; i++)
            {
                labels[i] = pdf.Pages[i].Label;
            }

            var builder = new CompareDocumentBuilder(DocumentKind.Flowing).WithMetadata(input.Name, input.Sha256Hex(), MediaType);
            AddReaderDiagnostics(builder, pdf);
            new PdfModelBuilder(options, builder, cancellationToken).Build(pages, labels);
            CompareDocument result = builder.WithOrigin(new DocumentOrigin(input.Content, options.Password, input.Sha256Hex())).Build();
            return result;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or InvalidDataException or OutOfMemoryException))
        {
            throw ex switch
            {
                PdfPasswordException or PdfEncryptionException => PdfSourceErrors.Make(PdfSourceFailure.Encrypted, "The PDF is encrypted and the password is missing or wrong.", ex),
                PdfLimitException => PdfSourceErrors.Make(PdfSourceFailure.TooLarge, "The PDF exceeds a reader limit.", ex),
                _ => PdfSourceErrors.Make(PdfSourceFailure.Malformed, "The input is not a readable PDF.", ex),
            };
        }
    }

    // What OnePDF repaired or could not recover while reading becomes a warning on the document, so a damaged file is reported on
    // the comparison instead of being compared as if it were whole. Repairs alone are informational; dropped content is a warning.
    internal static void AddReaderDiagnostics(CompareDocumentBuilder builder, PdfDocument pdf)
    {
        int repaired = 0, lost = 0;
        string? firstLost = null;
        foreach (PdfDiagnostic d in pdf.Diagnostics)
        {
            if (d.Severity == PdfSeverity.Error)
            {
                lost++;
                firstLost ??= d.Message;
            }
            else if (d.Severity == PdfSeverity.Warning)
            {
                repaired++;
            }
        }

        if (lost > 0)
        {
            builder.AddWarning(DiagnosticSeverity.Warning, "pdf.unrecovered", $"Content of this PDF could not be recovered ({lost} problem{(lost == 1 ? "" : "s")}; first: {firstLost}). The comparison covers only what was read.");
        }
        else if (repaired > 0)
        {
            builder.AddWarning(DiagnosticSeverity.Info, "pdf.repaired", $"This PDF deviates from the specification and was repaired while reading ({repaired} repair{(repaired == 1 ? "" : "s")}).");
        }
    }
}
