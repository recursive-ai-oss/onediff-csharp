namespace OneDiff.Documents;

/// <summary>
/// What to tell a caller whose input or output needs an adapter package. This is the only place that names other packages:
/// this package reads plain text and writes JSON and HTML, and recognizes the rest by its first bytes just well enough to
/// say which package to add.
/// </summary>
internal static class FormatHints
{
    private const string Prefix = "RecursiveAI.OneDiff.";

    /// <summary>The one piece of advice for formats this release does not read or write yet.</summary>
    private static string Coming(string what) =>
        "Support for " + what + " is coming in a 1.x release. Meanwhile, export the documents to PDF and compare the PDFs (with " + Prefix + "Pdf).";

    /// <summary>For bytes that begin like a format another package reads, or one not supported yet: what to tell the caller; otherwise null.</summary>
    public static string? MessageFor(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith("%PDF-"u8))
        {
            return "a PDF. Reference " + Prefix + "Pdf and call its Register method once at startup.";
        }

        if (b.StartsWith("PK\u0003\u0004"u8))
        {
            return "an Office Open XML or other zip-based document. " + Coming("Office documents (DOCX, XLSX and PPTX)");
        }

        if (b.StartsWith("{\\rtf"u8) || Is(b, 0xD0, 0xCF, 0x11, 0xE0))
        {
            return "a word-processing document. " + Coming("Word documents");
        }

        if (Is(b, 0x89, 0x50, 0x4E, 0x47) || Is(b, 0xFF, 0xD8, 0xFF) || b.StartsWith("II*\0"u8) || b.StartsWith("MM\0*"u8))
        {
            return "an image. Reference " + Prefix + "Ocr and call its Register method once at startup.";
        }

        return null;
    }

    private static bool Is(ReadOnlySpan<byte> bytes, params ReadOnlySpan<byte> prefix) => bytes.StartsWith(prefix);

    /// <summary>For an output extension (with its dot) that another package writes or that is not supported yet: advice; otherwise null.</summary>
    public static string? PackageForExtension(string extension) => extension switch
    {
        ".pdf" => "Reference " + Prefix + "Pdf and call its Register method once at startup.",
        ".docx" or ".docm" or ".dotx" => Coming("DOCX output"),
        ".xlsx" => Coming("XLSX output"),
        ".pptx" => Coming("PPTX output"),
        _ => null,
    };
}
