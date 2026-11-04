namespace OneDiff.Tokenization;

internal static class LineTokenizer
{
    public static void Tokenize(ReadOnlySpan<char> text, List<TokenRange> destination)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || (c == '\r' && !(i + 1 < text.Length && text[i + 1] == '\n')))
            {
                destination.Add(new TokenRange(start, i + 1 - start));
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            destination.Add(new TokenRange(start, text.Length - start));
        }
    }
}
