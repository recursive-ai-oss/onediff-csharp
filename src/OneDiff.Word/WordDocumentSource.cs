using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Word.Internal;
using RecursiveAI.OneDocument;
using RecursiveAI.OneDocument.Documents;
using RecursiveAI.OneDocument.Formats;
using RecursiveAI.OneDocument.IO;

namespace OneDiff.Word;

/// <summary>
/// Reads a Word document (DOCX, DOCM, DOTX, DOTM) into the flowing compare model through the OneDocument document hub. The input is
/// recognized by its content (a package whose main part is a word-processing document), never by file name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Structure.</b> One <see cref="NodeKind.Section"/> per section of the document, holding that section's headers and footers
/// (one node per own header or footer, with the <c>hf.type</c> attribute) and then its body blocks: paragraphs, list items (the
/// resolved label is the <c>list.label</c> attribute, never part of the text), tables with rows and cells (merges are cell
/// attributes), block content controls. Footnotes, endnotes, comments, fields, images, text boxes and inline content controls
/// are children of the paragraph they sit in, in order; the paragraph text holds U+FFFC where the object is (comments leave no
/// mark, because they are hidden by default). After the sections come one <see cref="NodeKind.Property"/> node per document
/// property (core and custom).
/// </para>
/// <para>
/// <b>Text and formatting.</b> Text is the text the document shows, normalized as in every source (quotes, dashes, spaces; the
/// folds go to the ledger). Run formatting is the resolved formatting (styles and theme applied): bold, italic, underline,
/// strike, font, size in points, colour, highlight, caps, super/subscript, hidden, hyperlink target. Paragraph attributes are the
/// resolved style name, alignment, indents, spacing and keep settings.
/// </para>
/// <para>
/// <b>Not in the model.</b> Parts listed as opaque (custom XML, embedded objects, charts, diagrams, macros, ink) are known by a
/// content hash; drawings that hold neither a picture nor a text box are an image node with <c>image.kind</c> and a hash of
/// their markup. Math, bookmarks, and the contents of text boxes beyond their text are not itemized.
/// </para>
/// <para>
/// <b>Anchors.</b> See docs/word.md. Every node's anchor names its element in the part; every segment names the run it came from and
/// the stretch of the paragraph's text it covers.
/// </para>
/// </remarks>
public sealed class WordDocumentSource : IDocumentSource
{
    /// <summary>The media type written to <see cref="CompareDocument.SourceFormat"/>.</summary>
    public const string MediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private readonly WordSourceOptions options;

    /// <summary>Creates a source with the default <see cref="WordSourceOptions"/>.</summary>
    public WordDocumentSource()
        : this(WordSourceOptions.Default)
    {
    }

    /// <summary>Creates a source.</summary>
    /// <param name="options">The settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">A limit is not positive, or the revision mode is unknown.</exception>
    public WordDocumentSource(WordSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            options.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ArgumentException(ex.Message, nameof(options), ex);
        }

        this.options = options;
    }

    /// <inheritdoc />
    public string Name => "word";

    /// <inheritdoc />
    public bool CanRead(DocumentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Sniff(input.Content) is OfficeFormat.Docx or OfficeFormat.Docm or OfficeFormat.Dotx or OfficeFormat.Dotm or OfficeFormat.EncryptedPackage;
    }

    private static OfficeFormat Sniff(ReadOnlyMemory<byte> content)
    {
        try
        {
            return FormatSniffer.Detect(content);
        }
        catch (OfficeLimitException)
        {
            // A package that trips a limit while being sniffed is a document that Read will reject as too large, not junk.
            return OfficeFormat.Docx;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Damaged bytes that the sniffer cannot judge are not a Word document; Read reports them as such.
            return OfficeFormat.Unknown;
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">
    /// The input is not a readable Word document, is encrypted, exceeds a limit, or holds tracked changes under
    /// <see cref="WordRevisionMode.Refuse"/>; <see cref="WordSourceErrors.FailureOf"/> tells which.
    /// </exception>
    public CompareDocument Read(DocumentInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            OfficeFormat format = Sniff(input.Content);
            if (format == OfficeFormat.EncryptedPackage)
            {
                throw WordSourceErrors.Make(WordSourceFailure.Encrypted, "the document is encrypted");
            }

            if (format is not (OfficeFormat.Docx or OfficeFormat.Docm or OfficeFormat.Dotx or OfficeFormat.Dotm))
            {
                throw WordSourceErrors.Make(WordSourceFailure.Malformed, "the input is not a Word document");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using Document doc = Document.Open(ByteSource.FromMemory(input.Content), new OpenContext(), cancellationToken);
            ApplyRevisionMode(doc);
            cancellationToken.ThrowIfCancellationRequested();

            var builder = new CompareDocumentBuilder(DocumentKind.Flowing).WithMetadata(input.Name, input.Sha256Hex(), MediaType);
            new WordModelBuilder(doc, builder, options, cancellationToken).Build();
            CompareDocument result = builder.Build();
            WordOrigins.Remember(result, input.Content, options.Revisions);
            return result;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or InvalidDataException))
        {
            throw ex switch
            {
                OfficeEncryptedException => WordSourceErrors.Make(WordSourceFailure.Encrypted, "the document is encrypted", ex),
                OfficeLimitException or OutOfMemoryException => WordSourceErrors.Make(WordSourceFailure.TooLarge, "the document exceeds a reader limit", ex),
                _ => WordSourceErrors.Make(WordSourceFailure.Malformed, "the input is not a readable Word document", ex),
            };
        }
    }

    private void ApplyRevisionMode(Document doc)
    {
        if (doc.Revisions.Revisions.Count == 0)
        {
            return;
        }

        switch (options.Revisions)
        {
            case WordRevisionMode.Accepted:
                doc.Revisions.AcceptAll();
                break;
            case WordRevisionMode.AsOriginal:
                doc.Revisions.RejectAll();
                break;
            default:
                throw WordSourceErrors.Make(WordSourceFailure.HasRevisions, $"the document holds {doc.Revisions.Revisions.Count} tracked changes");
        }
    }
}
