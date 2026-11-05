using System.Runtime.CompilerServices;
using OneDiff.Documents.Model;

namespace OneDiff.Word.Internal;

/// <summary>The bytes and revision mode a Word compare document was read from.</summary>
internal readonly record struct WordOrigin(ReadOnlyMemory<byte> Bytes, WordRevisionMode Revisions);

/// <summary>
/// Remembers, for each document the Word source returned, the bytes and revision mode it came from, so that an output form (which
/// receives only a comparison) can reopen the document and place revisions on the elements its anchors name. The table is weak,
/// holds no state of its own, and is thread-safe.
/// </summary>
internal static class WordOrigins
{
    private static readonly ConditionalWeakTable<CompareDocument, StrongBox<WordOrigin>> Table = [];

    public static void Remember(CompareDocument document, ReadOnlyMemory<byte> bytes, WordRevisionMode revisions) =>
        Table.AddOrUpdate(document, new StrongBox<WordOrigin>(new WordOrigin(bytes, revisions)));

    public static bool TryGet(CompareDocument document, out WordOrigin origin)
    {
        if (Table.TryGetValue(document, out StrongBox<WordOrigin>? box))
        {
            origin = box.Value;
            return true;
        }

        origin = default;
        return false;
    }
}
