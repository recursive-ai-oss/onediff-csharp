using System.Runtime.CompilerServices;
using OneDiff.Documents.Model;

namespace OneDiff.Pdf.Internal;

/// <summary>The bytes (and password) a PDF compare document was read from.</summary>
internal readonly record struct PdfOrigin(ReadOnlyMemory<byte> Bytes, string? Password);

/// <summary>
/// Remembers, for each document the PDF source returned, the bytes it came from, so that an output form (which receives only a
/// comparison) can mark the original pages. The table is weak: it keeps the bytes exactly as long as the document lives, holds no
/// state of its own, and is thread-safe.
/// </summary>
internal static class PdfOrigins
{
    private static readonly ConditionalWeakTable<CompareDocument, StrongBox<PdfOrigin>> Table = [];

    public static void Remember(CompareDocument document, ReadOnlyMemory<byte> bytes, string? password) =>
        Table.AddOrUpdate(document, new StrongBox<PdfOrigin>(new PdfOrigin(bytes, password)));

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
