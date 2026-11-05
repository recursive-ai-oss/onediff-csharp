using System.Runtime.CompilerServices;
using OneDiff.Documents.Model;

namespace OneDiff.Pdf.Internal;

/// <summary>The bytes (and password) a PDF compare document was read from.</summary>
/// <param name="Bytes">The PDF's bytes.</param>
/// <param name="Password">The password it was opened with, or null.</param>
/// <param name="Sha256">The hash the marked bytes must have when the document's own source hash is not that of a PDF (an image wrapped into a PDF page); null to use the document's source hash.</param>
internal readonly record struct PdfOrigin(ReadOnlyMemory<byte> Bytes, string? Password, string? Sha256 = null);

/// <summary>
/// Remembers, for each document the PDF source returned, the bytes it came from, so that an output form (which receives only a
/// comparison) can mark the original pages. The table is weak: it keeps the bytes exactly as long as the document lives, holds no
/// state of its own, and is thread-safe.
/// </summary>
internal static class PdfOrigins
{
    private static readonly ConditionalWeakTable<CompareDocument, StrongBox<PdfOrigin>> Table = [];

    public static void Remember(CompareDocument document, ReadOnlyMemory<byte> bytes, string? password, string? sha256 = null) =>
        Table.AddOrUpdate(document, new StrongBox<PdfOrigin>(new PdfOrigin(bytes, password, sha256)));

    public static bool TryGet(CompareDocument document, out PdfOrigin origin)
    {
        if (Table.TryGetValue(document, out StrongBox<PdfOrigin>? box))
        {
            origin = box.Value;
            return true;
        }

        origin = default;
        return false;
    }
}
