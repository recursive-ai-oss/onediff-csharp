using System.Collections.Immutable;
using OneDiff.Documents;
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
/// <param name="Category">A short stable name: <c>properties</c>, <c>comments</c>, <c>header-footer</c>, <c>section</c>, <c>content-control</c> or <c>unplaced</c>.</param>
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
/// The comparison must come from Word documents read by <see cref="WordDocumentSource"/> in this process (the form reopens the bytes
/// the source read). Differences a revision cannot express (document and comment changes, headers or footers present on one side,
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

    /// <summary>Registers the Word source and this form for the <c>.docx</c> extension.</summary>
    public static void Register()
    {
        Redline.Register(new WordDocumentSource());
        Redline.Register(Tracked);
    }

    /// <summary>Writes the comparison and returns what was left out.</summary>
    /// <param name="comparison">A comparison whose built-on document was read by <see cref="WordDocumentSource"/> and whose other document is a Word document too.</param>
    /// <param name="output">Where to write the DOCX; not closed.</param>
    /// <param name="cancellationToken">Observed between nodes.</param>
    /// <returns>The bytes written and the differences not carried.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">A document was not read from a Word file by <see cref="WordDocumentSource"/> in this process.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public DocxRedlineResult Create(Comparison comparison, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(Author);
        bool onNew = BuildOn == WordBuildOn.New;
        if (!WordOrigins.TryGet(comparison.New, out WordOrigin newOrigin) || !WordOrigins.TryGet(comparison.Old, out WordOrigin oldOrigin))
        {
            throw new InvalidOperationException("both documents must be Word files read by WordDocumentSource in this process, so that their bytes are known");
        }

        WordOrigin baseOrigin = onNew ? newOrigin : oldOrigin, donorOrigin = onNew ? oldOrigin : newOrigin;
        using var writer = new TrackedWriter(
            comparison,
            new TrackedOptions(Author, Date, onNew),
            new TrackedSide(onNew ? comparison.New : comparison.Old, baseOrigin.Bytes, baseOrigin.Revisions),
            new TrackedSide(onNew ? comparison.Old : comparison.New, donorOrigin.Bytes, donorOrigin.Revisions),
            cancellationToken);
        byte[] bytes = writer.Run();
        output.Write(bytes, 0, bytes.Length);
        return new DocxRedlineResult(bytes, [.. writer.Notes.Select(n => new WordNotCarried(n.Category, n.Message, n.BaseNodes, n.DonorNodes))]);
    }

    /// <inheritdoc />
    public override void Write(Comparison comparison, Stream output, RedlineStyle style, CancellationToken cancellationToken = default) =>
        Create(comparison, output, cancellationToken);
}
