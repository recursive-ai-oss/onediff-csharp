using System.Globalization;

namespace OneDiff.Tokenization;

internal static class GraphemeTokenizer
{
    public static void Tokenize(ReadOnlySpan<char> text, List<TokenRange> destination)
    {
        int i = 0;
        while (i < text.Length)
        {
            int length = StringInfo.GetNextTextElementLength(text.Slice(i));
            destination.Add(new TokenRange(i, length));
            i += length;
        }
    }
}
