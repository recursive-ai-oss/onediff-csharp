using System.Globalization;
using System.Text;

namespace OneDiff.Tokenization;

internal static class WordTokenizer
{
    private enum Class { Word, Space, Other }

    public static void Tokenize(ReadOnlySpan<char> text, List<TokenRange> destination)
    {
        int i = 0;
        while (i < text.Length)
        {
            Rune.DecodeFromUtf16(text.Slice(i), out Rune rune, out int width);
            Class kind = Classify(rune);
            if (kind == Class.Other)
            {
                destination.Add(new TokenRange(i, width));
                i += width;
                continue;
            }

            int end = i + width;
            while (end < text.Length)
            {
                Rune.DecodeFromUtf16(text.Slice(end), out Rune next, out int nextWidth);
                if (Classify(next) != kind)
                {
                    break;
                }

                end += nextWidth;
            }

            destination.Add(new TokenRange(i, end - i));
            i = end;
        }
    }

    private static Class Classify(Rune rune)
    {
        if (Rune.IsWhiteSpace(rune))
        {
            return Class.Space;
        }

        if (Rune.IsLetterOrDigit(rune) || rune.Value == '_')
        {
            return Class.Word;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            ? Class.Word
            : Class.Other;
    }
}
