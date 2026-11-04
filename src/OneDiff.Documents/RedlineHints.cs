namespace OneDiff.Documents;

/// <summary>
/// What to tell a caller whose input or output needs an adapter package. This is the only place that names other packages:
/// this package reads plain text and writes JSON and HTML, and recognizes the rest by its first bytes just well enough to
/// say which package to add.
/// </summary>
internal static class FormatHints
{
    private const string Prefix = "RecursiveAI.OneDiff.";

    /// <summary>For bytes that begin like a format another package reads: a description naming the package; otherwise null.</summary>
    public static string? PackageFor(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith("%PDF-"u8))
        {
            return "a PDF (package " + Prefix + "Pdf)";
        }

        if (b.StartsWith("PK\u0003\u0004"u8))
        {
            return "an Office Open XML or other zip-based document (packages " + Prefix + "Word, " + Prefix + "Spreadsheets or " + Prefix + "Presentations)";
        }

        if (b.StartsWith("{\\rtf"u8) || Is(b, 0xD0, 0xCF, 0x11, 0xE0))
        {
            return "a word-processing document (package " + Prefix + "Word)";
        }

        if (Is(b, 0x89, 0x50, 0x4E, 0x47) || Is(b, 0xFF, 0xD8, 0xFF) || b.StartsWith("II*\0"u8) || b.StartsWith("MM\0*"u8))
        {
            return "an image (package " + Prefix + "Ocr)";
        }

        return null;
    }

    private static bool Is(ReadOnlySpan<byte> bytes, params ReadOnlySpan<byte> prefix) => bytes.StartsWith(prefix);

    /// <summary>For an output extension (with its dot) that another package writes: advice naming the package; otherwise null.</summary>
    public static string? PackageForExtension(string extension) => extension switch
    {
        ".pdf" => "Reference " + Prefix + "Pdf and call its Register method once at startup.",
        ".docx" or ".docm" or ".dotx" => "Reference " + Prefix + "Word and call its Register method once at startup.",
        ".xlsx" => "Reference " + Prefix + "Spreadsheets and call its Register method once at startup.",
        ".pptx" => "Reference " + Prefix + "Presentations and call its Register method once at startup.",
        _ => null,
    };
}
