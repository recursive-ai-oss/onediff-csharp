namespace OneDiff.Tokenization;

/// <summary>
/// Cuts text into tokens. The contract every <see cref="TokenKind"/> keeps: the tokens are non-empty, in order, and
/// partition the text exactly (the first starts at 0, each starts where the previous ended, the last ends at the text length), so
/// joining them reproduces the text and a diff over tokens loses nothing. Empty text has no tokens.
/// </summary>
public static class Tokenizer
{
    /// <summary>Tokenizes <paramref name="text"/> and returns the ranges in a new array.</summary>
    public static TokenRange[] Tokenize(ReadOnlySpan<char> text, TokenKind kind)
    {
        var ranges = new List<TokenRange>();
        Tokenize(text, kind, ranges);
        return ranges.ToArray();
    }

    /// <summary>
    /// Tokenizes <paramref name="text"/> and appends the ranges to <paramref name="destination"/>, which is not cleared first, so
    /// one list can be reused across calls. Throws <see cref="ArgumentOutOfRangeException"/> for an undefined <paramref name="kind"/>.
    /// </summary>
    public static void Tokenize(ReadOnlySpan<char> text, TokenKind kind, List<TokenRange> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        switch (kind)
        {
            case TokenKind.Line: LineTokenizer.Tokenize(text, destination); break;
            case TokenKind.Word: WordTokenizer.Tokenize(text, destination); break;
            case TokenKind.Character: CharacterTokenizer.Tokenize(text, destination); break;
            case TokenKind.Grapheme: GraphemeTokenizer.Tokenize(text, destination); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
