using OneDiff.Tokenization;

namespace OneDiff.Cleanup;

/// <summary>One side of a <see cref="TextDiff"/> as cleanup needs it: the text and its tokens.</summary>
internal readonly struct TokenText
{
    private readonly string text;
    private readonly TokenRange[]? tokens; // null: one char per token

    public TokenText(string text, TokenRange[]? tokens)
    {
        this.text = text;
        this.tokens = tokens;
    }

    public string Text => text;

    public int Count => tokens?.Length ?? text.Length;

    public ReadOnlySpan<char> Token(int index) => tokens is null ? text.AsSpan(index, 1) : text.AsSpan(tokens[index].Start, tokens[index].Length);

    /// <summary>Characters in tokens [from, to). Tokens partition the text, so this is a difference of offsets.</summary>
    public int Chars(int from, int to) => Offset(to) - Offset(from);

    /// <summary>Text offset where token <paramref name="index"/> starts; the text length for the index one past the last token.</summary>
    public int Offset(int index) => tokens is null ? index : index < tokens.Length ? tokens[index].Start : text.Length;
}

/// <summary>Compares token text the way the diff did: ordinally, or through the comparer that was used to build the script.</summary>
internal readonly struct TokenEquality
{
    private readonly IEqualityComparer<string>? comparer;

    public TokenEquality(IEqualityComparer<string>? comparer) => this.comparer = comparer;

    public bool Equal(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (comparer is null)
        {
            return a.SequenceEqual(b);
        }

        if (comparer is TokenComparer tokenComparer)
        {
            return tokenComparer.Equals(a, b);
        }

        return comparer.Equals(a.ToString(), b.ToString());
    }
}
