namespace OneDiff.Tokenization;

internal static class CharacterTokenizer
{
    public static void Tokenize(ReadOnlySpan<char> text, ref TokenWriter destination)
    {
        int i = 0;
        while (i < text.Length)
        {
            int length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            destination.Add(i, length);
            i += length;
        }
    }
}
