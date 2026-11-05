namespace OneDiff.Ocr.Internal;

/// <summary>What an input is, by its first bytes (never by file name).</summary>
internal enum InputKind
{
    Unknown = 0,
    Pdf,
    Png,
    Jpeg,
    Tiff,
}

internal static class InputKinds
{
    private const int PdfWindow = 1024;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Recognizes a PDF (the <c>%PDF-</c> header within the first 1024 bytes, ISO 32000-2 7.5.2), a PNG (W3C PNG 8-byte signature),
    /// a JPEG (the SOI marker followed by a marker, ISO/IEC 10918-1 B.1.1.2) or a TIFF (byte order mark and 42, TIFF 6.0 section 2).
    /// </summary>
    public static InputKind Of(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith(PngSignature))
        {
            return InputKind.Png;
        }

        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            return InputKind.Jpeg;
        }

        if (b.Length >= 8 && ((b[0] == 0x49 && b[1] == 0x49 && b[2] == 0x2A && b[3] == 0) || (b[0] == 0x4D && b[1] == 0x4D && b[2] == 0 && b[3] == 0x2A)))
        {
            return InputKind.Tiff;
        }

        return b[..Math.Min(b.Length, PdfWindow)].IndexOf("%PDF-"u8) >= 0 ? InputKind.Pdf : InputKind.Unknown;
    }
}
