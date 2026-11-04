namespace OneDiff.Tokenization;

/// <summary>A token's place in the text: a UTF-16 offset and length.</summary>
/// <param name="Start">Index of the first char of the token.</param>
/// <param name="Length">Number of chars in the token. Always positive for tokens a tokenizer returns.</param>
public readonly record struct TokenRange(int Start, int Length)
{
    /// <summary>Index one past the last char of the token.</summary>
    public int End => Start + Length;
}
