namespace OneDiff.Patching;

/// <summary>Why patch text was rejected.</summary>
public enum PatchErrorKind
{
    /// <summary>The input is longer than the parser accepts.</summary>
    TooLarge,

    /// <summary>The input has content but no patch in it.</summary>
    NoPatchFound,

    /// <summary>A <c>---</c> line is not followed by a <c>+++</c> line.</summary>
    MissingNewFileHeader,

    /// <summary>A hunk header (<c>@@ -a,b +c,d @@</c>) is malformed.</summary>
    InvalidHunkHeader,

    /// <summary>A number in a header does not fit in a 32-bit integer.</summary>
    NumberTooLarge,

    /// <summary>The input ends before a hunk has all the lines its header announces.</summary>
    TruncatedHunk,

    /// <summary>A line inside a hunk has no valid prefix.</summary>
    UnexpectedLine,

    /// <summary>A percent escape is malformed or does not decode to UTF-8.</summary>
    InvalidEncoding,
}

/// <summary>Where and why patch text was rejected.</summary>
/// <param name="Kind">What went wrong.</param>
/// <param name="Line">One-based line number in the patch text where it went wrong.</param>
/// <param name="Offset">Zero-based character offset in the patch text where the offending line starts.</param>
/// <param name="Message">A plain-words description.</param>
public readonly record struct PatchParseError(PatchErrorKind Kind, int Line, int Offset, string Message);

/// <summary>Thrown by the <c>Parse</c> methods when patch text is malformed. The <c>TryParse</c> forms report the same information without throwing.</summary>
public sealed class PatchFormatException : FormatException
{
    /// <summary>Creates the exception for <paramref name="error"/>.</summary>
    public PatchFormatException(PatchParseError error)
        : base($"{error.Message} (line {error.Line}, offset {error.Offset})")
    {
        Error = error;
    }

    /// <summary>What went wrong and where.</summary>
    public PatchParseError Error { get; }
}
