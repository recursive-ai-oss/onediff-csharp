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
            throw PdfSourceErrors.Make(PdfSourceFailure.Malformed, "the input has no PDF header");
        }

        try
        {
            using PdfDocument pdf = PdfDocument.Open(input.Content, new PdfOpenOptions { Password = options.Password });
            if (pdf.PageCount > options.MaxPages)
            {
                throw PdfSourceErrors.Make(PdfSourceFailure.TooLarge, $"the document has {pdf.PageCount} pages; the limit is {options.MaxPages}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PdfPageText> pages = pdf.ExtractText(
                new PdfTextOptions { RetainGlyphs = true, DetectHeadersAndFooters = true, MaxDegreeOfParallelism = 1 },
                cancellationToken);
            var labels = new string?[pages.Count];
            for (int i = 0; i < labels.Length; i++)
            {
                labels[i] = pdf.Pages[i].Label;
            }

            var builder = new CompareDocumentBuilder(DocumentKind.Flowing).WithMetadata(input.Name, input.Sha256Hex(), MediaType);
            new PdfModelBuilder(options, builder, cancellationToken).Build(pages, labels);
            return builder.Build();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or InvalidDataException or OutOfMemoryException))
        {
            throw ex switch
            {
                PdfPasswordException or PdfEncryptionException => PdfSourceErrors.Make(PdfSourceFailure.Encrypted, "the PDF is encrypted and the password is missing or wrong", ex),
                PdfLimitException => PdfSourceErrors.Make(PdfSourceFailure.TooLarge, "the PDF exceeds a reader limit", ex),
                _ => PdfSourceErrors.Make(PdfSourceFailure.Malformed, "the input is not a readable PDF", ex),
            };
        }
    }
}
