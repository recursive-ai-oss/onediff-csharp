namespace OneDiff.Tokenization;

/// <summary>Lines end at <c>\n</c>, <c>\r\n</c> or a lone <c>\r</c>; the terminator stays in the token.</summary>
internal static class LineTokenizer
{
    public static void Tokenize(ReadOnlySpan<char> text, ref TokenWriter destination)
    {
        int start = 0;
        while (start < text.Length)
        {
            // Vectorized search for the next terminator character.
            int found = text.Slice(start).IndexOfAny('\n', '\r');
            if (found < 0)
            {
                break;
            }

            int end = start + found + 1;
            if (text[end - 1] == '\r' && end < text.Length && text[end] == '\n')
            {
                end++;
            }

            destination.Add(start, end - start);
            start = end;
        }

        if (start < text.Length)
        {
            destination.Add(start, text.Length - start);
        }
    }
}
