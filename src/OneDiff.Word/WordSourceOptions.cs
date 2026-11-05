namespace OneDiff.Word;

/// <summary>What the source does with tracked changes (revisions) that the input document already holds.</summary>
public enum WordRevisionMode
{
    /// <summary>Every revision is accepted before the document is read: inserted text stays, deleted and moved-away text is gone, property changes keep the current properties. This is the default.</summary>
    Accepted = 0,

    /// <summary>Every revision is rejected before the document is read, so the document reads as it was before anyone changed it: inserted text is gone, deleted text is back, property changes are undone.</summary>
    AsOriginal = 1,

    /// <summary>The source refuses an input that holds any revision, with <see cref="WordSourceFailure.HasRevisions"/>, so that the caller decides.</summary>
    Refuse = 2,
}

/// <summary>
/// Settings of <see cref="WordDocumentSource"/>. Immutable; derive variants with <c>with</c>. The defaults read any ordinary document,
/// accept its tracked changes and keep the work bounded.
/// </summary>
public sealed record WordSourceOptions
{
    /// <summary>The settings used when none are given.</summary>
    public static WordSourceOptions Default { get; } = new();

    /// <summary>What to do with tracked changes already in the input. Default <see cref="WordRevisionMode.Accepted"/>.</summary>
    public WordRevisionMode Revisions { get; init; } = WordRevisionMode.Accepted;

    /// <summary>The most characters of text a document may hold in total (body, headers, notes, comments, text boxes); more is rejected with <see cref="WordSourceFailure.TooLarge"/>. Default 64 million.</summary>
    public long MaxCharacters { get; init; } = 64_000_000;

    /// <summary>The most nodes a document may produce; more is rejected with <see cref="WordSourceFailure.TooLarge"/>. Default 8 million.</summary>
    public int MaxNodes { get; init; } = 8_000_000;

    /// <summary>The largest image, in bytes, that is hashed; a larger one is rejected with <see cref="WordSourceFailure.TooLarge"/>. Default 256 MiB.</summary>
    public long MaxImageBytes { get; init; } = 256L << 20;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxNodes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxImageBytes, 1);
        if (!Enum.IsDefined(Revisions))
        {
            throw new ArgumentOutOfRangeException(nameof(Revisions), "unknown revision mode");
        }
    }
}

/// <summary>Why <see cref="WordDocumentSource"/> rejected an input.</summary>
public enum WordSourceFailure
{
    /// <summary>The bytes are not a readable word-processing package (damaged, truncated, or not a Word document).</summary>
    Malformed = 0,

    /// <summary>The file is encrypted. Encrypted packages are not read by this source, whatever the password.</summary>
    Encrypted = 1,

    /// <summary>The document exceeds a limit of <see cref="WordSourceOptions"/> or of the OneOffice reader.</summary>
    TooLarge = 2,

    /// <summary>The document holds tracked changes and <see cref="WordSourceOptions.Revisions"/> is <see cref="WordRevisionMode.Refuse"/>.</summary>
    HasRevisions = 3,
}

/// <summary>
/// Reads the reason out of the <see cref="InvalidDataException"/> that <see cref="WordDocumentSource.Read"/> throws. The source throws the
/// exception type its contract names (a sealed framework type) and records the reason in the exception's <see cref="Exception.Data"/>.
/// </summary>
public static class WordSourceErrors
{
    internal const string Key = "OneDiff.Word.Failure";

    /// <summary>The reason a document was rejected.</summary>
    /// <param name="error">An exception thrown by <see cref="WordDocumentSource.Read"/>.</param>
    /// <returns>The reason, or null when the exception did not come from the Word source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public static WordSourceFailure? FailureOf(InvalidDataException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Data[Key] is WordSourceFailure f ? f : null;
    }

    internal static InvalidDataException Make(WordSourceFailure failure, string message, Exception? inner = null)
    {
        var e = new InvalidDataException(message, inner);
        e.Data[Key] = failure;
        return e;
    }
}
