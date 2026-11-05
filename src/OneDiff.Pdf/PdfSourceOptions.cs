namespace OneDiff.Pdf;

/// <summary>
/// Settings of <see cref="PdfDocumentSource"/>. Immutable; derive variants with <c>with</c>. The defaults read any ordinary
/// digital PDF, keep the work bounded, and join paragraphs that run across columns and pages.
/// </summary>
public sealed record PdfSourceOptions
{
    /// <summary>The settings used when none are given.</summary>
    public static PdfSourceOptions Default { get; } = new();

    /// <summary>The password of an encrypted file, or null. Without it an encrypted file is rejected with <see cref="PdfSourceFailure.Encrypted"/>.</summary>
    public string? Password { get; init; }

    /// <summary>The most pages a document may have; more is rejected with <see cref="PdfSourceFailure.TooLarge"/>. Default 10,000.</summary>
    public int MaxPages { get; init; } = 10_000;

    /// <summary>The most characters of text a document may hold in total; more is rejected with <see cref="PdfSourceFailure.TooLarge"/>. Default 64 million.</summary>
    public long MaxCharacters { get; init; } = 64_000_000;

    /// <summary>Whether blocks the text analysis recognized as tables become table, row and cell nodes. Default true; when false they read as paragraphs, one per row.</summary>
    public bool ReadTables { get; init; } = true;

    /// <summary>Whether a block that starts with a bullet or a number becomes a list item whose label is an attribute. Default true.</summary>
    public bool ReadListItems { get; init; } = true;

    /// <summary>Whether a paragraph that runs on at the end of a column or a page is joined to its continuation. Default true.</summary>
    public bool JoinAcrossColumnsAndPages { get; init; } = true;

    /// <summary>Whether character formatting (bold, italic, font, size, colour) is read from the glyphs. Default true.</summary>
    public bool ReadFormatting { get; init; } = true;

    /// <summary>Checks the numeric limits.</summary>
    /// <exception cref="ArgumentException">A limit is not positive.</exception>
    public void Validate()
    {
        if (MaxPages <= 0 || MaxCharacters <= 0)
        {
            throw new ArgumentException("The page and character limits must be positive.");
        }
    }
}

/// <summary>Why <see cref="PdfDocumentSource"/> (or the scan source) rejected an input.</summary>
public enum PdfSourceFailure
{
    /// <summary>The bytes are not a readable PDF (damaged beyond repair, or the extraction failed).</summary>
    Malformed = 0,

    /// <summary>The file is encrypted and no password, or a wrong one, was given.</summary>
    Encrypted = 1,

    /// <summary>The document exceeds a limit of <see cref="PdfSourceOptions"/> or of the PDF reader.</summary>
    TooLarge = 2,

    /// <summary>The file is a readable PDF with pages but no text layer, so it is a scan (or a drawing): reading it as text would compare an empty document. The OCR package reads it.</summary>
    NoTextLayer = 3,
}

/// <summary>
/// Reads the reason out of the <see cref="InvalidDataException"/> that <see cref="PdfDocumentSource.Read"/> throws. The source throws the
/// exception type its contract names (a sealed framework type), and records the reason in the exception's <see cref="Exception.Data"/>.
/// </summary>
public static class PdfSourceErrors
{
    internal const string Key = "OneDiff.Pdf.Failure";

    /// <summary>The reason a PDF was rejected.</summary>
    /// <param name="error">An exception thrown by <see cref="PdfDocumentSource.Read"/>.</param>
    /// <returns>The reason, or null when the exception did not come from the PDF source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public static PdfSourceFailure? FailureOf(InvalidDataException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Data[Key] is PdfSourceFailure f ? f : null;
    }

    internal static InvalidDataException Make(PdfSourceFailure failure, string message, Exception? inner = null)
    {
        var e = new InvalidDataException(message, inner);
        e.Data[Key] = failure;
        return e;
    }
}
