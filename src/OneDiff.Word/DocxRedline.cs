using System.Collections.Immutable;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Word.Internal;

namespace OneDiff.Word;

/// <summary>Which document a tracked-changes DOCX is built on.</summary>
public enum WordBuildOn
{
    /// <summary>The new document: its package, styles and formatting are kept, the old content is added as deletions. Accept-all gives the new document exactly.</summary>
    New = 0,

    /// <summary>The old document: its package is kept, the new content is added as insertions. Reject-all gives the old document exactly.</summary>
    Old = 1,
}

/// <summary>A difference that a tracked-changes DOCX cannot carry as a revision, with the model nodes it concerns.</summary>
/// <param name="Category">A short stable name: <c>properties</c>, <c>comments</c>, <c>header-footer</c>, <c>section</c>, <c>content-control</c>, <c>paragraph-mark-accept</c>, <c>paragraph-mark-reject</c>, <c>unplaced</c> or <c>import</c>.</param>
/// <param name="Message">What could not be carried and why.</param>
/// <param name="BaseNodes">Node ids in the document the DOCX was built on.</param>
/// <param name="OtherNodes">Node ids in the other document.</param>
public sealed record WordNotCarried(string Category, string Message, ImmutableArray<int> BaseNodes, ImmutableArray<int> OtherNodes);

/// <summary>A written tracked-changes document and what it left out.</summary>
/// <param name="Bytes">The DOCX package.</param>
/// <param name="NotCarried">The differences it could not carry as revisions; each is also in the comparison (as a change or a ledger entry).</param>
public sealed record DocxRedlineResult(byte[] Bytes, ImmutableArray<WordNotCarried> NotCarried);

/// <summary>
/// The output form that writes a comparison as a DOCX with real tracked changes (ECMA-376 Part 1 §17.13.5): insertions and deletions
/// of text, paragraphs, table rows and cells, moves with range markers, and property changes. The document is built on the new
/// document by default, so that accepting all revisions gives the new document and rejecting all gives the old one.
/// </summary>
/// <remarks>
/// A side read by <see cref="WordDocumentSource"/> in this process is built on as it is (the form reopens the bytes the source read).
/// Any other flowing document (a PDF, a scan, text) is first reconstructed as a DOCX from its compare model: paragraphs, list items with
/// their resolved labels, tables, one header and one footer, and the character formatting the model carries; the package is labelled as
/// reconstructed in its core properties and by a note in its header (<see cref="ReconstructionNote"/>). With <see cref="WordBuildOn.New"/>
/// (the default) the output is built on the new document, reconstructed when it is not a Word file, and the old document is the donor of
/// the deleted content; <see cref="WordBuildOn.Old"/> builds on the old one. Differences a revision cannot express (document and comment changes, headers or footers present on one side,
/// content-control wrappers, section breaks) are listed in <see cref="DocxRedlineResult.NotCarried"/> by <see cref="Create"/>.
/// Revision ids are numbered in document order, and no date is written unless <see cref="Date"/> is set, so equal inputs give equal bytes.
/// </remarks>
public sealed class DocxRedline : RedlineForm
{
    /// <summary>Creates the form with the default author and no date.</summary>
    public DocxRedline() : base("docx", [".docx"])
    {
    }

    /// <summary>The form with default settings: built on the new document, author <c>OneDiff</c>, no date.</summary>
    public static DocxRedline Tracked { get; } = new();

    /// <summary>The author written on every revision. Default <c>OneDiff</c>.</summary>
    public string Author { get; init; } = "OneDiff";

    /// <summary>The date written on every revision; null (the default) writes none.</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>Which document the output is built on. Default <see cref="WordBuildOn.New"/>.</summary>
    public WordBuildOn BuildOn { get; init; } = WordBuildOn.New;

    /// <summary>
    /// Whether a document that is reconstructed (it was not read from a Word file) carries a visible note saying so, as the last paragraph of
    /// its header. Default true. The core properties say it either way. Word files read by <see cref="WordDocumentSource"/> are never touched.
    /// </summary>
    public bool ReconstructionNote { get; init; } = true;

    /// <summary>Registers the Word source and this form for the <c>.docx</c> extension.</summary>
    public static void Register()
    {
        Redline.Register(new WordDocumentSource());
        Redline.Register(Tracked);
    }

    /// <summary>Writes the comparison and returns what was left out.</summary>
    /// <param name="comparison">A comparison of two flowing documents; each may be a Word document read by <see cref="WordDocumentSource"/> or any other flowing document.</param>
    /// <param name="output">Where to write the DOCX; not closed.</param>
    /// <param name="cancellationToken">Observed between nodes.</param>
    /// <returns>The bytes written and the differences not carried.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="NotSupportedException">A document that is not a Word file holds nodes a reconstruction cannot write (children of paragraphs, notes, comments, text boxes) or is not of the flowing kind.</exception>
    /// <exception cref="InvalidOperationException">A reconstructed package does not read back as the model it was built from (a defect).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public DocxRedlineResult Create(Comparison comparison, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(Author);
        bool onNew = BuildOn == WordBuildOn.New;
        CancellationToken ct = cancellationToken;
        (CompareDocument newModel, ReadOnlyMemory<byte> newBytes, WordRevisionMode newMode) = Prepare(comparison.New, ct);
        (CompareDocument oldModel, ReadOnlyMemory<byte> oldBytes, WordRevisionMode oldMode) = Prepare(comparison.Old, ct);
        using var writer = new TrackedWriter(
            comparison.Alignment,
            comparison.Changes,
            new TrackedOptions(Author, Date, onNew),
            onNew ? new TrackedSide(newModel, newBytes, newMode) : new TrackedSide(oldModel, oldBytes, oldMode),
            onNew ? new TrackedSide(oldModel, oldBytes, oldMode) : new TrackedSide(newModel, newBytes, newMode),
            ct);
        byte[] bytes = writer.Run();
        output.Write(bytes, 0, bytes.Length);
        return new DocxRedlineResult(bytes, [.. writer.Notes.Select(n => new WordNotCarried(n.Category, n.Message, n.BaseNodes, n.DonorNodes))]);
    }

    /// <summary>
    /// The document and package a side is written on: a Word document as read, any other flowing document (a PDF, a scan, plain text)
    /// reconstructed as a DOCX and re-anchored (see docs/word.md).
    /// </summary>
    private (CompareDocument Model, ReadOnlyMemory<byte> Bytes, WordRevisionMode Mode) Prepare(CompareDocument document, CancellationToken cancellationToken)
    {
        if (WordOrigins.TryGet(document, out WordOrigin origin))
        {
            return (document, origin.Bytes, origin.Revisions);
        }

        ReconstructedDocx rebuilt = Reconstruction.Build(document, document.SourceFormat.Length > 0 ? document.SourceFormat : "a non-Word document", ReconstructionNote, cancellationToken);
        return (rebuilt.Model, rebuilt.Bytes, WordRevisionMode.Accepted);
    }

    /// <inheritdoc />
    public override void Write(Comparison comparison, Stream output, RedlineStyle style, CancellationToken cancellationToken = default) =>
        Create(comparison, output, cancellationToken);
}
