namespace OneDiff.Tokenization;

internal static class CharacterTokenizer
{
    public static void Tokenize(ReadOnlySpan<char> text, List<TokenRange> destination)
    {
        int i = 0;
        while (i < text.Length)
        {
            int length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            destination.Add(new TokenRange(i, length));
            i += length;
        }
    }
}
