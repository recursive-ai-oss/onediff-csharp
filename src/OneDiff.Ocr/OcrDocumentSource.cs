using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Ocr.Internal;
using OneDiff.Pdf;
using OneDiff.Pdf.Internal;
using OneImage;
using OneOcr;
using OneOcr.Pdf;

namespace OneDiff.Ocr;

/// <summary>
/// Reads a scan into the flowing compare model: a PDF whose pages carry no text of their own, or an image (PNG, JPEG or TIFF). The
/// pages are read by the OCR engine of the OCR package, locally and deterministically, and laid out the way the PDF source lays out a
/// text layer: paragraphs, list items, tables, and running headers and footers in a section of their own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which inputs.</b> Inputs are recognized by their first bytes, never by file name. An image is a PNG (signature), a JPEG (SOI
/// marker) or a TIFF (byte order mark and 42). A PDF (<c>%PDF-</c> within the first 1024 bytes) is a scan when none of the pages
/// examined has a text layer of <see cref="OcrSourceOptions.MinTextLayerCharacters"/> non-space characters; the pages examined are
/// all of them up to eight, otherwise eight spread evenly from the first to the last. Any other PDF is left to the PDF source, which
/// reads its text layer. A PDF that mixes pages of both kinds counts as a scan when its sampled pages have no text, and then pages
/// that do have text are read from their text layer (confidence 1) and the rest by OCR.
/// </para>
/// <para>
/// <b>Anchors.</b> Each node and each word-sized segment carries an <see cref="AnchorKind.OcrWords"/> anchor: the quadrilaterals in
/// upright page space (points, origin at the lower left of the page as displayed, y up; corners lower left, lower right, upper right,
/// upper left of the text), a zero-based page index, and the lowest recognition confidence of the words it covers, in [0, 1]. A
/// space between two words on one line has its own segment, with confidence 1. An image page is as large in points as the image is
/// at its resolution (declared by the file, set by <see cref="OcrSourceOptions.ImageDpi"/>, or else estimated by the engine).
/// </para>
/// <para>
/// <b>Marking.</b> <see cref="PdfRedline"/> marks the pages of the new input. A scanned PDF is marked in its own bytes; an image is
/// marked on a PDF with one page per image frame that the source makes from it (the image is embedded as it is).
/// </para>
/// <para>
/// <b>Guarantee.</b> The compare model is the recognized text, so completeness holds relative to the recognition: a difference that is
/// only a recognition error is a change like any other, and the changes whose words were read with low confidence carry
/// <see cref="Change.PossibleOcrError"/>. The recognized words are not hidden or corrected. What the OCR engine does not read (pictures,
/// pages it finds empty) is not in the model.
/// </para>
/// <para><see cref="OcrRedline.Register"/> registers this source with a priority above the PDF source's, so the order of registration does not matter.</para>
/// </remarks>
public sealed class OcrDocumentSource : IDocumentSource
{
    private static readonly Lazy<OcrEngine> BundledEngine = new(static () => OcrEngine.LoadBundled(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly OcrSourceOptions options;

    /// <summary>Creates a source with the default <see cref="OcrSourceOptions"/>.</summary>
    public OcrDocumentSource()
        : this(OcrSourceOptions.Default)
    {
    }

    /// <summary>Creates a source.</summary>
    /// <param name="options">The settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">A limit is not positive or a resolution is out of range.</exception>
    public OcrDocumentSource(OcrSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
    }

    /// <summary>The media type written to <see cref="CompareDocument.SourceFormat"/> for a scanned PDF; an image keeps its own (<c>image/png</c>, <c>image/jpeg</c>, <c>image/tiff</c>).</summary>
    public const string PdfMediaType = "application/pdf";

    /// <inheritdoc />
    public string Name => "ocr";

    /// <inheritdoc />
    public bool CanRead(DocumentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        InputKind kind = InputKinds.Of(input.Content.Span);
        return kind switch
        {
            InputKind.Unknown => false,
            InputKind.Pdf => ScanPdf.IsScan(input.Content, options),
            _ => true,
        };
    }

    /// <summary>
    /// Whether a PDF is read as a scan under the settings: see the remarks of <see cref="OcrDocumentSource"/>. False for a file that
    /// is not a PDF or cannot be opened.
    /// </summary>
    /// <param name="pdf">The file's bytes.</param>
    /// <param name="options">The settings; null for the defaults.</param>
    public static bool IsScannedPdf(ReadOnlyMemory<byte> pdf, OcrSourceOptions? options = null)
    {
        OcrSourceOptions o = options ?? OcrSourceOptions.Default;
        return InputKinds.Of(pdf.Span) == InputKind.Pdf && ScanPdf.IsScan(pdf, o);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">The input is not a scan this source reads, cannot be decoded, is encrypted and needs a password, or exceeds a limit (<see cref="PdfSourceErrors.FailureOf"/> says which of the three kinds).</exception>
    /// <exception cref="OcrModelException">The OCR engine's models are missing or do not match their pins.</exception>
    public CompareDocument Read(DocumentInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        InputKind kind = InputKinds.Of(input.Content.Span);
        if (kind == InputKind.Unknown)
        {
            throw PdfSourceErrors.Make(PdfSourceFailure.Malformed, "The input is neither a PDF nor a PNG, JPEG or TIFF image.");
        }

        if (input.Content.Length > options.MaxInputBytes)
        {
            throw PdfSourceErrors.Make(PdfSourceFailure.TooLarge, $"The input is {input.Content.Length} bytes; the limit is {options.MaxInputBytes}");
        }

        try
        {
            OcrEngine engine = options.Engine ?? BundledEngine.Value;
            IReadOnlyList<OcrPage> pages;
            byte[]? wrapped = null;
            if (kind == InputKind.Pdf)
            {
                pages = PdfOcr.Read(
                    input.Content,
                    engine,
                    new PdfOcrOptions
                    {
                        RenderDpi = options.RenderDpi,
                        Password = options.Password,
                        IncludeChars = false,
                        MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                        MaxFileBytes = options.MaxInputBytes,
                    },
                    cancellationToken);
            }
            else
            {
                Image image = Image.Load(input.Content, cancellationToken: cancellationToken);
                if (image.Frames.Count > options.MaxPages)
                {
                    throw PdfSourceErrors.Make(PdfSourceFailure.TooLarge, $"The image has {image.Frames.Count} frames; the limit is {options.MaxPages}");
                }

                pages = engine.OcrPages(image, new OcrOptions { Dpi = options.ImageDpi, IncludeChars = false, MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, cancellationToken);
                wrapped = ScanPdf.Wrap(kind, input.Content, pages);
            }

            if (pages.Count > options.MaxPages)
            {
                throw PdfSourceErrors.Make(PdfSourceFailure.TooLarge, $"The document has {pages.Count} pages; the limit is {options.MaxPages}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var data = new PageData[pages.Count];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = PageConversion.Convert(pages[i]);
            }

            var builder = new CompareDocumentBuilder(DocumentKind.Flowing).WithMetadata(input.Name, input.Sha256Hex(), MediaTypeOf(kind));
            new OcrModelBuilder(options, builder, cancellationToken).Build(PageFurniture.Detect(data));
            if (data.All(static p => p.Blocks.Count == 0))
            {
                // The pages rendered but the engine read no word at all (blank, too faint, or not text). Comparing nothing must not pass
                // as a result in silence; a strict comparison throws on this warning.
                builder.AddWarning(DiagnosticSeverity.Warning, "ocr.nothingRecognized", "The OCR engine recognized no words on any page, so this input is compared as an empty document. Check that the pages show text.");
            }

            return builder
                .WithOrigin(wrapped is null ? new DocumentOrigin(input.Content, options.Password, input.Sha256Hex()) : new DocumentOrigin(wrapped))
                .Build();
        }
        catch (Exception ex) when (ex is OcrFormatException or OcrLimitException or ImageException)
        {
            throw PdfSourceErrors.Make(
                ex is OcrLimitException || ex is ImageLimitException ? PdfSourceFailure.TooLarge : PdfSourceFailure.Malformed,
                "the input cannot be read as a scan: " + ex.Message,
                ex);
        }
    }

    private static string MediaTypeOf(InputKind kind) => kind switch
    {
        InputKind.Png => "image/png",
        InputKind.Jpeg => "image/jpeg",
        InputKind.Tiff => "image/tiff",
        _ => PdfMediaType,
    };
}
