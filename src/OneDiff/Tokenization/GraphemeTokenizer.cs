using System.Globalization;

namespace OneDiff.Tokenization;

/// <summary>
/// Extended grapheme clusters (Unicode UAX #29, "Grapheme Cluster Boundaries"), as implemented by the .NET text-element rules.
/// </summary>
internal static class GraphemeTokenizer
{
    public static void Tokenize(ReadOnlySpan<char> text, ref TokenWriter destination)
    {
        int i = 0;
        while (i < text.Length)
        {
            int length;
            char c = text[i];

            // Fast path. An ASCII character that is not CR always ends its cluster when the next char is below U+0300: no
            // combining mark, joiner, variation selector, prepend or spacing mark lives there (UAX #29 rules GB9, GB9a, GB9b).
            if (c < 0x80 && c != '\r' && (i + 1 == text.Length || text[i + 1] < 0x300))
            {
                length = 1;
            }
            else
            {
                length = Math.Max(1, StringInfo.GetNextTextElementLength(text.Slice(i)));
            }

            destination.Add(i, length);
            i += length;
        }
    }
}
